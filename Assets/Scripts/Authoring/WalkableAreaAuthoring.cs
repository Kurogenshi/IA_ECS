using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

namespace Crowd.Authoring
{
    /// <summary>
    /// Place on a GameObject to declare a region of the scene where agents may walk
    /// (sidewalk, plaza, crosswalk). The movement system enforces that every agent stays
    /// inside the union of all WalkableArea entities; agents that leave are snapped back to
    /// the nearest boundary (Phase 2).
    ///
    /// Designer tip: at intersections, make two adjacent areas overlap by ~0.5m so agents at
    /// the seam don't briefly fall "outside everything".
    /// </summary>
    [DisallowMultipleComponent]
    public class WalkableAreaAuthoring : MonoBehaviour
    {
        [Tooltip("Box uses HalfExtents.x and HalfExtents.z. Circle uses HalfExtents.x as radius.")]
        public ObstacleShape Shape = ObstacleShape.Box;

        [Tooltip("Half-extents in local space. Box: x/z half-size. Circle: x = radius. Minimum 0.1.")]
        public Vector3 HalfExtents = new Vector3(4f, 0.1f, 4f);

        [Tooltip("Draw the area outline in the Scene view.")]
        public bool DrawGizmos = true;

        [Tooltip("Color used for the gizmo (wireframe + translucent fill).")]
        public Color GizmoColor = new Color(0.3f, 1f, 0.45f, 0.85f);

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
                // Flat-ish slab to make walkable zones visually distinct from obstacle boxes.
                var size = new Vector3(hx * 2f, 0.2f, hz * 2f);

                Gizmos.color = GizmoColor;
                Gizmos.DrawWireCube(Vector3.zero, size);

                var fill = GizmoColor; fill.a = 0.15f;
                Gizmos.color = fill;
                Gizmos.DrawCube(Vector3.zero, size);
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

        private class Baker : Baker<WalkableAreaAuthoring>
        {
            public override void Bake(WalkableAreaAuthoring authoring)
            {
                var entity = GetEntity(TransformUsageFlags.None);
                AddComponent(entity, new WalkableArea
                {
                    Shape       = authoring.Shape,
                    Center      = authoring.transform.position,
                    HalfExtents = new float3(
                        math.max(0.1f, authoring.HalfExtents.x),
                        math.max(0.1f, authoring.HalfExtents.y),
                        math.max(0.1f, authoring.HalfExtents.z)),
                    RotationY   = math.radians(authoring.transform.eulerAngles.y),
                });
            }
        }
    }
}
