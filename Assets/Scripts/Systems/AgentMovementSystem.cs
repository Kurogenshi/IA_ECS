using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;

namespace Crowd.Systems
{
    /// <summary>
    /// Applies velocity, then enforces hard constraints in this order:
    /// <list type="number">
    /// <item>Static-obstacle pushout (Phase 1): two passes to handle corners where ejecting
    /// from one obstacle pushes the agent into another.</item>
    /// <item>Road pushout (Phase 11): if the agent is on a road AND not inside any crosswalk,
    /// snap it back to the closest walkable area.</item>
    /// <item>Walkable-area snap (Phase 2): if the agent ended up outside every walkable area
    /// (and not on a crosswalk), snap it to the closest area's boundary and kill the outward
    /// component of velocity.</item>
    /// <item>One more obstacle pushout pass: catches the rare case where the walkable snap
    /// nudges the agent back into an obstacle.</item>
    /// </list>
    /// Finally rotates the agent to face its movement direction.
    /// </summary>
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(AgentSteeringSystem))]
    public partial struct AgentMovementSystem : ISystem
    {
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<SpawnerConfig>();
            state.RequireForUpdate<ObstacleSpatialIndex>();
            state.RequireForUpdate<WalkableSpatialIndex>();
            state.RequireForUpdate<RoadSpatialIndex>();
            state.RequireForUpdate<CrosswalkSpatialIndex>();
        }

        public void OnUpdate(ref SystemState state)
        {
            var obstacleIndex  = SystemAPI.GetSingleton<ObstacleSpatialIndex>();
            var walkableIndex  = SystemAPI.GetSingleton<WalkableSpatialIndex>();
            var roadIndex      = SystemAPI.GetSingleton<RoadSpatialIndex>();
            var crosswalkIndex = SystemAPI.GetSingleton<CrosswalkSpatialIndex>();

            new MovementJob
            {
                DeltaTime         = SystemAPI.Time.DeltaTime,
                Obstacles         = obstacleIndex.Obstacles,
                ObstacleCellMap   = obstacleIndex.CellToObstacleIndex,
                ObstacleCellSize  = obstacleIndex.CellSize,
                WalkableAreas     = walkableIndex.Areas,
                WalkableCellMap   = walkableIndex.CellToAreaIndex,
                WalkableCellSize  = walkableIndex.CellSize,
                EnforceWalkable   = walkableIndex.HasAreas,
                Roads             = roadIndex.Roads,
                RoadCellMap       = roadIndex.CellToRoadIndex,
                RoadCellSize      = roadIndex.CellSize,
                EnforceRoads      = roadIndex.HasRoads,
                Crosswalks        = crosswalkIndex.Crosswalks,
                CrosswalkCellMap  = crosswalkIndex.CellToCrosswalkIndex,
                CrosswalkCellSize = crosswalkIndex.CellSize,
                HasCrosswalks     = crosswalkIndex.HasCrosswalks,
            }.ScheduleParallel();
        }

        [BurstCompile]
        [WithAll(typeof(AgentTag))]
        private partial struct MovementJob : IJobEntity
        {
            public float DeltaTime;

            [ReadOnly] public NativeArray<StaticObstacle> Obstacles;
            [ReadOnly] public NativeParallelMultiHashMap<int, int> ObstacleCellMap;
            public float ObstacleCellSize;

            [ReadOnly] public NativeArray<WalkableArea> WalkableAreas;
            [ReadOnly] public NativeParallelMultiHashMap<int, int> WalkableCellMap;
            public float WalkableCellSize;
            public byte EnforceWalkable;

            [ReadOnly] public NativeArray<RoadZone> Roads;
            [ReadOnly] public NativeParallelMultiHashMap<int, int> RoadCellMap;
            public float RoadCellSize;
            public byte EnforceRoads;

            [ReadOnly] public NativeArray<CrosswalkZone> Crosswalks;
            [ReadOnly] public NativeParallelMultiHashMap<int, int> CrosswalkCellMap;
            public float CrosswalkCellSize;
            public byte HasCrosswalks;

            private void Execute(ref LocalTransform transform, ref AgentMovement movement)
            {
                float3 pos = transform.Position + movement.Velocity * DeltaTime;
                pos.y = 0f;

                if (Obstacles.Length > 0)
                {
                    pos = Pushout(pos, ref movement);
                    pos = Pushout(pos, ref movement);
                }

                // Phase 11: if the pedestrian ended up on a road slab AND is not currently
                // inside a crosswalk, snap them back to the closest walkable area. Crosswalks
                // (when present) act as "holes" through the road for foot traffic.
                bool onCrosswalk = HasCrosswalks == 1 && Crosswalks.Length > 0 && IsInsideAnyCrosswalk(pos);

                if (EnforceRoads == 1 && Roads.Length > 0 && !onCrosswalk)
                {
                    pos = PushoutFromRoad(pos, ref movement);
                }

                if (EnforceWalkable == 1 && WalkableAreas.Length > 0 && !onCrosswalk)
                {
                    pos = ConstrainToWalkable(pos, ref movement);

                    // The walkable snap may have re-entered an obstacle; one more pushout pass
                    // resolves that. Pushout cost is local (9 cells) so this is cheap.
                    if (Obstacles.Length > 0)
                    {
                        pos = Pushout(pos, ref movement);
                    }
                }

                transform.Position = pos;

                // Smoothed yaw: snap-rotation reads every micro-oscillation of velocity and
                // flickers the agent's facing when steering wrestles with constraints. A slerp
                // toward the target rotation absorbs sub-frame jitter while still tracking
                // genuine direction changes within ~150ms. Speed gate avoids spinning on the spot
                // when the agent is essentially stopped (POI interaction, stalled, etc.).
                float speedSq = math.lengthsq(movement.Velocity);
                if (speedSq > 0.04f)
                {
                    float angle = math.atan2(movement.Velocity.x, movement.Velocity.z);
                    quaternion target = quaternion.RotateY(angle);
                    transform.Rotation = math.slerp(transform.Rotation, target, math.saturate(DeltaTime * 10f));
                }
            }

            private float3 Pushout(float3 pos, ref AgentMovement movement)
            {
                int2 cell = SpatialHashUtil.Cell(pos, ObstacleCellSize);
                for (int dx = -1; dx <= 1; dx++)
                {
                    for (int dz = -1; dz <= 1; dz++)
                    {
                        int hash = SpatialHashUtil.HashCell(new int2(cell.x + dx, cell.y + dz));
                        if (ObstacleCellMap.TryGetFirstValue(hash, out int obsIdx, out var it))
                        {
                            do
                            {
                                var obs = Obstacles[obsIdx];
                                float3 closest = ObstacleMath.ClosestPoint(pos, obs, out bool inside, out _);
                                if (inside)
                                {
                                    float3 normal = pos - closest;
                                    normal.y = 0f;
                                    if (math.lengthsq(normal) < 1e-6f)
                                    {
                                        normal = new float3(1f, 0f, 0f);
                                    }
                                    else
                                    {
                                        normal = math.normalize(normal);
                                    }

                                    pos = closest + normal * 0.05f;
                                    pos.y = 0f;

                                    float vDotN = math.dot(movement.Velocity, normal);
                                    if (vDotN < 0f)
                                    {
                                        movement.Velocity -= normal * vDotN;
                                    }
                                }
                            } while (ObstacleCellMap.TryGetNextValue(out obsIdx, ref it));
                        }
                    }
                }
                return pos;
            }

            /// <summary>
            /// If <paramref name="pos"/> is inside any walkable area, leave it alone. Otherwise snap to
            /// the boundary of the closest walkable area (smallest signed-distance), nudged 0.05m
            /// inward so the next frame's inside-test classifies it as inside. Kills the outward
            /// component of velocity so the agent stops fighting the constraint.
            /// </summary>
            private float3 ConstrainToWalkable(float3 pos, ref AgentMovement movement)
            {
                int2 cell = SpatialHashUtil.Cell(pos, WalkableCellSize);
                bool insideAny = false;
                float bestDist = float.MaxValue;
                int bestIdx = -1;

                for (int dx = -1; dx <= 1 && !insideAny; dx++)
                {
                    for (int dz = -1; dz <= 1 && !insideAny; dz++)
                    {
                        int hash = SpatialHashUtil.HashCell(new int2(cell.x + dx, cell.y + dz));
                        if (WalkableCellMap.TryGetFirstValue(hash, out int aIdx, out var it))
                        {
                            do
                            {
                                var area = WalkableAreas[aIdx];
                                ObstacleMath.ClosestPointOnShape(pos, area.Shape, area.Center, area.HalfExtents, area.RotationY,
                                    out bool isInside, out float signedDist);
                                if (isInside)
                                {
                                    insideAny = true;
                                    break;
                                }
                                if (signedDist < bestDist)
                                {
                                    bestDist = signedDist;
                                    bestIdx = aIdx;
                                }
                            } while (WalkableCellMap.TryGetNextValue(out aIdx, ref it));
                        }
                    }
                }

                if (insideAny) return pos;

                // Fallback: agent is far from any cell-indexed area. Brute-force the full list.
                // Rare in normal play (would mean the agent strayed > ~WalkableCellSize from any zone).
                if (bestIdx == -1)
                {
                    for (int i = 0; i < WalkableAreas.Length; i++)
                    {
                        var area = WalkableAreas[i];
                        ObstacleMath.ClosestPointOnShape(pos, area.Shape, area.Center, area.HalfExtents, area.RotationY,
                            out bool isInside, out float signedDist);
                        if (isInside) return pos;
                        if (signedDist < bestDist) { bestDist = signedDist; bestIdx = i; }
                    }
                    if (bestIdx == -1) return pos;
                }

                var snapArea = WalkableAreas[bestIdx];
                float3 closestOnBoundary = ObstacleMath.ClosestPointOnShape(pos, snapArea.Shape, snapArea.Center,
                    snapArea.HalfExtents, snapArea.RotationY, out _, out _);

                // inward = from agent (outside) toward closest point on boundary; continuing past it
                // by 0.05m places the agent just inside the area.
                float3 inward = closestOnBoundary - pos;
                inward.y = 0f;
                float inwardLenSq = math.lengthsq(inward);

                if (inwardLenSq > 1e-6f)
                {
                    inward = math.normalize(inward);
                    pos = closestOnBoundary + inward * 0.05f;
                    pos.y = 0f;

                    float3 outward = -inward;
                    float vDotOut = math.dot(movement.Velocity, outward);
                    if (vDotOut > 0f)
                    {
                        movement.Velocity -= outward * vDotOut;
                    }
                }
                else
                {
                    pos = closestOnBoundary;
                    pos.y = 0f;
                }

                return pos;
            }

            /// <summary>
            /// Returns true if <paramref name="pos"/> lies inside any crosswalk in the spatial index.
            /// Used to exempt pedestrians from road pushout and walkable snap while crossing.
            /// </summary>
            private bool IsInsideAnyCrosswalk(float3 pos)
            {
                int2 cell = SpatialHashUtil.Cell(pos, CrosswalkCellSize);
                for (int dx = -1; dx <= 1; dx++)
                {
                    for (int dz = -1; dz <= 1; dz++)
                    {
                        int hash = SpatialHashUtil.HashCell(new int2(cell.x + dx, cell.y + dz));
                        if (CrosswalkCellMap.TryGetFirstValue(hash, out int cwIdx, out var it))
                        {
                            do
                            {
                                var cw = Crosswalks[cwIdx];
                                ObstacleMath.ClosestPointOnShape(pos, cw.Shape, cw.Center, cw.HalfExtents, cw.RotationY,
                                    out bool isInside, out _);
                                if (isInside) return true;
                            } while (CrosswalkCellMap.TryGetNextValue(out cwIdx, ref it));
                        }
                    }
                }
                return false;
            }

            /// <summary>
            /// If <paramref name="pos"/> is inside any road zone, eject the agent past the nearest
            /// road boundary by 0.05m. Mirror of <see cref="ConstrainToWalkable"/> in spirit, but
            /// inverted: here we WANT the agent to leave the shape rather than enter it. Kills
            /// the inward component of velocity so the agent stops pressing back onto the road.
            /// </summary>
            private float3 PushoutFromRoad(float3 pos, ref AgentMovement movement)
            {
                int2 cell = SpatialHashUtil.Cell(pos, RoadCellSize);
                for (int dx = -1; dx <= 1; dx++)
                {
                    for (int dz = -1; dz <= 1; dz++)
                    {
                        int hash = SpatialHashUtil.HashCell(new int2(cell.x + dx, cell.y + dz));
                        if (RoadCellMap.TryGetFirstValue(hash, out int roadIdx, out var it))
                        {
                            do
                            {
                                var road = Roads[roadIdx];
                                float3 closest = ObstacleMath.ClosestPointOnShape(pos, road.Shape, road.Center,
                                    road.HalfExtents, road.RotationY, out bool inside, out _);
                                if (inside)
                                {
                                    // Agent is inside the road; closest is the nearest boundary
                                    // point. (closest - pos) points from the agent toward the
                                    // boundary, which is the OUTWARD direction relative to the road.
                                    float3 outward = closest - pos;
                                    outward.y = 0f;
                                    if (math.lengthsq(outward) < 1e-6f)
                                    {
                                        outward = new float3(1f, 0f, 0f);
                                    }
                                    else
                                    {
                                        outward = math.normalize(outward);
                                    }

                                    pos = closest + outward * 0.05f;
                                    pos.y = 0f;

                                    // Kill any velocity component still pulling back into the road.
                                    float vDotOut = math.dot(movement.Velocity, outward);
                                    if (vDotOut < 0f)
                                    {
                                        movement.Velocity -= outward * vDotOut;
                                    }
                                }
                            } while (RoadCellMap.TryGetNextValue(out roadIdx, ref it));
                        }
                    }
                }
                return pos;
            }
        }
    }
}
