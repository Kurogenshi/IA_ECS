using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using Unity.Transforms;

namespace Crowd.Systems
{
    public struct AgentSpatialData
    {
        public Entity Entity;
        public float3 Position;
        public float3 Velocity;   // Phase 3: ORCA-lite needs neighbor velocities to predict TTC
        public byte IsHurried;
    }

    /// <summary>
    /// Builds a spatial hash of all agents then runs steering (path-follow + separation) in jobs.
    /// Honours SpawnerConfig.SteeringInterval to skip frames; on skipped frames the previous
    /// velocity (already smoothed by lerp) continues to drive the movement system.
    /// </summary>
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    public partial struct AgentSteeringSystem : ISystem
    {
        private NativeParallelMultiHashMap<int, AgentSpatialData> _spatialHash;
        private EntityQuery _agentQuery;
        private BufferLookup<Waypoint> _waypointLookup;
        private int _frameCounter;

        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<SpawnerConfig>();
            state.RequireForUpdate<ObstacleSpatialIndex>();
            state.RequireForUpdate<RoadSpatialIndex>();
            state.RequireForUpdate<CrosswalkSpatialIndex>();
            state.RequireForUpdate<WalkableSpatialIndex>();
            _agentQuery = SystemAPI.QueryBuilder()
                .WithAll<AgentTag, AgentMovement, AgentTypeData, PathFollower, AgentGoal, LocalTransform>()
                .Build();
            _waypointLookup = state.GetBufferLookup<Waypoint>(true);
            _spatialHash = new NativeParallelMultiHashMap<int, AgentSpatialData>(8192, Allocator.Persistent);
            _frameCounter = 0;
        }

        public void OnDestroy(ref SystemState state)
        {
            if (_spatialHash.IsCreated) _spatialHash.Dispose();
        }

        public void OnUpdate(ref SystemState state)
        {
            var config = SystemAPI.GetSingleton<SpawnerConfig>();
            int agentCount = _agentQuery.CalculateEntityCount();
            if (agentCount == 0) return;

            // Steering is the heaviest CPU job here. Skip N-1 frames out of N when the
            // configured interval > 1; the velocity smoothing in the steer job keeps motion
            // stable visually even with sparse updates.
            _frameCounter++;
            int interval = math.max(1, config.SteeringInterval);
            if (interval > 1 && (_frameCounter % interval) != 0) return;

            _waypointLookup.Update(ref state);

            int required = math.max(agentCount * 2, 1024);
            if (_spatialHash.Capacity < required)
            {
                _spatialHash.Capacity = required;
            }
            _spatialHash.Clear();

            var buildJob = new BuildSpatialHashJob
            {
                Map = _spatialHash.AsParallelWriter(),
                CellSize = config.NeighborCellSize,
            };
            state.Dependency = buildJob.ScheduleParallel(_agentQuery, state.Dependency);

            // Multiply DeltaTime by the steering interval so the smoothing/lerp behaves as
            // if running at full rate (otherwise velocity would lag behind targets).
            float scaledDt = SystemAPI.Time.DeltaTime * interval;
            var obstacleIndex  = SystemAPI.GetSingleton<ObstacleSpatialIndex>();
            var roadIndex      = SystemAPI.GetSingleton<RoadSpatialIndex>();
            var crosswalkIndex = SystemAPI.GetSingleton<CrosswalkSpatialIndex>();
            var walkableIndex  = SystemAPI.GetSingleton<WalkableSpatialIndex>();

            var steerJob = new SteeringJob
            {
                Map = _spatialHash,
                CellSize = config.NeighborCellSize,
                SeparationRadius = config.SeparationRadius,
                WaypointLookup = _waypointLookup,
                WaypointArriveDistance = config.WaypointArriveDistance,
                DeltaTime = scaledDt,
                SteeringSmoothing = config.SteeringSmoothing,
                StationaryWanderRadius = config.StationaryWanderRadius,
                TimeSeed = (uint)math.max(1, (int)(SystemAPI.Time.ElapsedTime * 1000.0)),
                Obstacles = obstacleIndex.Obstacles,
                ObstacleCellMap = obstacleIndex.CellToObstacleIndex,
                ObstacleCellSize = obstacleIndex.CellSize,
                ObstacleRepulsionRadius = config.ObstacleRepulsionRadius,
                ObstacleWeight = config.ObstacleWeight,
                LookAheadTime = config.LookAheadTime,
                AvoidanceWeight = config.AvoidanceWeight,
                AvoidanceCollisionRadiusSq = config.AvoidanceCollisionRadius * config.AvoidanceCollisionRadius,
                Roads             = roadIndex.Roads,
                RoadCellMap       = roadIndex.CellToRoadIndex,
                RoadCellSize      = roadIndex.CellSize,
                EnforceRoads      = roadIndex.HasRoads,
                RoadRepulsionRadius = config.RoadRepulsionRadius,
                RoadWeight        = config.RoadWeight,
                Crosswalks        = crosswalkIndex.Crosswalks,
                CrosswalkCellMap  = crosswalkIndex.CellToCrosswalkIndex,
                CrosswalkCellSize = crosswalkIndex.CellSize,
                HasCrosswalks     = crosswalkIndex.HasCrosswalks,
                WalkableAreas     = walkableIndex.Areas,
                WalkableCellMap   = walkableIndex.CellToAreaIndex,
                WalkableCellSize  = walkableIndex.CellSize,
                EnforceWalkable   = walkableIndex.HasAreas,
                WalkableSlideRadius = config.WalkableSlideRadius,
            };
            state.Dependency = steerJob.ScheduleParallel(_agentQuery, state.Dependency);
        }

