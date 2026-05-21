using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

namespace Crowd.Authoring
{
    /// <summary>
    /// Place on any GameObject to mark a forbidden zone or static obstacle (building, barrier, road).
    /// Agents will be repelled out of it by the steering system and clamped to its boundary by the
    /// movement system. The obstacle's world position and Y rotation are read from the Transform;
    /// half-extents are local-space half-sizes (Box) or radius in X (Circle).
    /// </summary>
    [DisallowMultipleComponent]
    public class ObstacleAuthoring : MonoBehaviour
    {
        [Tooltip("Box uses HalfExtents.x and HalfExtents.z. Circle uses HalfExtents.x as radius.")]
        public ObstacleShape Shape = ObstacleShape.Box;

        [Tooltip("Half-extents in local space. Box: x/z half-size. Circle: x = radius. Minimum 0.05.")]
        public Vector3 HalfExtents = new Vector3(1f, 1f, 1f);

        [Tooltip("Draw the obstacle outline in the Scene view.")]
        public bool DrawGizmos = true;

        [Tooltip("Color used for the gizmo (wireframe + translucent fill).")]
        public Color GizmoColor = new Color(1f, 0.25f, 0.15f, 0.85f);

        private void OnDrawGizmos()
        {
            if (!DrawGizmos) return;

            var prevMatrix = Gizmos.matrix;
            var prevColor  = Gizmos.color;

            if (Shape == ObstacleShape.Box)
            {
                var rot = Quaternion.Euler(0f, transform.eulerAngles.y, 0f);
                Gizmos.matrix = Matrix4x4.TRS(transform.position, rot, Vector3.one);

                float hx = Mathf.Max(0.05f, HalfExtents.x);
                float hz = Mathf.Max(0.05f, HalfExtents.z);
                var size = new Vector3(hx * 2f, 2f, hz * 2f);

                Gizmos.color = GizmoColor;
                Gizmos.DrawWireCube(Vector3.zero, size);

                var fill = GizmoColor; fill.a = 0.18f;
                Gizmos.color = fill;
                Gizmos.DrawCube(Vector3.zero, size);
            }
            else
            {
                Gizmos.matrix = prevMatrix;
                float r = Mathf.Max(0.05f, HalfExtents.x);
                Gizmos.color = GizmoColor;
                DrawCircleGizmo(transform.position, r);

                var fill = GizmoColor; fill.a = 0.18f;
                Gizmos.color = fill;
                DrawCircleGizmo(transform.position, r * 0.98f);
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

        private class Baker : Baker<ObstacleAuthoring>
        {
            public override void Bake(ObstacleAuthoring authoring)
            {
                var entity = GetEntity(TransformUsageFlags.None);
                AddComponent(entity, new StaticObstacle
                {
                    Shape       = authoring.Shape,
                    Center      = authoring.transform.position,
                    HalfExtents = new float3(
                        math.max(0.05f, authoring.HalfExtents.x),
                        math.max(0.05f, authoring.HalfExtents.y),
                        math.max(0.05f, authoring.HalfExtents.z)),
                    RotationY   = math.radians(authoring.transform.eulerAngles.y),
                });
            }
        }
    }
}
