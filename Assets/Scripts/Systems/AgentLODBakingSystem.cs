using Unity.Collections;
using Unity.Entities;
using Unity.Rendering;

namespace Crowd.Systems
{
    /// <summary>
    /// Runs during baking, AFTER every Baker has produced its entities. For each agent
    /// root (entity with AgentTag), iterate its LinkedEntityGroup buffer (= all entities
    /// created from the prefab hierarchy, including LOD child render entities) and copy
    /// the per-instance material property components onto each child that has a
    /// MaterialMeshInfo (i.e. is a real renderable). The runtime
    /// PropagateMaterialPropsToLODSystem then keeps their values in sync with the root's
    /// every frame so per-LOD draw calls see the right _AnimTime / _AnimClip / visibility.
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.BakingSystem)]
    [UpdateInGroup(typeof(PostBakingSystemGroup))]
    public partial class AgentLODBakingSystem : SystemBase
    {
        protected override void OnUpdate()
        {
            var ecb = new EntityCommandBuffer(Allocator.TempJob);

            // IncludePrefab is critical: the agent root is a prefab entity during baking
            // (it gets instantiated later), so by default it's hidden from queries.
            Entities
                .WithAll<AgentTag>()
                .WithEntityQueryOptions(EntityQueryOptions.IncludePrefab | EntityQueryOptions.IncludeDisabledEntities)
                .ForEach((Entity rootEntity, in DynamicBuffer<LinkedEntityGroup> group) =>
                {
                    int added = 0;
                    for (int i = 0; i < group.Length; i++)
                    {
                        var child = group[i].Value;
                        if (child == rootEntity) continue;
                        if (!EntityManager.HasComponent<MaterialMeshInfo>(child)) continue;

                        if (!EntityManager.HasComponent<AnimClipProperty>(child))
                            ecb.AddComponent(child, new AnimClipProperty { Value = 0f });
                        if (!EntityManager.HasComponent<AnimTimeProperty>(child))
                            ecb.AddComponent(child, new AnimTimeProperty { Value = 0f });
                        if (!EntityManager.HasComponent<AgentVisibleProperty>(child))
                            ecb.AddComponent(child, new AgentVisibleProperty { Value = 1f });
                        if (!EntityManager.HasComponent<AgentShadowVisibleProperty>(child))
                            ecb.AddComponent(child, new AgentShadowVisibleProperty { Value = 1f });
                        added++;
                    }
                    if (added > 0)
                        UnityEngine.Debug.Log($"[AgentLODBakingSystem] Added per-instance props to {added} LOD render children of {rootEntity}");
                })
                .WithoutBurst()
                .Run();

            ecb.Playback(EntityManager);
            ecb.Dispose();
        }
    }
}