        [BurstCompile]
        private partial struct BuildSpatialHashJob : IJobEntity
        {
            public NativeParallelMultiHashMap<int, AgentSpatialData>.ParallelWriter Map;
            public float CellSize;

            private void Execute(Entity entity, in LocalTransform transform, in AgentTypeData type, in AgentMovement movement)
            {
                int hash = SpatialHashUtil.HashCell(SpatialHashUtil.Cell(transform.Position, CellSize));
                Map.Add(hash, new AgentSpatialData
                {
                    Entity = entity,
                    Position = transform.Position,
                    Velocity = movement.Velocity,
                    IsHurried = (byte)(type.Behavior == AgentBehavior.HurriedPedestrian ? 1 : 0),
                });
            }
        }

        [BurstCompile]
        private partial struct SteeringJob : IJobEntity
        {
            [ReadOnly] public NativeParallelMultiHashMap<int, AgentSpatialData> Map;
            [ReadOnly] public BufferLookup<Waypoint> WaypointLookup;
            public float CellSize;
            public float SeparationRadius;
            public float WaypointArriveDistance;
            public float DeltaTime;
            public float SteeringSmoothing;
            public float StationaryWanderRadius;
            public uint TimeSeed;

            // Phase 1: static obstacle avoidance
            [ReadOnly] public NativeArray<StaticObstacle> Obstacles;
            [ReadOnly] public NativeParallelMultiHashMap<int, int> ObstacleCellMap;
            public float ObstacleCellSize;
            public float ObstacleRepulsionRadius;
            public float ObstacleWeight;

            // Phase 3: ORCA-lite local avoidance
            public float LookAheadTime;
            public float AvoidanceWeight;
            public float AvoidanceCollisionRadiusSq;

            // Phase 11: road tangent projection + active repulsion — keep pedestrians off roads
            // unless they're on a crosswalk.
            [ReadOnly] public NativeArray<RoadZone> Roads;
            [ReadOnly] public NativeParallelMultiHashMap<int, int> RoadCellMap;
            public float RoadCellSize;
            public byte EnforceRoads;
            public float RoadRepulsionRadius;
            public float RoadWeight;

            [ReadOnly] public NativeArray<CrosswalkZone> Crosswalks;
            [ReadOnly] public NativeParallelMultiHashMap<int, int> CrosswalkCellMap;
            public float CrosswalkCellSize;
            public byte HasCrosswalks;

            // Phase 11 bis: walkable boundary tangent — keep pedestrians from drifting off
            // sidewalks at the seams between zones / next to non-walkable terrain.
            [ReadOnly] public NativeArray<WalkableArea> WalkableAreas;
            [ReadOnly] public NativeParallelMultiHashMap<int, int> WalkableCellMap;
            public float WalkableCellSize;
            public byte EnforceWalkable;
            public float WalkableSlideRadius;

