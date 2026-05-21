using Crowd.Animation;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

namespace Crowd.Authoring
{
    /// <summary>
    /// Add to the agent GameObject prefab. The prefab's MeshFilter/MeshRenderer should
    /// reference the Mesh + Material produced by the VAT baker. The VATAsset assigned
    /// here is used at bake time to populate the per-entity clip table.
    /// </summary>
    [DisallowMultipleComponent]
    public class AgentAuthoring : MonoBehaviour
    {
        [Tooltip("Baked VAT data for this agent's mesh. Required for animation.")]
        public VATAsset VAT;

        private class Baker : Baker<AgentAuthoring>
        {
            public override void Bake(AgentAuthoring authoring)
            {
                var entity = GetEntity(TransformUsageFlags.Dynamic);
                AddComponent<AgentTag>(entity);
                AddComponent(entity, new AgentMovement
                {
                    Speed = 1f,
                    Velocity = float3.zero,
                });
                AddComponent(entity, new AgentTypeData
                {
                    Behavior = AgentBehavior.Walker,
                    BaseBehavior = AgentBehavior.Walker,
                    BaseSpeed = 1f,
                });
                AddComponent(entity, new PathFollower
                {
                    PathEntity = Entity.Null,
                    CurrentWaypoint = 0,
                    ReverseDirection = 0,
                    HomePosition = float3.zero,
                });

                // Phase 4: goal/POI state. Initialized to Idle — the goal system will assign
                // a target if any POIs exist in the scene; otherwise the agent falls back to
                // PathFollower behavior.
                AddComponent(entity, new AgentGoal
                {
                    TargetPOI      = Entity.Null,
                    State          = AgentGoalState.Idle,
                    Timer          = 0f,
                    TargetPosition = float3.zero,
                });

                // Animation
                AddComponent(entity, new AgentAnimationState
                {
                    CurrentClip = AnimClipId.Idle,
                    ClipTime = 0f,
                    PhaseOffset = 0f, // randomized at spawn
                });
                AddComponent(entity, new AnimClipProperty { Value = 0f });
                AddComponent(entity, new AnimTimeProperty { Value = 0f });
                AddComponent(entity, new AgentVisibleProperty       { Value = 1f });
                AddComponent(entity, new AgentShadowVisibleProperty { Value = 1f });

                var table = new VATClipTable
                {
                    ClipStartFrame = float4.zero,
                    ClipFrameCount = float4.zero,
                    ClipFps        = new float4(30f),
                    TotalFrames    = 0f,
                    VertexCount    = 0f,
                };

                if (authoring.VAT != null)
                {
                    DependsOn(authoring.VAT);
                    table.TotalFrames = authoring.VAT.TotalFrames;
                    table.VertexCount = authoring.VAT.VertexCount;
                    var clips = authoring.VAT.Clips;
                    if (clips != null)
                    {
                        for (int i = 0; i < clips.Length && i < 4; i++)
                        {
                            table.ClipStartFrame[i] = clips[i].StartFrame;
                            table.ClipFrameCount[i] = clips[i].FrameCount;
                            table.ClipFps[i]        = clips[i].Fps;
                        }
                    }
                }
                else
                {
                    Debug.LogWarning($"[AgentAuthoring] '{authoring.name}' has no VATAsset assigned — animation will be disabled on this agent.", authoring);
                }

                AddComponent(entity, table);

                // For LODGroup setups, each LOD child GameObject is baked into its own
                // render entity by Entities Graphics. We can't AddComponent on those
                // from here (they belong to other Bakers). Instead, AgentLODBakingSystem
                // runs after all Bakers and copies the per-instance material property
                // components onto each LinkedEntityGroup render child. The runtime
                // PropagateMaterialPropsToLODSystem then mirrors values from root every frame.
            }
        }
    }
}
