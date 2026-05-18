using Unity.Burst;
using Unity.Entities;
using Unity.Mathematics;

namespace Crowd.Systems
{
    /// <summary>
    /// Reads <see cref="AgentMovement.Velocity"/> to pick Idle/Walk, advances clip time,
    /// and pushes the result into the GPU-bound <see cref="AnimClipProperty"/> /
    /// <see cref="AnimTimeProperty"/> material property components.
    ///
    /// Runs after movement so we sample this frame's velocity.
    /// </summary>
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(AgentMovementSystem))]
    public partial struct AgentAnimationSystem : ISystem
    {
        private int _frameCounter;

        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<AgentTag>();
            state.RequireForUpdate<SpawnerConfig>();
            _frameCounter = 0;
        }

        public void OnUpdate(ref SystemState state)
        {
            var config = SystemAPI.GetSingleton<SpawnerConfig>();
            int interval = math.max(1, config.AnimationInterval);
            _frameCounter++;
            if (interval > 1 && (_frameCounter % interval) != 0) return;

            new AnimateJob
            {
                // Scale DeltaTime by the interval so the animation plays at real-time speed
                // even when we update only every Nth frame.
                DeltaTime = SystemAPI.Time.DeltaTime * interval,
            }.ScheduleParallel();
        }

        [BurstCompile]
        [WithAll(typeof(AgentTag))]
        private partial struct AnimateJob : IJobEntity
        {
            public float DeltaTime;

            // Velocity threshold (m/s^2 of squared length) to flip Idle <-> Walk.
            // Squared to skip the sqrt.
            private const float WalkThresholdSq = 0.04f; // ~0.2 m/s

            private void Execute(
                ref AgentAnimationState anim,
                ref AnimClipProperty clipProp,
                ref AnimTimeProperty timeProp,
                in AgentMovement movement,
                in VATClipTable table)
            {
                float speedSq = math.lengthsq(movement.Velocity);
                AnimClipId desired = speedSq > WalkThresholdSq ? AnimClipId.Walk : AnimClipId.Idle;

                if (desired != anim.CurrentClip)
                {
                    anim.CurrentClip = desired;
                    anim.ClipTime = 0f; // hard cut for v1; blend will come later
                }

                anim.ClipTime += DeltaTime;

                // The clip table tells us how long the clip is; we let the shader
                // do the modulo, but we also wrap on CPU to avoid huge floats.
                int ci = (int)anim.CurrentClip;
                float count = table.ClipFrameCount[ci];
                float fps   = table.ClipFps[ci];
                if (count > 0f && fps > 0f)
                {
                    float duration = count / fps;
                    if (anim.ClipTime > duration * 8f)
                    {
                        anim.ClipTime = math.fmod(anim.ClipTime, duration);
                    }
                }

                clipProp.Value = (float)ci;
                timeProp.Value = anim.ClipTime + anim.PhaseOffset;
            }
        }
    }
}