            private void Execute(
                Entity entity,
                ref AgentMovement movement,
                ref PathFollower path,
                ref AgentGoal goal,
                in LocalTransform transform,
                in AgentTypeData typeData)
            {
                float3 pos = transform.Position;

                // 1. Determine `desired`. POI goal (Phase 4) takes precedence over PathFollower;
                //    when the agent is between goals (Idle) or has none (Stationary), fall back
                //    to the existing path / wander logic.
                float3 desired = float3.zero;
                bool goalDrives = goal.State != AgentGoalState.Idle;

                if (goal.State == AgentGoalState.Interacting)
                {
                    // desired = 0: the agent stays put. Separation still applies (so neighbors
                    // can nudge it slightly) but no path force pulls it away from the POI.
                }
                else if (goal.State == AgentGoalState.Traveling)
                {
                    float3 toTarget = goal.TargetPosition - pos;
                    toTarget.y = 0f;
                    float distSq = math.lengthsq(toTarget);
                    if (distSq > 1e-4f)
                    {
                        desired = toTarget * math.rsqrt(distSq);
                    }
                }
                else if (typeData.Behavior != AgentBehavior.Stationary && path.PathEntity != Entity.Null
                    && WaypointLookup.HasBuffer(path.PathEntity))
                {
                    var waypoints = WaypointLookup[path.PathEntity];
                    if (waypoints.Length > 0)
                    {
                        int idx = math.clamp(path.CurrentWaypoint, 0, waypoints.Length - 1);
                        float3 target = waypoints[idx].Position;
                        float3 toTarget = target - pos;
                        toTarget.y = 0f;
                        float distSq = math.lengthsq(toTarget);

                        if (distSq < WaypointArriveDistance * WaypointArriveDistance)
                        {
                            int dir = path.ReverseDirection == 1 ? -1 : 1;
                            idx = (idx + dir + waypoints.Length) % waypoints.Length;
                            path.CurrentWaypoint = idx;
                            target = waypoints[idx].Position;
                            toTarget = target - pos;
                            toTarget.y = 0f;
                            distSq = math.lengthsq(toTarget);
                        }

                        if (distSq > 1e-4f)
                        {
                            desired = toTarget * math.rsqrt(distSq);
                        }
                    }
                }
                else if (typeData.Behavior == AgentBehavior.Stationary && StationaryWanderRadius > 0.05f)
                {
                    float3 toHome = path.HomePosition - pos;
                    toHome.y = 0f;
                    float homeDistSq = math.lengthsq(toHome);
                    if (homeDistSq > StationaryWanderRadius * StationaryWanderRadius)
                    {
                        desired = toHome * math.rsqrt(homeDistSq);
                    }
                    else
                    {
                        var rng = Random.CreateFromIndex((uint)entity.Index ^ TimeSeed);
                        float a = rng.NextFloat(0f, math.PI * 2f);
                        desired = new float3(math.cos(a), 0f, math.sin(a)) * 0.2f;
                    }
                }

                // 2. Separation (close range) + ORCA-lite anticipation (medium range, Phase 3).
                //    Both forces are accumulated in the same neighbor sweep to avoid duplicating
                //    the spatial-hash walk.
                float3 separation = float3.zero;
                float3 avoidance = float3.zero;
                int neighborCount = 0;
                int avoidanceCount = 0;
                int2 cell = SpatialHashUtil.Cell(pos, CellSize);
                float sepRadSq = SeparationRadius * SeparationRadius;
                float avoidRangeSq = sepRadSq * 9f; // ~3× separation radius — moderate anticipation window

                for (int dx = -1; dx <= 1; dx++)
                {
                    for (int dz = -1; dz <= 1; dz++)
                    {
                        int hash = SpatialHashUtil.HashCell(new int2(cell.x + dx, cell.y + dz));
                        if (Map.TryGetFirstValue(hash, out var neighbor, out var it))
                        {
                            do
                            {
                                if (neighbor.Entity == entity) continue;
                                float3 diff = pos - neighbor.Position;
                                diff.y = 0f;
                                float dSq = math.lengthsq(diff);
                                if (dSq < 1e-5f) continue;

                                // (a) Short-range separation — purely reactive.
                                if (dSq < sepRadSq)
                                {
                                    float weight = 1f / dSq;
                                    if (neighbor.IsHurried == 1) weight *= 1.6f;
                                    separation += diff * weight;
                                    neighborCount++;
                                }
                                // (b) Medium-range ORCA-lite — anticipate trajectories.
                                else if (dSq < avoidRangeSq && AvoidanceWeight > 0f)
                                {
                                    float3 rv = movement.Velocity - neighbor.Velocity;
                                    float rvSq = math.lengthsq(rv);
                                    if (rvSq < 1e-4f) continue; // moving in lockstep — no convergence

                                    float dotDV = math.dot(diff, rv);
                                    if (dotDV >= 0f) continue;  // already separating

                                    float ttc = -dotDV / rvSq;
                                    if (ttc > LookAheadTime) continue;

                                    // Closest-approach vector (from neighbor to us at impact time).
                                    float3 missVec = diff + rv * ttc;
                                    missVec.y = 0f;
                                    float missLenSq = math.lengthsq(missVec);
                                    if (missLenSq >= AvoidanceCollisionRadiusSq) continue;

                                    float urgency = 1f - ttc / LookAheadTime;
                                    urgency *= urgency;

                                    if (missLenSq > 1e-5f)
                                    {
                                        avoidance += missVec * (urgency * math.rsqrt(missLenSq));
                                    }
                                    else
                                    {
                                        // Head-on: pick a deterministic side perpendicular to rv.
                                        // Tiebreaking by entity-index parity ensures the pair
                                        // chooses opposite sides instead of mirror-locking.
                                        float3 perp = new float3(-rv.z, 0f, rv.x);
                                        float perpLenSq = math.lengthsq(perp);
                                        if (perpLenSq > 1e-5f)
                                        {
                                            perp = perp * math.rsqrt(perpLenSq);
                                            if ((entity.Index & 1) == 0) perp = -perp;
                                            avoidance += perp * urgency;
                                        }
                                    }
                                    avoidanceCount++;
                                }
                            } while (Map.TryGetNextValue(out neighbor, ref it));
                        }
                    }
                }

                if (neighborCount > 0)
                {
                    separation = math.normalizesafe(separation);
                }
                if (avoidanceCount > 0)
                {
                    avoidance = math.normalizesafe(avoidance);
                }

                // 3. Static obstacle repulsion + wall-sliding (Phase 1)
                // - Repulsion: pushes the agent away from the surface (quadratic falloff).
                // - Wall-sliding: when `desired` points INTO a nearby obstacle, project it onto
                //   the obstacle's tangent so the agent walks along the wall in the direction
                //   that still brings it closer to its waypoint, instead of grinding into it.
                //   Without this, repulsion and path-follow cancel out and the agent stalls.
                float3 obstacleForce = float3.zero;
                if (Obstacles.Length > 0)
                {
                    int2 oCell = SpatialHashUtil.Cell(pos, ObstacleCellSize);
                    float repRad = ObstacleRepulsionRadius;
                    // Slightly larger range for tangent projection than for repulsion — we want
                    // to start sliding before the repulsion force kicks in, so the path direction
                    // is already wall-aligned by the time the two forces would clash.
                    float slideRad = repRad * 1.5f;

                    for (int dx = -1; dx <= 1; dx++)
                    {
                        for (int dz = -1; dz <= 1; dz++)
                        {
                            int hash = SpatialHashUtil.HashCell(new int2(oCell.x + dx, oCell.y + dz));
                            if (ObstacleCellMap.TryGetFirstValue(hash, out int obsIdx, out var oit))
                            {
                                do
                                {
                                    var obs = Obstacles[obsIdx];
                                    float3 closest = ObstacleMath.ClosestPoint(pos, obs, out bool inside, out float signedDist);
                                    float3 away = pos - closest;
                                    away.y = 0f;
                                    float awayLenSq = math.lengthsq(away);

                                    if (inside)
                                    {
                                        float3 n = awayLenSq > 1e-6f ? math.normalize(away) : new float3(1f, 0f, 0f);
                                        obstacleForce += n;
                                        // Slide hard: kill any inward component of desired.
                                        float dn = math.dot(desired, n);
                                        if (dn < 0f) desired -= n * dn;
                                    }
                                    else if (signedDist < slideRad && awayLenSq > 1e-6f)
                                    {
                                        float3 n = math.normalize(away);

                                        if (signedDist < repRad)
                                        {
                                            float t = 1f - (signedDist / repRad);
                                            obstacleForce += n * (t * t);
                                        }

                                        // Tangent-project desired: weight grows from 0 at slideRad
                                        // to 1 at the surface, so far-away walls don't influence
                                        // path direction at all and close walls fully redirect it.
                                        float dn = math.dot(desired, n);
                                        if (dn < 0f)
                                        {
                                            float slideWeight = 1f - (signedDist / slideRad);
                                            desired -= n * dn * slideWeight;
                                        }
                                    }
                                } while (ObstacleCellMap.TryGetNextValue(out obsIdx, ref oit));
                            }
                        }
                    }
                    obstacleForce = math.normalizesafe(obstacleForce);

                    // Re-normalize desired after projection so we don't lose speed near walls.
                    float dMagSq = math.lengthsq(desired);
                    if (dMagSq > 1e-6f) desired = desired * math.rsqrt(dMagSq);
                    else desired = float3.zero;
                }

                // 3b. Roads (Phase 11) — three combined behaviors:
                //   (a) Active outward repulsion proportional to proximity, so separation /
                //       avoidance forces from neighbors can't accidentally push agents into the
                //       road. Lives in `roadForce`, applied in the final blend.
                //   (b) Tangent projection of `desired` (wall-sliding) when desired points into
                //       a nearby road from the sidewalk side.
                //   (c) Hard escape when the agent is already inside a road (overshoot recovery).
                //       The outward direction here is `closest - pos`, i.e. toward the NEAREST
                //       boundary, so the agent rebounds to the sidewalk they came from instead
                //       of crossing the entire road slab.
                //
                // All three are skipped while the agent stands inside a crosswalk (crossing is
                // intentional and a crosswalk is "drilled through" the road for foot traffic).
                float3 roadForce = float3.zero;
                bool onCrosswalk = HasCrosswalks == 1 && Crosswalks.Length > 0
                                 && IsPosInsideAnyCrosswalk(pos);

                if (EnforceRoads == 1 && Roads.Length > 0 && !onCrosswalk)
                {
                    float roadSlideRad = math.max(RoadRepulsionRadius, ObstacleRepulsionRadius * 1.5f);
                    int2 rCell = SpatialHashUtil.Cell(pos, RoadCellSize);

                    for (int dx = -1; dx <= 1; dx++)
                    {
                        for (int dz = -1; dz <= 1; dz++)
                        {
                            int hash = SpatialHashUtil.HashCell(new int2(rCell.x + dx, rCell.y + dz));
                            if (RoadCellMap.TryGetFirstValue(hash, out int roadIdx, out var rit))
                            {
                                do
                                {
                                    var road = Roads[roadIdx];
                                    float3 closest = ObstacleMath.ClosestPointOnShape(pos, road.Shape, road.Center,
                                        road.HalfExtents, road.RotationY, out bool inside, out float signedDist);

                                    if (inside)
                                    {
                                        // Agent inside the road. ClosestPointOnShape returns the
                                        // nearest boundary point when inside, so (closest - pos)
                                        // is the OUTWARD direction toward the nearest exit.
                                        // Critical: do NOT use (pos - closest) here — that points
                                        // toward the OPPOSITE boundary and makes the agent cross
                                        // the entire road instead of rebounding.
                                        float3 escape = closest - pos;
                                        escape.y = 0f;
                                        float escapeLenSq = math.lengthsq(escape);
                                        float3 nOut = escapeLenSq > 1e-6f
                                            ? escape * math.rsqrt(escapeLenSq)
                                            : new float3(1f, 0f, 0f);

                                        // Strong escape push — overrides POI-pull while inside.
                                        roadForce += nOut;
                                        // Kill any component of desired that still pulls into the road.
                                        float dn = math.dot(desired, nOut);
                                        if (dn < 0f) desired -= nOut * dn;
                                    }
                                    else if (signedDist < roadSlideRad)
                                    {
                                        // Agent just outside the road. (pos - closest) is OUTWARD here.
                                        float3 away = pos - closest;
                                        away.y = 0f;
                                        float awayLenSq = math.lengthsq(away);
                                        if (awayLenSq < 1e-6f) continue;

                                        float3 nOut = away * math.rsqrt(awayLenSq);

                                        // (a) Repulsion: quadratic ramp from 0 at slideRad to 1 at the surface.
                                        float t = 1f - (signedDist / roadSlideRad);
                                        roadForce += nOut * (t * t);

                                        // (b) Tangent projection of desired.
                                        float dn = math.dot(desired, nOut);
                                        if (dn < 0f)
                                        {
                                            desired -= nOut * dn * t;
                                        }
                                    }
                                } while (RoadCellMap.TryGetNextValue(out roadIdx, ref rit));
                            }
                        }
                    }

                    roadForce = math.normalizesafe(roadForce);

                    // Re-normalize desired so wall-sliding doesn't shrink the agent's speed.
                    float dMagSq2 = math.lengthsq(desired);
                    if (dMagSq2 > 1e-6f) desired = desired * math.rsqrt(dMagSq2);
                    else desired = float3.zero;
                }

                // 3c. Walkable-area tangent projection (Phase 11 bis). Keeps agents from drifting
                // off the sidewalk at spots where the path / POI direction or neighbor pressure
                // would push them outside the walkable zone (and into either a building, a road,
                // or empty terrain). Skipped when on a crosswalk (legitimate sidewalk-leave).
                //
                // Overlap handling: at intersections, two walkable areas share a seam. We only
                // tangent-project if a forward probe (pos + outward * (slide + nudge)) lands
                // outside ALL walkable areas — i.e. the agent would actually leave walkable
                // territory. Inside an overlap, no projection happens, so seams stay free.
                if (EnforceWalkable == 1 && WalkableAreas.Length > 0 && WalkableSlideRadius > 0.01f
                    && !onCrosswalk)
                {
                    int2 wCell = SpatialHashUtil.Cell(pos, WalkableCellSize);
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        for (int dz = -1; dz <= 1; dz++)
                        {
                            int hash = SpatialHashUtil.HashCell(new int2(wCell.x + dx, wCell.y + dz));
                            if (WalkableCellMap.TryGetFirstValue(hash, out int aIdx, out var wit))
                            {
                                do
                                {
                                    var area = WalkableAreas[aIdx];
                                    float3 closest = ObstacleMath.ClosestPointOnShape(pos, area.Shape, area.Center,
                                        area.HalfExtents, area.RotationY, out bool insideArea, out float signedDist);
                                    if (!insideArea) continue;

                                    // Distance to boundary from inside is |signedDist|.
                                    float distToBoundary = -signedDist;
                                    if (distToBoundary > WalkableSlideRadius) continue;

                                    // Outward (would-leave) direction: from agent toward boundary.
                                    float3 outward = closest - pos;
                                    outward.y = 0f;
                                    float outLenSq = math.lengthsq(outward);
                                    if (outLenSq < 1e-6f) continue;
                                    float3 nOut = outward * math.rsqrt(outLenSq);

                                    float dn = math.dot(desired, nOut);
                                    if (dn <= 0f) continue; // desired doesn't push outward — fine

                                    // Probe a bit past the boundary; if it's inside another walkable
                                    // area (overlap/seam), don't fight the desired direction.
                                    float3 probe = pos + nOut * (distToBoundary + 0.5f);
                                    if (IsInsideAnyOtherWalkable(probe, aIdx)) continue;

                                    // Tangent-project, weighted by proximity (1 at the boundary, 0 at slideRad).
                                    float slideWeight = 1f - (distToBoundary / WalkableSlideRadius);
                                    desired -= nOut * dn * slideWeight;
                                } while (WalkableCellMap.TryGetNextValue(out aIdx, ref wit));
                            }
                        }
                    }

                    float dMagSq3 = math.lengthsq(desired);
                    if (dMagSq3 > 1e-6f) desired = desired * math.rsqrt(dMagSq3);
                    else desired = float3.zero;
                }

