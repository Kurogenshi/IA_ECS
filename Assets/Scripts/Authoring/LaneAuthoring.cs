using System.Collections.Generic;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

namespace Crowd.Authoring
{
    /// <summary>
    /// Oriented lane for vehicle traffic (Phase 12). Unlike <see cref="PathAuthoring"/>
    /// (closed loop for pedestrians), a lane is directional — cars travel from the first
    /// node to the last, then jump to one of the connected lanes.
    ///
    /// Place empty GameObjects as children to define waypoints, OR fill the Waypoints list
    /// manually. The <see cref="ConnectionsAtEnd"/> list wires intersections: each connected
    /// lane becomes a possible successor at the end of this lane.
    /// </summary>
    [DisallowMultipleComponent]
    public class LaneAuthoring : MonoBehaviour
    {
        [Tooltip("Manual waypoint list. If empty, all direct children of this GameObject are used in hierarchy order.")]
        public List<Transform> Waypoints = new List<Transform>();

        [Tooltip("Lanes that may follow this one at intersections. Picked at random on lane transition (Phase 12).")]
        public List<LaneAuthoring> ConnectionsAtEnd = new List<LaneAuthoring>();

        [Tooltip("Speed cap on this lane in km/h (converted to m/s at bake).")]
        [Min(1f)] public float SpeedLimitKmh = 50f;

        [Tooltip("Draw the lane in the Scene view.")]
        public bool DrawGizmos = true;

        [Tooltip("Color used for the lane gizmo.")]
        public Color GizmoColor = new Color(0.95f, 0.4f, 0.95f, 1f);

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
                Gizmos.DrawSphere(wps[i].position + Vector3.up * 0.2f, 0.3f);

                if (i + 1 < wps.Count && wps[i + 1] != null)
                {
                    var a = wps[i].position + Vector3.up * 0.2f;
                    var b = wps[i + 1].position + Vector3.up * 0.2f;
                    Gizmos.DrawLine(a, b);

                    // Direction arrow at segment midpoint.
                    Vector3 mid = (a + b) * 0.5f;
                    Vector3 dir = (b - a).normalized;
                    Vector3 left = Vector3.Cross(Vector3.up, dir) * 0.4f;
                    Gizmos.DrawLine(mid, mid - dir * 0.6f + left);
                    Gizmos.DrawLine(mid, mid - dir * 0.6f - left);
                }
            }

            // Connections drawn as dashed lines from the last node to each next lane's first node.
            if (ConnectionsAtEnd == null || wps.Count == 0) return;
            var last = wps[wps.Count - 1];
            if (last == null) return;

            var oldColor = Gizmos.color;
            Gizmos.color = new Color(GizmoColor.r, GizmoColor.g, GizmoColor.b, 0.6f);
            foreach (var next in ConnectionsAtEnd)
            {
                if (next == null) continue;
                var nextWps = next.ResolveWaypoints();
                if (nextWps.Count == 0 || nextWps[0] == null) continue;
                Vector3 a = last.position + Vector3.up * 0.2f;
                Vector3 b = nextWps[0].position + Vector3.up * 0.2f;
                int dashes = 8;
                for (int i = 0; i < dashes; i += 2)
                {
                    Vector3 p0 = Vector3.Lerp(a, b, i / (float)dashes);
                    Vector3 p1 = Vector3.Lerp(a, b, (i + 1) / (float)dashes);
                    Gizmos.DrawLine(p0, p1);
                }
            }
            Gizmos.color = oldColor;
        }

        private class Baker : Baker<LaneAuthoring>
        {
            public override void Bake(LaneAuthoring authoring)
            {
                var entity = GetEntity(TransformUsageFlags.None);
                AddComponent(entity, new LaneTag
                {
                    MaxSpeed = math.max(1f, authoring.SpeedLimitKmh) * (1f / 3.6f),
                });

                var nodeBuf = AddBuffer<LaneNode>(entity);
                var wps = authoring.Waypoints != null && authoring.Waypoints.Count > 0
                    ? authoring.Waypoints
                    : null;

                if (wps == null)
                {
                    foreach (Transform child in authoring.transform)
                    {
                        DependsOn(child);
                        nodeBuf.Add(new LaneNode { Position = child.position });
                    }
                }
                else
                {
                    foreach (var t in wps)
                    {
                        if (t == null) continue;
                        DependsOn(t);
                        nodeBuf.Add(new LaneNode { Position = t.position });
                    }
                }

                var connBuf = AddBuffer<LaneConnection>(entity);
                if (authoring.ConnectionsAtEnd != null)
                {
                    foreach (var next in authoring.ConnectionsAtEnd)
                    {
                        if (next == null) continue;
                        connBuf.Add(new LaneConnection
                        {
                            NextLane = GetEntity(next, TransformUsageFlags.None),
                        });
                    }
                }
            }
        }
    }
}
