using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;

namespace Crowd.Systems
{
    /// <summary>
    /// Phase 12 — drives each car towards the next node of its current lane. On reaching the
    /// last segment it picks a random successor from the lane's <see cref="LaneConnection"/>
    /// buffer and resets to node 0 of that lane. No collision avoidance yet (Phase 13).
    ///
    /// Sets <see cref="CarMovement.TargetSpeed"/> = <see cref="LaneFollower.LaneMaxSpeed"/>
    /// and <see cref="CarMovement.Forward"/> = direction towards the next node. The actual
    /// integration of speed -> position is done by <see cref="CarMovementSystem"/> which runs after.
    /// </summary>
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateBefore(typeof(CarMovementSystem))]
    public partial struct CarLaneFollowingSystem : ISystem
    {
        private uint _frame;

        public void OnCreate(ref SystemState state)
        {
            _frame = 0;
        }

        public void OnUpdate(ref SystemState state)
        {
            _frame++;
            new LaneFollowJob
            {
                NodeLookup       = SystemAPI.GetBufferLookup<LaneNode>(true),
                ConnectionLookup = SystemAPI.GetBufferLookup<LaneConnection>(true),
                LaneTagLookup    = SystemAPI.GetComponentLookup<LaneTag>(true),
                Frame            = _frame,
            }.ScheduleParallel();
        }

        [BurstCompile]
        [WithAll(typeof(CarTag))]
        private partial struct LaneFollowJob : IJobEntity
        {
            [ReadOnly] public BufferLookup<LaneNode> NodeLookup;
            [ReadOnly] public BufferLookup<LaneConnection> ConnectionLookup;
            [ReadOnly] public ComponentLookup<LaneTag> LaneTagLookup;
            public uint Frame;

            private void Execute(Entity entity, ref LaneFollower follower, ref CarMovement movement, in LocalTransform xform)
            {
                if (follower.CurrentLane == Entity.Null || !NodeLookup.HasBuffer(follower.CurrentLane))
                {
                    movement.TargetSpeed = 0f;
                    return;
                }

                var nodes = NodeLookup[follower.CurrentLane];
                if (nodes.Length < 2)
                {
                    movement.TargetSpeed = 0f;
                    return;
                }

                // Clamp NodeIndex defensively in case the spawner pre-positioned us.
                if (follower.NodeIndex < 0)                follower.NodeIndex = 0;
                if (follower.NodeIndex > nodes.Length - 2) follower.NodeIndex = nodes.Length - 2;

                float3 pos    = xform.Position; pos.y = 0f;
                float3 segEnd = nodes[follower.NodeIndex + 1].Position; segEnd.y = 0f;
                float3 toEnd  = segEnd - pos;
                float distToEnd = math.length(toEnd);

                // Arrival threshold: a couple of meters or 1.5× car length. Use a constant
                // since CarTypeData isn't passed here; ~2m is fine for typical city blocks.
                const float arriveDist = 2.0f;

                if (distToEnd < arriveDist)
                {
                    // Advance to next segment, or transition to a successor lane at the end.
                    if (follower.NodeIndex < nodes.Length - 2)
                    {
                        follower.NodeIndex++;
                    }
                    else
                    {
                        Entity nextLane = PickNextLane(follower.CurrentLane, entity, Frame);
                        if (nextLane != Entity.Null && NodeLookup.HasBuffer(nextLane))
                        {
                            var nextNodes = NodeLookup[nextLane];
                            if (nextNodes.Length >= 2)
                            {
                                follower.CurrentLane  = nextLane;
                                follower.NodeIndex    = 0;
                                follower.LaneMaxSpeed = LaneTagLookup.HasComponent(nextLane)
                                    ? LaneTagLookup[nextLane].MaxSpeed
                                    : follower.LaneMaxSpeed;
                            }
                        }
                        else
                        {
                            // Dead-end: brake to a stop. Phase 13 will handle U-turns / despawn.
                            movement.TargetSpeed = 0f;
                            return;
                        }
                    }

                    // Refresh segment.
                    nodes  = NodeLookup[follower.CurrentLane];
                    segEnd = nodes[follower.NodeIndex + 1].Position; segEnd.y = 0f;
                    toEnd  = segEnd - pos;
                }

                if (math.lengthsq(toEnd) < 1e-6f)
                {
                    movement.TargetSpeed = 0f;
                    return;
                }

                movement.Forward     = math.normalize(toEnd);
                movement.TargetSpeed = follower.LaneMaxSpeed;
            }

            /// <summary>Pick one of the connected next lanes deterministically (seeded by entity index + frame).</summary>
            private Entity PickNextLane(Entity currentLane, Entity carEntity, uint frame)
            {
                if (!ConnectionLookup.HasBuffer(currentLane)) return Entity.Null;
                var conns = ConnectionLookup[currentLane];
                if (conns.Length == 0) return Entity.Null;
                if (conns.Length == 1) return conns[0].NextLane;

                uint h = (uint)carEntity.Index * 2654435761u + frame * 0x9E3779B1u;
                int  idx = (int)(h % (uint)conns.Length);
                return conns[idx].NextLane;
            }
        }
    }
}
