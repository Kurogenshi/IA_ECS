using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

namespace Crowd.Authoring
{
    /// <summary>
    /// Marks a slab of the scene as drivable road. Pedestrians are pushed back to the nearest
    /// walkable area if they ever end up here (unless they're inside an overlapping
    /// <see cref="CrosswalkAuthoring"/>). Cars (Phase 12) drive on lanes that sit on top of
    /// these zones.
    /// </summary>
    [DisallowMultipleComponent]
    public class RoadAuthoring : MonoBehaviour
    {
        [Tooltip("Box uses HalfExtents.x and HalfExtents.z. Circle uses HalfExtents.x as radius.")]
        public ObstacleShape Shape = ObstacleShape.Box;

        [Tooltip("Half-extents in local space. Box: x/z half-size. Circle: x = radius. Minimum 0.1.")]
        public Vector3 HalfExtents = new Vector3(6f, 0.1f, 20f);

        [Tooltip("Speed limit in km/h (converted to m/s at bake). Used by cars in Phase 12+.")]
        [Min(1f)] public float SpeedLimitKmh = 50f;

        [Tooltip("Number of lanes — informational, used by lane authoring in Phase 12.")]
        [Range(1, 8)] public int LaneCount = 2;

        [Tooltip("Draw the road outline in the Scene view.")]
        public bool DrawGizmos = true;

        [Tooltip("Color used for the gizmo (wireframe + translucent fill).")]
        public Color GizmoColor = new Color(0.25f, 0.25f, 0.28f, 0.9f);

        private void OnDrawGizmos()
        {
            if (!DrawGizmos) return;

            var prevMatrix = Gizmos.matrix;
            var prevColor  = Gizmos.color;

            if (Shape == ObstacleShape.Box)
            {
                var rot = Quaternion.Euler(0f, transform.eulerAngles.y, 0f);
                Gizmos.matrix = Matrix4x4.TRS(transform.position, rot, Vector3.one);

                float hx = Mathf.Max(0.1f, HalfExtents.x);
                float hz = Mathf.Max(0.1f, HalfExtents.z);
                // Thin slab, darker than walkable. Sits slightly above ground so it's visible.
                var size = new Vector3(hx * 2f, 0.15f, hz * 2f);

                Gizmos.color = GizmoColor;
                Gizmos.DrawWireCube(Vector3.zero, size);

                var fill = GizmoColor; fill.a = 0.45f;
                Gizmos.color = fill;
                Gizmos.DrawCube(Vector3.zero, size);

                // Dashed centerline to suggest a road.
                var line = new Color(0.95f, 0.85f, 0.2f, 0.9f);
                Gizmos.color = line;
                int dashes = Mathf.Max(2, Mathf.RoundToInt(hz));
                float step = (hz * 2f) / dashes;
                for (int i = 0; i < dashes; i++)
                {
                    if ((i & 1) == 1) continue;
                    float z0 = -hz + i * step + step * 0.2f;
                    float z1 = -hz + i * step + step * 0.8f;
                    Gizmos.DrawLine(new Vector3(0f, 0.08f, z0), new Vector3(0f, 0.08f, z1));
                }
            }
            else
            {
                Gizmos.matrix = prevMatrix;
                float r = Mathf.Max(0.1f, HalfExtents.x);
                Gizmos.color = GizmoColor;
                DrawCircleGizmo(transform.position, r);
            }

            Gizmos.matrix = prevMatrix;
            Gizmos.color  = prevColor;
        }

        private static void DrawCircleGizmo(Vector3 center, float radius)
        {
            const int seg = 48;
            float step = Mathf.PI * 2f / seg;
            Vector3 prev = center + new Vector3(radius, 0f, 0f);
            for (int i = 1; i <= seg; i++)
            {
                float a = i * step;
                Vector3 next = center + new Vector3(Mathf.Cos(a) * radius, 0f, Mathf.Sin(a) * radius);
                Gizmos.DrawLine(prev, next);
                prev = next;
            }
        }

        private class Baker : Baker<RoadAuthoring>
        {
            public override void Bake(RoadAuthoring authoring)
            {
                var entity = GetEntity(TransformUsageFlags.None);
                AddComponent(entity, new RoadZone
                {
                    Shape       = authoring.Shape,
                    Center      = authoring.transform.position,
                    HalfExtents = new float3(
                        math.max(0.1f, authoring.HalfExtents.x),
                        math.max(0.1f, authoring.HalfExtents.y),
                        math.max(0.1f, authoring.HalfExtents.z)),
                    RotationY   = math.radians(authoring.transform.eulerAngles.y),
                    SpeedLimit  = math.max(1f, authoring.SpeedLimitKmh) * (1f / 3.6f),
                    LaneCount   = (byte)math.clamp(authoring.LaneCount, 1, 8),
                });
            }
        }
    }
}
