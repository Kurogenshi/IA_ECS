using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;

namespace Crowd.Systems
{
    /// <summary>
    /// Spawns the requested number of agents from the prefab once, then disables itself.
    /// </summary>
    [UpdateInGroup(typeof(InitializationSystemGroup))]
    public partial struct CrowdSpawnerSystem : ISystem
    {
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<SpawnerConfig>();
        }

        public void OnUpdate(ref SystemState state)
        {
            state.Enabled = false;

            var configEntity = SystemAPI.GetSingletonEntity<SpawnerConfig>();
            var config = SystemAPI.GetComponent<SpawnerConfig>(configEntity);
            var pathBuffer = SystemAPI.GetBuffer<SpawnerPathRef>(configEntity);

            if (config.AgentPrefab == Entity.Null)
            {
                UnityEngine.Debug.LogError("[CrowdSpawnerSystem] No AgentPrefab assigned on CrowdSpawnerAuthoring.");
                return;
            }

            var waypointLookup = SystemAPI.GetBufferLookup<Waypoint>(true);

            int pathCount = pathBuffer.Length;
            var pathEntities = new NativeArray<Entity>(pathCount, Allocator.Temp);
            for (int i = 0; i < pathCount; i++) pathEntities[i] = pathBuffer[i].PathEntity;

            var ecb = new EntityCommandBuffer(Allocator.Temp);
            var random = new Random(config.RandomSeed);

            // Phase 3: Stationary is no longer a spawn-time category. Every agent starts as
            // Walker or HurriedPedestrian; Stationary becomes a runtime state assigned by
            // AgentGoalSystem when the agent is interacting with a POI.
            float pctHurried = math.clamp(config.PercentHurried, 0f, 1f);

            // Grid-based spawn: each agent gets its own cell with mild jitter, guaranteeing
            // a minimum spacing equal to the cell size. This avoids the dense pile-ups that
            // can lock groups of agents in place when the steering separation cancels itself
            // out in a packed cluster.
            int cols = math.max(1, (int)math.ceil(math.sqrt((float)config.Count)));
            int rows = (config.Count + cols - 1) / cols;
            float cellW = config.ZoneSize.x / cols;
            float cellD = config.ZoneSize.z / rows;
            float jitterScale = 0.6f; // fraction of cell size used for random offset
            float zoneOriginX = config.ZoneCenter.x - config.ZoneSize.x * 0.5f;
            float zoneOriginZ = config.ZoneCenter.z - config.ZoneSize.z * 0.5f;

            for (int i = 0; i < config.Count; i++)
            {
                var entity = ecb.Instantiate(config.AgentPrefab);

                int col = i % cols;
                int row = i / cols;
                float jitterX = (random.NextFloat() - 0.5f) * cellW * jitterScale;
                float jitterZ = (random.NextFloat() - 0.5f) * cellD * jitterScale;
                float3 pos = new float3(
                    zoneOriginX + (col + 0.5f) * cellW + jitterX,
                    config.ZoneCenter.y,
                    zoneOriginZ + (row + 0.5f) * cellD + jitterZ
                );

                float r = random.NextFloat();
                AgentBehavior baseBehavior;
                float speed;
                if (r < pctHurried)
                {
                    baseBehavior = AgentBehavior.HurriedPedestrian;
                    speed = random.NextFloat(config.MinSpeedHurried, config.MaxSpeedHurried);
                }
                else
                {
                    baseBehavior = AgentBehavior.Walker;
                    speed = random.NextFloat(config.MinSpeedWalker, config.MaxSpeedWalker);
                }

                Entity assignedPath = Entity.Null;
                int startWaypoint = 0;
                byte reverse = 0;

                if (pathCount > 0)
                {
                    int pIdx = random.NextInt(0, pathCount);
                    assignedPath = pathEntities[pIdx];
                    if (waypointLookup.HasBuffer(assignedPath))
                    {
                        var wp = waypointLookup[assignedPath];
                        if (wp.Length > 0)
                        {
                            startWaypoint = random.NextInt(0, wp.Length);
                        }
                    }
                    reverse = (byte)(random.NextFloat() < 0.5f ? 0 : 1);
                }

                quaternion yaw = quaternion.RotateY(random.NextFloat(0f, math.PI * 2f));
                ecb.SetComponent(entity, LocalTransform.FromPositionRotation(pos, yaw));
                ecb.SetComponent(entity, new AgentMovement
                {
                    Speed = speed,
                    Velocity = float3.zero,
                });
                ecb.SetComponent(entity, new AgentTypeData
                {
                    Behavior     = baseBehavior,
                    BaseBehavior = baseBehavior,
                    BaseSpeed    = speed,
                });
                ecb.SetComponent(entity, new PathFollower
                {
                    PathEntity = assignedPath,
                    CurrentWaypoint = startWaypoint,
                    ReverseDirection = reverse,
                    HomePosition = pos,
                });

                // Desync animation cycles: each agent starts its clip at a random phase.
                ecb.SetComponent(entity, new AgentAnimationState
                {
                    CurrentClip = AnimClipId.Idle,
                    ClipTime    = 0f,
                    PhaseOffset = random.NextFloat(0f, 4f),
                });
            }

            ecb.Playback(state.EntityManager);
            ecb.Dispose();
            pathEntities.Dispose();

            // Create the runtime-target singleton so CrowdRuntimeControlSystem can adjust the
            // live count from the HUD. Initial target matches what we just spawned.
            var targetEntity = state.EntityManager.CreateEntity();
            state.EntityManager.SetName(targetEntity, "CrowdRuntimeTarget");
            state.EntityManager.AddComponentData(targetEntity, new CrowdRuntimeTarget
            {
                TargetCount        = config.Count,
                SpawnBatchPerFrame = 500,
                NextSeed           = config.RandomSeed * 2654435761u + 1u,
            });
        }
    }
}
