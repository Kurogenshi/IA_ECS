using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;

namespace Crowd.Systems
{
    /// <summary>
    /// When the agent prefab uses a LODGroup, Entities Graphics bakes each LOD's
    /// MeshRenderer as its own render entity. The per-instance material property
    /// components (AnimClip / AnimTime / AgentVisible / AgentShadowVisible) live
    /// on the agent root, but they need to be readable on each render entity for
    /// the GPU to see the right values per LOD draw. This system mirrors them.
    ///
    /// For single-mesh agents (no LODGroup), there is no LinkedEntityGroup buffer
    /// on the entity and the system simply skips them.
    /// </summary>
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(AgentAnimationSystem))]
    [UpdateAfter(typeof(AgentVisibilitySystem))]
    public partial struct PropagateMaterialPropsToLODSystem : ISystem
    {
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<AgentTag>();
        }

        public void OnUpdate(ref SystemState state)
        {
            new PropagateJob
            {
                ClipLookup   = SystemAPI.GetComponentLookup<AnimClipProperty>(false),
                TimeLookup   = SystemAPI.GetComponentLookup<AnimTimeProperty>(false),
                VisLookup    = SystemAPI.GetComponentLookup<AgentVisibleProperty>(false),
                ShadowLookup = SystemAPI.GetComponentLookup<AgentShadowVisibleProperty>(false),
            }.ScheduleParallel();
        }

        // The component types accessed via ComponentLookup are the same as those we'd
        // normally take as `in` query parameters — including them as both would alias
        // and the scheduler refuses to run. Reading the root's value through the lookup
        // (using the entity argument) avoids that conflict.
        [BurstCompile]
        [WithAll(typeof(AgentTag))]
        private partial struct PropagateJob : IJobEntity
        {
            [NativeDisableContainerSafetyRestriction] public ComponentLookup<AnimClipProperty>           ClipLookup;
            [NativeDisableContainerSafetyRestriction] public ComponentLookup<AnimTimeProperty>           TimeLookup;
            [NativeDisableContainerSafetyRestriction] public ComponentLookup<AgentVisibleProperty>       VisLookup;
            [NativeDisableContainerSafetyRestriction] public ComponentLookup<AgentShadowVisibleProperty> ShadowLookup;

            private void Execute(Entity rootEntity, in DynamicBuffer<LinkedEntityGroup> group)
            {
                // Read the values from the root (also via lookup so the type is not aliased).
                var clipProp   = ClipLookup[rootEntity];
                var timeProp   = TimeLookup[rootEntity];
                var visProp    = VisLookup[rootEntity];
                var shadowProp = ShadowLookup[rootEntity];

                for (int i = 0; i < group.Length; i++)
                {
                    var child = group[i].Value;
                    if (child == rootEntity) continue;

                    if (ClipLookup.HasComponent(child))   ClipLookup[child]   = clipProp;
                    if (TimeLookup.HasComponent(child))   TimeLookup[child]   = timeProp;
                    if (VisLookup.HasComponent(child))    VisLookup[child]    = visProp;
                    if (ShadowLookup.HasComponent(child)) ShadowLookup[child] = shadowProp;
                }
            }
        }
    }
}
