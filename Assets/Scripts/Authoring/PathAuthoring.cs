using System.Collections.Generic;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

namespace Crowd.Authoring
{
    /// <summary>
    /// Defines a closed-loop path. Drop empty GameObjects as children and reference them here,
    /// or use the auto-pick of direct children.
    /// </summary>
    [DisallowMultipleComponent]
    public class PathAuthoring : MonoBehaviour
    {
        [Tooltip("Manual waypoint list. If empty, all direct children of this GameObject are used in hierarchy order.")]
        public List<Transform> Waypoints = new List<Transform>();

        [Tooltip("Draw the path in the Scene view.")]
        public bool DrawGizmos = true;

        [Tooltip("Color used when drawing the path gizmo.")]
        public Color GizmoColor = new Color(0.2f, 1f, 0.9f, 1f);

        private List<Transform> ResolveWaypoints()
        {
            if (Waypoints != null && Waypoints.Count > 0) return Waypoints;
            var list = new List<Transform>();
            foreach (Transform child in transform) list.Add(child);
            return list;
        }

        private void OnDrawGizmos()
        {
            if (!DrawGizmos) return;
            var wps = ResolveWaypoints();
            if (wps.Count == 0) return;

            Gizmos.color = GizmoColor;
            for (int i = 0; i < wps.Count; i++)
            {
                if (wps[i] == null) continue;
                Gizmos.DrawSphere(wps[i].position, 0.25f);
                int next = (i + 1) % wps.Count;
                if (wps[next] != null) Gizmos.DrawLine(wps[i].position, wps[next].position);
            }
        }

        private class Baker : Baker<PathAuthoring>
        {
            public override void Bake(PathAuthoring authoring)
            {
                var entity = GetEntity(TransformUsageFlags.None);
                var buffer = AddBuffer<Waypoint>(entity);
                var wps = authoring.Waypoints != null && authoring.Waypoints.Count > 0
                    ? authoring.Waypoints
                    : null;

                if (wps == null)
                {
                    foreach (Transform child in authoring.transform)
                    {
                        DependsOn(child);
                        buffer.Add(new Waypoint { Position = child.position });
                    }
                }
                else
                {
                    foreach (var t in wps)
                    {
                        if (t == null) continue;
                        DependsOn(t);
                        buffer.Add(new Waypoint { Position = t.position });
                    }
                }
            }
        }
    }
}
