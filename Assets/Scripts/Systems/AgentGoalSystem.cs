using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;

namespace Crowd.Systems
{
    /// <summary>
    /// Drives the per-agent state machine Idle -> Traveling -> Interacting -> Idle (Phase 4).
    /// Runs <em>before</em> <see cref="AgentSteeringSystem"/> so the steering job sees the
    /// up-to-date goal each frame.
    ///
    /// Single-threaded on purpose: it mutates <see cref="PointOfInterest.CurrentOccupancy"/>
    /// across agents, and the simplest race-free choice is sequential execution. With a few
    /// thousand agents the goal logic is cheap (a handful of comparisons + a write per agent).
    ///
    /// If the spawner singleton has an empty <see cref="POIRef"/> buffer (no POIs in scene),
    /// the system early-exits and every agent stays in Idle — downstream the steering falls
    /// back to <see cref="PathFollower"/> exactly as in Phase 2.
    /// </summary>
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateBefore(typeof(AgentSteeringSystem))]
    public partial struct AgentGoalSystem : ISystem
    {
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<SpawnerConfig>();
        }

        public void OnUpdate(ref SystemState state)
        {
            var configEntity = SystemAPI.GetSingletonEntity<SpawnerConfig>();

            // Snapshot the POI entity list — always run the job (even with 0 POIs) so that
            // agents in the Interacting state can still tick down their dwell timer if the
            // POI buffer becomes empty at runtime. The helpers below tolerate an empty list.
            int poiCount = 0;
            if (SystemAPI.HasBuffer<POIRef>(configEntity))
            {
                poiCount = SystemAPI.GetBuffer<POIRef>(configEntity).Length;
            }
            var poiEntities = new NativeArray<Entity>(poiCount, Allocator.TempJob);
            if (poiCount > 0)
            {
                var poiBuffer = SystemAPI.GetBuffer<POIRef>(configEntity);
                for (int i = 0; i < poiBuffer.Length; i++) poiEntities[i] = poiBuffer[i].POIEntity;
            }

            var poiLookup = SystemAPI.GetComponentLookup<PointOfInterest>(false);

            var spawnerConfig = SystemAPI.GetSingleton<SpawnerConfig>();

            var job = new GoalUpdateJob
            {
                POIs       = poiEntities,
                POILookup  = poiLookup,
                DeltaTime  = SystemAPI.Time.DeltaTime,
                TimeSeed   = (uint)math.max(1, (int)(SystemAPI.Time.ElapsedTime * 1000.0)),
                MinSpeedHurried = spawnerConfig.MinSpeedHurried,
                MaxSpeedHurried = spawnerConfig.MaxSpeedHurried,
                MinSpeedWalker  = spawnerConfig.MinSpeedWalker,
                MaxSpeedWalker  = spawnerConfig.MaxSpeedWalker,
                PersonalitySwapChance = 0.1f, // 10% chance on Idle->Traveling to flip Walker <-> Hurried
            };

            // Schedule (single-threaded) — see class comment for rationale.
            state.Dependency = job.Schedule(state.Dependency);
            poiEntities.Dispose(state.Dependency);
        }

        [BurstCompile]
        [WithAll(typeof(AgentTag))]
        private partial struct GoalUpdateJob : IJobEntity
        {
            [ReadOnly] public NativeArray<Entity> POIs;
            public ComponentLookup<PointOfInterest> POILookup;
            public float DeltaTime;
            public uint TimeSeed;
            public float MinSpeedHurried;
            public float MaxSpeedHurried;
            public float MinSpeedWalker;
            public float MaxSpeedWalker;
            public float PersonalitySwapChance;

            private void Execute(
                Entity entity,
                ref AgentGoal goal,
                ref AgentTypeData type,
                ref AgentMovement movement,
                in LocalTransform transform)
            {
                // Agents whose BASE personality is Stationary opt out of POIs (rare — set by
                // scene-specific overrides, not by the default spawn distribution). Their
                // live Behavior is also Stationary so they fall through to the wander branch
                // in steering.
                if (type.BaseBehavior == AgentBehavior.Stationary) return;

                switch (goal.State)
                {
                    case AgentGoalState.Idle:
                        TryPickPOI(entity, ref goal, ref type, ref movement);
                        break;

                    case AgentGoalState.Traveling:
                        UpdateTraveling(entity, ref goal, ref type, ref movement, transform.Position);
                        break;

                    case AgentGoalState.Interacting:
                        UpdateInteracting(entity, ref goal, ref type, ref movement);
                        break;
                }
            }

            /// <summary>Up to 4 random tries to find a POI with available capacity. If none,
            /// stays Idle — we'll try again next frame. On a successful pick, also rolls the
            /// personality-swap dice (Phase 3 behavior dynamics).</summary>
            private void TryPickPOI(Entity entity, ref AgentGoal goal, ref AgentTypeData type, ref AgentMovement movement)
            {
                var rng = Random.CreateFromIndex((uint)entity.Index ^ TimeSeed);
                int attempts = math.min(4, POIs.Length);
                for (int i = 0; i < attempts; i++)
                {
                    int idx = rng.NextInt(0, POIs.Length);
                    var poiEnt = POIs[idx];
                    if (!POILookup.HasComponent(poiEnt)) continue;
                    var poi = POILookup[poiEnt];
                    if (poi.CurrentOccupancy < poi.Capacity)
                    {
                        // Note: we don't reserve the slot yet — multiple agents may target the
                        // same POI in parallel. The first to arrive claims a slot; latecomers
                        // find it full and re-roll. Acceptable for Phase 4; reservation can be
                        // added later if it visibly causes thrashing.
                        goal.TargetPOI      = poiEnt;
                        goal.TargetPosition = poi.Position;
                        goal.State          = AgentGoalState.Traveling;

                        // On each fresh trip, small chance to flip personality (Phase 3).
                        // Resamples BaseSpeed from the matching speed range so the new
                        // personality looks consistent with spawn-time variation.
                        if (rng.NextFloat() < PersonalitySwapChance)
                        {
                            if (type.BaseBehavior == AgentBehavior.Walker)
                            {
                                type.BaseBehavior = AgentBehavior.HurriedPedestrian;
                                type.BaseSpeed    = rng.NextFloat(MinSpeedHurried, MaxSpeedHurried);
                            }
                            else if (type.BaseBehavior == AgentBehavior.HurriedPedestrian)
                            {
                                type.BaseBehavior = AgentBehavior.Walker;
                                type.BaseSpeed    = rng.NextFloat(MinSpeedWalker, MaxSpeedWalker);
                            }
                        }

                        // Restore live behavior + cruise speed in case we just exited Interacting.
                        type.Behavior  = type.BaseBehavior;
                        movement.Speed = type.BaseSpeed;
                        return;
                    }
                }
            }

            private void UpdateTraveling(Entity entity, ref AgentGoal goal, ref AgentTypeData type, ref AgentMovement movement, float3 pos)
            {
                if (goal.TargetPOI == Entity.Null || !POILookup.HasComponent(goal.TargetPOI))
                {
                    goal.TargetPOI = Entity.Null;
                    goal.State     = AgentGoalState.Idle;
                    return;
                }

                var poi = POILookup[goal.TargetPOI];
                goal.TargetPosition = poi.Position; // refresh in case the POI was moved at runtime

                float3 diff = poi.Position - pos;
                diff.y = 0f;
                float arriveSq = poi.InteractionRadius * poi.InteractionRadius;
                if (math.lengthsq(diff) > arriveSq) return;

                // Arrived. Try to claim a slot atomically (single-thread guarantees correctness).
                if (poi.CurrentOccupancy < poi.Capacity)
                {
                    poi.CurrentOccupancy++;
                    POILookup[goal.TargetPOI] = poi;

                    var rng = Random.CreateFromIndex((uint)entity.Index ^ TimeSeed ^ 0xCAFEu);
                    goal.Timer = rng.NextFloat(poi.DwellTimeRange.x, poi.DwellTimeRange.y);
                    goal.State = AgentGoalState.Interacting;

                    // Phase 3: arriving at a POI = the agent is now Stationary. Speed = 0 lets
                    // the steering smoothing decay velocity to a halt naturally; animation will
                    // flip to Idle (forced via goal.State in AgentAnimationSystem).
                    type.Behavior  = AgentBehavior.Stationary;
                    movement.Speed = 0f;
                }
                else
                {
                    // POI filled up while we were traveling — pick another next frame.
                    goal.TargetPOI = Entity.Null;
                    goal.State     = AgentGoalState.Idle;
                }
            }

            private void UpdateInteracting(Entity entity, ref AgentGoal goal, ref AgentTypeData type, ref AgentMovement movement)
            {
                goal.Timer -= DeltaTime;
                if (goal.Timer > 0f) return;

                if (goal.TargetPOI != Entity.Null && POILookup.HasComponent(goal.TargetPOI))
                {
                    var poi = POILookup[goal.TargetPOI];
                    poi.CurrentOccupancy = math.max(0, poi.CurrentOccupancy - 1);
                    POILookup[goal.TargetPOI] = poi;
                }
                goal.TargetPOI = Entity.Null;
                goal.State     = AgentGoalState.Idle;

                // Restore the cruise behavior so the agent walks away from the POI at full
                // speed instead of crawling.
                type.Behavior  = type.BaseBehavior;
                movement.Speed = type.BaseSpeed;
            }
        }
    }
}