                // 4. Combine
                float pathWeight = 1.0f;
                float sepWeight = 1.6f;
                if (typeData.Behavior == AgentBehavior.HurriedPedestrian) { pathWeight = 1.4f; sepWeight = 1.2f; }
                else if (typeData.Behavior == AgentBehavior.Stationary) { pathWeight = 0.5f; sepWeight = 2.0f; }

                float3 steer = desired * pathWeight
                             + separation * sepWeight
                             + avoidance * AvoidanceWeight
                             + obstacleForce * ObstacleWeight
                             + roadForce * RoadWeight;
                float3 targetVelocity = math.normalizesafe(steer) * movement.Speed;
                targetVelocity.y = 0f;

                float t2 = math.saturate(DeltaTime * SteeringSmoothing);
                movement.Velocity = math.lerp(movement.Velocity, targetVelocity, t2);

                // 5. Stuck detection (Phase 1 / Phase 4 stopgap until real pathfinding in Phase 5).
                // When an agent's target velocity stays very low while moving toward something:
                //   - Path-driven (no goal): skip the current waypoint after ~2s.
                //   - Goal-driven (Traveling): abandon the POI after ~5s so the goal system can re-roll.
                //   - Interacting / true Stationary / no path: no action (the agent is meant to be still).
                bool isTravelingToPOI = goal.State == AgentGoalState.Traveling;
                bool hasPath = !goalDrives
                               && typeData.Behavior != AgentBehavior.Stationary
                               && path.PathEntity != Entity.Null
                               && WaypointLookup.HasBuffer(path.PathEntity);
                float stallThresholdSq = movement.Speed * movement.Speed * 0.04f; // 20% of cruise
                bool stalled = movement.Speed > 0.01f && math.lengthsq(targetVelocity) < stallThresholdSq;

