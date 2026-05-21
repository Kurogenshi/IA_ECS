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
            var obstacleIndex = SystemAPI.GetSingleton<ObstacleSpatialIndex>();

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

                // 4. Combine
                float pathWeight = 1.0f;
                float sepWeight = 1.6f;
                if (typeData.Behavior == AgentBehavior.HurriedPedestrian) { pathWeight = 1.4f; sepWeight = 1.2f; }
                else if (typeData.Behavior == AgentBehavior.Stationary) { pathWeight = 0.5f; sepWeight = 2.0f; }

                float3 steer = desired * pathWeight
                             + separation * sepWeight
                             + avoidance * AvoidanceWeight
                             + obstacleForce * ObstacleWeight;
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
