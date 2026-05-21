using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

namespace Crowd.Authoring
{
    /// <summary>
    /// Marks a pedestrian crossing. Designer guideline: a crosswalk MUST overlap both the
    /// underlying <see cref="RoadAuthoring"/> AND the two adjacent <see cref="WalkableAreaAuthoring"/>
    /// slabs. The overlap is what lets the agent transition from sidewalk -> road -> sidewalk
    /// without the movement system snapping it back.
    /// </summary>
    [DisallowMultipleComponent]
    public class CrosswalkAuthoring : MonoBehaviour
    {
        [Tooltip("Box uses HalfExtents.x and HalfExtents.z. Circle uses HalfExtents.x as radius.")]
        public ObstacleShape Shape = ObstacleShape.Box;

        [Tooltip("Half-extents in local space. Typically thin along road direction (x ~ road half-width, z ~ 1.5).")]
        public Vector3 HalfExtents = new Vector3(6f, 0.1f, 1.5f);

        [Tooltip("AlwaysGreen = pedestrians may cross any time. Timed/Demand are wired in Phase 14.")]
        public CrosswalkSignal SignalType = CrosswalkSignal.AlwaysGreen;

        [Tooltip("Full cycle duration in seconds when SignalType == Timed.")]
        [Min(2f)] public float SignalCycleDuration = 12f;

        [Tooltip("Offset within the cycle, in seconds. Lets you desync multiple lights.")]
        [Min(0f)] public float SignalPhaseOffset = 0f;

        [Tooltip("Draw the crossing outline in the Scene view.")]
        public bool DrawGizmos = true;

        [Tooltip("Color used for the gizmo.")]
        public Color GizmoColor = new Color(0.95f, 0.95f, 0.95f, 0.95f);

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
                var size = new Vector3(hx * 2f, 0.18f, hz * 2f);

                Gizmos.color = GizmoColor;
                Gizmos.DrawWireCube(Vector3.zero, size);

                var fill = GizmoColor; fill.a = 0.18f;
                Gizmos.color = fill;
                Gizmos.DrawCube(Vector3.zero, size);

                // Zebra stripes: alternate full-width bars along the X axis.
                int stripes = Mathf.Max(2, Mathf.RoundToInt(hx * 2f));
                float stripeStep = (hx * 2f) / stripes;
                Gizmos.color = new Color(1f, 1f, 1f, 0.95f);
                for (int i = 0; i < stripes; i++)
                {
                    if ((i & 1) == 1) continue;
                    float x0 = -hx + i * stripeStep;
                    float x1 = x0 + stripeStep;
                    Gizmos.DrawLine(new Vector3(x0, 0.1f, -hz), new Vector3(x0, 0.1f, hz));
                    Gizmos.DrawLine(new Vector3(x1, 0.1f, -hz), new Vector3(x1, 0.1f, hz));
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

        private class Baker : Baker<CrosswalkAuthoring>
        {
            public override void Bake(CrosswalkAuthoring authoring)
            {
                var entity = GetEntity(TransformUsageFlags.None);
                AddComponent(entity, new CrosswalkZone
                {
                    Shape               = authoring.Shape,
                    Center              = authoring.transform.position,
                    HalfExtents         = new float3(
                        math.max(0.1f, authoring.HalfExtents.x),
                        math.max(0.1f, authoring.HalfExtents.y),
                        math.max(0.1f, authoring.HalfExtents.z)),
                    RotationY           = math.radians(authoring.transform.eulerAngles.y),
                    SignalType          = authoring.SignalType,
                    SignalCycleDuration = math.max(2f, authoring.SignalCycleDuration),
                    SignalPhaseOffset   = math.max(0f, authoring.SignalPhaseOffset),
                });
            }
        }
    }
}
