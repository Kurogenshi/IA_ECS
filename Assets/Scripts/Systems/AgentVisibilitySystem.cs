using Unity.Burst;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
using UnityEngine;

namespace Crowd.Systems
{
    /// <summary>
    /// Once per frame, updates every agent's per-instance visibility flags from the active
    /// camera's distance. The shader reads these and early-exits in the vertex stage when
    /// the agent is too far. This skips fragment shading entirely for distant agents and
    /// stops them casting shadows past a shorter threshold (the bulk of GPU savings).
    /// </summary>
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(AgentMovementSystem))]
    public partial class AgentVisibilitySystem : SystemBase
    {
        private Camera _cachedCamera;

        protected override void OnCreate()
        {
            RequireForUpdate<SpawnerConfig>();
            RequireForUpdate<AgentTag>();
        }

        protected override void OnUpdate()
        {
            // Camera.main does a string-tag lookup; cache it.
            if (_cachedCamera == null || !_cachedCamera.isActiveAndEnabled)
                _cachedCamera = Camera.main;
            if (_cachedCamera == null) return;

            var config = SystemAPI.GetSingleton<SpawnerConfig>();
            float maxRenderSq = config.MaxRenderDistance * config.MaxRenderDistance;
            float maxShadowSq = config.MaxShadowDistance * config.MaxShadowDistance;

            new VisibilityJob
            {
                CameraPos    = _cachedCamera.transform.position,
                MaxRenderSq  = maxRenderSq,
                MaxShadowSq  = maxShadowSq,
            }.ScheduleParallel();
        }

        [BurstCompile]
        [WithAll(typeof(AgentTag))]
        private partial struct VisibilityJob : IJobEntity
        {
            public float3 CameraPos;
            public float  MaxRenderSq;
            public float  MaxShadowSq;

            private void Execute(
                ref AgentVisibleProperty visProp,
                ref AgentShadowVisibleProperty shadowProp,
                in LocalTransform transform)
            {
                float distSq = math.distancesq(transform.Position, CameraPos);
                visProp.Value    = distSq < MaxRenderSq ? 1f : 0f;
                shadowProp.Value = distSq < MaxShadowSq ? 1f : 0f;
            }
        }
    }
}
