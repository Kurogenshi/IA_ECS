using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace Crowd
{
    public enum ObstacleShape : byte
    {
        Box = 0,
        Circle = 1,
    }

    /// <summary>
    /// One static obstacle in the scene. Stored as an IComponentData on a per-obstacle entity
    /// (one entity per ObstacleAuthoring at bake time). At runtime,
    /// <see cref="ObstacleSpatialIndexSystem"/> reads all of these and packs them into the
    /// <see cref="ObstacleSpatialIndex"/> singleton for fast spatial queries.
    /// </summary>
    public struct StaticObstacle : IComponentData
    {
        public ObstacleShape Shape;
        /// <summary>World-space center on the XZ plane. Y is unused for collision but kept for gizmos.</summary>
        public float3 Center;
        /// <summary>Box: x/z half-size (y ignored). Circle: x = radius.</summary>
        public float3 HalfExtents;
        /// <summary>Rotation around the Y axis, radians. Only relevant for Box.</summary>
        public float RotationY;
    }

    /// <summary>
    /// Singleton component built once at startup by <see cref="ObstacleSpatialIndexSystem"/>.
    /// Carries a flat array of all obstacles plus a spatial multi-hash mapping cell -> obstacle index.
    /// Both native containers are owned by the index system and disposed in its OnDestroy.
    /// </summary>
    public struct ObstacleSpatialIndex : IComponentData
    {
        public NativeArray<StaticObstacle> Obstacles;
        public NativeParallelMultiHashMap<int, int> CellToObstacleIndex;
        public float CellSize;
        /// <summary>1 once the index has been built. Jobs should still check Obstacles.Length > 0 before iterating.</summary>
        public byte IsBuilt;
    }

    /// <summary>
    /// XZ math helpers for any oriented Box / Circle shape. Used by static obstacles (Phase 1)
    /// and walkable areas (Phase 2). All operations ignore Y; the simulation is planar for now.
    /// </summary>
    public static class ObstacleMath
    {
        /// <summary>
        /// Returns the closest point on (or inside) the obstacle to <paramref name="point"/>.
        /// <paramref name="isInside"/> is true when the point is strictly inside.
        /// <paramref name="signedDistance"/> is positive when outside, 0 on boundary, negative when inside
        /// (penetration depth as a negative value).
        /// </summary>
        public static float3 ClosestPoint(float3 point, in StaticObstacle obs, out bool isInside, out float signedDistance)
            => ClosestPointOnShape(point, obs.Shape, obs.Center, obs.HalfExtents, obs.RotationY, out isInside, out signedDistance);

        /// <summary>World-space axis-aligned bounding box on the XZ plane.</summary>
        public static void WorldAABB(in StaticObstacle obs, out float3 min, out float3 max)
            => WorldAABBOfShape(obs.Shape, obs.Center, obs.HalfExtents, obs.RotationY, out min, out max);

        /// <summary>
        /// Primitive-args variant of <see cref="ClosestPoint(float3, in StaticObstacle, out bool, out float)"/>.
        /// Lets other component types (e.g. <c>WalkableArea</c>) share the same geometry without depending on <see cref="StaticObstacle"/>.
        /// </summary>
        public static float3 ClosestPointOnShape(
            float3 point, ObstacleShape shape, float3 center, float3 halfExtents, float rotationY,
            out bool isInside, out float signedDistance)
        {
            if (shape == ObstacleShape.Circle)
            {
                float3 diff = point - center;
                diff.y = 0f;
                float distSq = math.lengthsq(diff);
                float r = math.max(halfExtents.x, 1e-4f);

                if (distSq < 1e-8f)
                {
                    isInside = true;
                    signedDistance = -r;
                    return center + new float3(r, 0f, 0f);
                }

                float dist = math.sqrt(distSq);
                isInside = dist < r;
                signedDistance = dist - r;
                float3 onCircle = center + diff * (r / dist);
                onCircle.y = center.y;
                return onCircle;
            }

            // Box: transform world point to local frame (rotate by -rotationY around Y).
            float c = math.cos(rotationY);
            float s = math.sin(rotationY);
            float3 d = point - center;
            float lx =  d.x * c + d.z * s;
            float lz = -d.x * s + d.z * c;

            float hx = math.max(halfExtents.x, 1e-4f);
            float hz = math.max(halfExtents.z, 1e-4f);

            float cx = math.clamp(lx, -hx, hx);
            float cz = math.clamp(lz, -hz, hz);

            float dx = math.abs(lx) - hx;
            float dz = math.abs(lz) - hz;
            isInside = dx <= 0f && dz <= 0f;

            if (isInside)
            {
                if (dx > dz)
                {
                    cx = lx >= 0f ? hx : -hx;
                    signedDistance = dx;
                }
                else
                {
                    cz = lz >= 0f ? hz : -hz;
                    signedDistance = dz;
                }
            }
            else
            {
                float exX = math.max(dx, 0f);
                float exZ = math.max(dz, 0f);
                signedDistance = math.sqrt(exX * exX + exZ * exZ);
            }

            float wx = cx * c - cz * s;
            float wz = cx * s + cz * c;
            return new float3(center.x + wx, center.y, center.z + wz);
        }

        /// <summary>Primitive-args variant of <see cref="WorldAABB(in StaticObstacle, out float3, out float3)"/>.</summary>
        public static void WorldAABBOfShape(
            ObstacleShape shape, float3 center, float3 halfExtents, float rotationY,
            out float3 min, out float3 max)
        {
            if (shape == ObstacleShape.Circle)
            {
                float r = math.max(halfExtents.x, 1e-4f);
                min = new float3(center.x - r, center.y, center.z - r);
                max = new float3(center.x + r, center.y, center.z + r);
                return;
            }

            float c = math.abs(math.cos(rotationY));
            float s = math.abs(math.sin(rotationY));
            float hx = math.max(halfExtents.x, 1e-4f);
            float hz = math.max(halfExtents.z, 1e-4f);
            float exX = c * hx + s * hz;
            float exZ = s * hx + c * hz;

            min = new float3(center.x - exX, center.y, center.z - exZ);
            max = new float3(center.x + exX, center.y, center.z + exZ);
        }
    }
}