                if (stalled && hasPath)
                {
                    movement.StuckTimer += DeltaTime;
                    if (movement.StuckTimer > 2.0f)
                    {
                        var wp = WaypointLookup[path.PathEntity];
                        if (wp.Length > 1)
                        {
                            int dir = path.ReverseDirection == 1 ? -1 : 1;
                            path.CurrentWaypoint = (path.CurrentWaypoint + dir + wp.Length) % wp.Length;
                        }
                        movement.StuckTimer = 0f;
                    }
                }
                else if (stalled && isTravelingToPOI)
                {
                    movement.StuckTimer += DeltaTime;
                    if (movement.StuckTimer > 5.0f)
                    {
                        // Abandon this POI. AgentGoalSystem will re-roll next frame.
                        goal.State     = AgentGoalState.Idle;
                        goal.TargetPOI = Entity.Null;
                        movement.StuckTimer = 0f;
                    }
                }
                else
                {
                    movement.StuckTimer = math.max(0f, movement.StuckTimer - DeltaTime);
                }
            }

            /// <summary>Local helper: true if <paramref name="pos"/> lies inside any crosswalk
            /// in the spatial index. Mirror of the same function in <see cref="AgentMovementSystem"/>;
            /// kept inline here to avoid passing the indices through additional structs.</summary>
            private bool IsPosInsideAnyCrosswalk(float3 pos)
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

