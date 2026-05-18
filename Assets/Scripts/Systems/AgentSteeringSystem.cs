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
            _agentQuery = SystemAPI.QueryBuilder()
                .WithAll<AgentTag, AgentMovement, AgentTypeData, PathFollower, LocalTransform>()
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
            };
            state.Dependency = steerJob.ScheduleParallel(_agentQuery, state.Dependency);
        }

        [BurstCompile]
        private partial struct BuildSpatialHashJob : IJobEntity
        {
            public NativeParallelMultiHashMap<int, AgentSpatialData>.ParallelWriter Map;
            public float CellSize;

            private void Execute(Entity entity, in LocalTransform transform, in AgentTypeData type)
            {
                int hash = SpatialHashUtil.HashCell(SpatialHashUtil.Cell(transform.Position, CellSize));
                Map.Add(hash, new AgentSpatialData
                {
                    Entity = entity,
                    Position = transform.Position,
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

            private void Execute(
                Entity entity,
                ref AgentMovement movement,
                ref PathFollower path,
                in LocalTransform transform,
                in AgentTypeData typeData)
            {
                float3 pos = transform.Position;

                // 1. Path following (seek desired)
                float3 desired = float3.zero;
                if (typeData.Behavior != AgentBehavior.Stationary && path.PathEntity != Entity.Null
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

                // 2. Separation via spatial hash
                float3 separation = float3.zero;
                int neighborCount = 0;
                int2 cell = SpatialHashUtil.Cell(pos, CellSize);
                float sepRadSq = SeparationRadius * SeparationRadius;

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
                                if (dSq > 1e-5f && dSq < sepRadSq)
                                {
                                    float weight = 1f / dSq;
                                    if (neighbor.IsHurried == 1) weight *= 1.6f;
                                    separation += diff * weight;
                                    neighborCount++;
                                }
                            } while (Map.TryGetNextValue(out neighbor, ref it));
                        }
                    }
                }

                if (neighborCount > 0)
                {
                    separation = math.normalizesafe(separation);
                }

                // 3. Combine
                float pathWeight = 1.0f;
                float sepWeight = 1.6f;
                if (typeData.Behavior == AgentBehavior.HurriedPedestrian) { pathWeight = 1.4f; sepWeight = 1.2f; }
                else if (typeData.Behavior == AgentBehavior.Stationary) { pathWeight = 0.5f; sepWeight = 2.0f; }

                float3 steer = desired * pathWeight + separation * sepWeight;
                float3 targetVelocity = math.normalizesafe(steer) * movement.Speed;
                targetVelocity.y = 0f;

                float t = math.saturate(DeltaTime * SteeringSmoothing);
                movement.Velocity = math.lerp(movement.Velocity, targetVelocity, t);
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
