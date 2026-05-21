using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace Crowd
{
    /// <summary>
    /// One walkable region in the scene (sidewalk, plaza, crosswalk). Agents must remain inside
    /// the union of all walkable areas — the movement system snaps them to the closest area
    /// boundary if they ever leave (Phase 2). Geometry shares the <see cref="ObstacleShape"/>
    /// enum and <see cref="ObstacleMath"/> helpers with <see cref="StaticObstacle"/>.
    ///
    /// Designer note: adjacent walkable areas (e.g. two sidewalks meeting at an intersection)
    /// should overlap by ~0.5m to avoid agents "falling between" them at the boundary.
    /// </summary>
    public struct WalkableArea : IComponentData
    {
        public ObstacleShape Shape;
        public float3 Center;
        public float3 HalfExtents;
        public float RotationY;
    }

    /// <summary>
    /// Singleton built once at startup by <see cref="Crowd.Systems.WalkableSpatialIndexSystem"/>.
    /// Holds a flat array of all walkable areas and a spatial multi-hash (cell -> area index).
    ///
    /// <see cref="HasAreas"/> is the master switch: when 0 (no <see cref="WalkableArea"/> entities
    /// were baked), downstream systems must skip the walkable constraint entirely to preserve
    /// pre-Phase-2 behavior in scenes that haven't been migrated yet.
    /// </summary>
    public struct WalkableSpatialIndex : IComponentData
    {
        public NativeArray<WalkableArea> Areas;
        public NativeParallelMultiHashMap<int, int> CellToAreaIndex;
        public float CellSize;
        public byte IsBuilt;
        public byte HasAreas;
    }
}