            /// <summary>True if <paramref name="probe"/> sits inside any walkable area OTHER than
            /// <paramref name="skipIndex"/>. Used by the walkable wall-sliding logic to recognize
            /// seam overlaps (two zones meeting at an intersection) and let agents cross freely
            /// between them. Crosswalks count too — they're a legitimate sidewalk-leave path.</summary>
            private bool IsInsideAnyOtherWalkable(float3 probe, int skipIndex)
            {
                // Crosswalk fast path.
                if (HasCrosswalks == 1 && Crosswalks.Length > 0 && IsPosInsideAnyCrosswalk(probe))
                    return true;

                int2 cell = SpatialHashUtil.Cell(probe, WalkableCellSize);
                for (int dx = -1; dx <= 1; dx++)
                {
                    for (int dz = -1; dz <= 1; dz++)
                    {
                        int hash = SpatialHashUtil.HashCell(new int2(cell.x + dx, cell.y + dz));
                        if (WalkableCellMap.TryGetFirstValue(hash, out int aIdx, out var it))
                        {
                            do
                            {
                                if (aIdx == skipIndex) continue;
                                var area = WalkableAreas[aIdx];
                                ObstacleMath.ClosestPointOnShape(probe, area.Shape, area.Center, area.HalfExtents, area.RotationY,
                                    out bool isInside, out _);
                                if (isInside) return true;
                            } while (WalkableCellMap.TryGetNextValue(out aIdx, ref it));
                        }
                    }
                }
                return false;
            }
        }
    }

    public static class SpatialHashUtil
    {
        public static int2 Cell(float3 pos, float cellSize)
        {
            return new int2((int)math.floor(pos.x / cellSize), (int)math.floor(pos.z / cellSize));
        }

        public static int HashCell(int2 cell)
        {
            unchecked
            {
                return (cell.x * 73856093) ^ (cell.y * 19349663);
            }
        }
    }
}
