using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;

namespace Crowd.Systems
{
    /// <summary>
    /// Reconciles the live agent count with <see cref="CrowdRuntimeTarget.TargetCount"/> each
    /// frame. Used by the demo HUD (and any external script) to dial the crowd size up and
    /// down on the fly without restarting the simulation — necessary for the live demo
    /// requirement of the Forma Studio workshop.
    ///
    /// Spawn / despawn behaviors:
    /// <list type="bullet">
    /// <item>Target &gt; current : spawn up to <see cref="CrowdRuntimeTarget.SpawnBatchPerFrame"/>
    ///   agents this frame inside the configured spawn zone, randomized position + behavior.
    ///   The cap prevents a multi-thousand-agent jump from stuttering the frame.</item>
    /// <item>Target &lt; current : destroy the overflow in one ECB pass. Destruction is cheap,
    ///   no batching needed.</item>
    /// </list>
    ///
    /// Runs in SimulationSystemGroup AFTER the one-shot CrowdSpawnerSystem has created both
    /// the SpawnerConfig and the CrowdRuntimeTarget singletons.
    /// </summary>
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    public partial struct CrowdRuntimeControlSystem : ISystem
    {
        private EntityQuery _agentQuery;

        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<SpawnerConfig>();
            state.RequireForUpdate<CrowdRuntimeTarget>();
            _agentQuery = state.GetEntityQuery(ComponentType.ReadOnly<AgentTag>());
        }

        public void OnUpdate(ref SystemState state)
        {
            var configEntity = SystemAPI.GetSingletonEntity<SpawnerConfig>();
            var config       = SystemAPI.GetComponent<SpawnerConfig>(configEntity);
            var targetEntity = SystemAPI.GetSingletonEntity<CrowdRuntimeTarget>();
            var target       = SystemAPI.GetComponent<CrowdRuntimeTarget>(targetEntity);

            if (config.AgentPrefab == Entity.Null) return;

            int desired = math.max(0, target.TargetCount);
            int current = _agentQuery.CalculateEntityCount();
            int delta   = desired - current;
            if (delta == 0) return;

            var ecb = new EntityCommandBuffer(Allocator.Temp);

            if (delta > 0)
            {
                int batch = math.min(delta, math.max(1, target.SpawnBatchPerFrame));
                var pathBuffer = SystemAPI.GetBuffer<SpawnerPathRef>(configEntity);
                var waypointLookup = SystemAPI.GetBufferLookup<Waypoint>(true);
                int pathCount = pathBuffer.Length;

                var pathEntities = new NativeArray<Entity>(pathCount, Allocator.Temp);
                for (int i = 0; i < pathCount; i++) pathEntities[i] = pathBuffer[i].PathEntity;

                var random = new Random(target.NextSeed == 0u ? 1u : target.NextSeed);
                float pctHurried = math.clamp(config.PercentHurried, 0f, 1f);

                float zoneOriginX = config.ZoneCenter.x - config.ZoneSize.x * 0.5f;
                float zoneOriginZ = config.ZoneCenter.z - config.ZoneSize.z * 0.5f;

                for (int i = 0; i < batch; i++)
                {
                    var entity = ecb.Instantiate(config.AgentPrefab);

                    float3 pos = new float3(
                        zoneOriginX + random.NextFloat() * config.ZoneSize.x,
                        config.ZoneCenter.y,
                        zoneOriginZ + random.NextFloat() * config.ZoneSize.z);

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
                            if (wp.Length > 0) startWaypoint = random.NextInt(0, wp.Length);
                        }
                        reverse = (byte)(random.NextFloat() < 0.5f ? 0 : 1);
                    }

                    quaternion yaw = quaternion.RotateY(random.NextFloat(0f, math.PI * 2f));
                    ecb.SetComponent(entity, LocalTransform.FromPositionRotation(pos, yaw));
                    ecb.SetComponent(entity, new AgentMovement
                    {
                        Speed    = speed,
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
                        PathEntity       = assignedPath,
                        CurrentWaypoint  = startWaypoint,
                        ReverseDirection = reverse,
                        HomePosition     = pos,
                    });
                    ecb.SetComponent(entity, new AgentAnimationState
                    {
                        CurrentClip = AnimClipId.Idle,
                        ClipTime    = 0f,
                        PhaseOffset = random.NextFloat(0f, 4f),
                    });
                }

                target.NextSeed = random.state;
                pathEntities.Dispose();
            }
            else
            {
                // Despawn |delta| agents. We grab the current query entities and destroy the tail.
                int toRemove = -delta;
                var agents = _agentQuery.ToEntityArray(Allocator.Temp);
                int n = math.min(toRemove, agents.Length);
                for (int i = agents.Length - n; i < agents.Length; i++)
                {
                    ecb.DestroyEntity(agents[i]);
                }
                agents.Dispose();
            }

            ecb.Playback(state.EntityManager);
            ecb.Dispose();

            SystemAPI.SetComponent(targetEntity, target);
        }
    }
}
