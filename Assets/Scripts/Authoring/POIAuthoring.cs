using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

namespace Crowd.Authoring
{
    /// <summary>
    /// Pose on any GameObject to declare a point of interest (bench, shop window, fountain...)
    /// that agents may walk to and linger at. The POI must be registered in the
    /// <see cref="CrowdSpawnerAuthoring.POIs"/> list so the goal system can pick it.
    /// </summary>
    [DisallowMultipleComponent]
    public class POIAuthoring : MonoBehaviour
    {
        public POIType Type = POIType.Bench;

        [Tooltip("Maximum agents that can occupy this POI simultaneously.")]
        [Min(1)] public int Capacity = 4;

        [Tooltip("Agents within this distance from the POI's position consider themselves arrived.")]
        public float InteractionRadius = 1.5f;

        [Header("Dwell time on arrival (seconds)")]
        public float DwellTimeMin = 5f;
        public float DwellTimeMax = 15f;

        [Header("Gizmo")]
        public bool DrawGizmos = true;

        private static Color ColorFor(POIType type) => type switch
        {
            POIType.Bench      => new Color(0.65f, 0.40f, 0.20f, 1f),  // brown
            POIType.ShopWindow => new Color(0.25f, 0.85f, 1.00f, 1f),  // cyan
            POIType.Fountain   => new Color(0.20f, 0.45f, 1.00f, 1f),  // deep blue
            POIType.BusStop    => new Color(1.00f, 0.85f, 0.20f, 1f),  // yellow
            POIType.StreetFood => new Color(1.00f, 0.55f, 0.20f, 1f),  // orange
            _                  => Color.white,
        };

        private void OnDrawGizmos()
        {
            if (!DrawGizmos) return;

            var color = ColorFor(Type);
            var prev = Gizmos.color;

            Gizmos.color = color;
            Gizmos.DrawSphere(transform.position + Vector3.up * 0.1f, 0.35f);

            color.a = 0.15f;
            Gizmos.color = color;
            DrawCircleGizmo(transform.position, Mathf.Max(0.3f, InteractionRadius));

            Gizmos.color = prev;
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

        private class Baker : Baker<POIAuthoring>
        {
            public override void Bake(POIAuthoring authoring)
            {
                var entity = GetEntity(TransformUsageFlags.None);
                float dwellMin = math.max(0.1f, authoring.DwellTimeMin);
                float dwellMax = math.max(dwellMin, authoring.DwellTimeMax);

                AddComponent(entity, new PointOfInterest
                {
                    Type              = authoring.Type,
                    Position          = authoring.transform.position,
                    Capacity          = math.max(1, authoring.Capacity),
                    CurrentOccupancy  = 0,
                    InteractionRadius = math.max(0.3f, authoring.InteractionRadius),
                    DwellTimeRange    = new float2(dwellMin, dwellMax),
                });
            }
        }
    }
}
