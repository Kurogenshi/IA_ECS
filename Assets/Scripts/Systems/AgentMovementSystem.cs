using Unity.Burst;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;

namespace Crowd.Systems
{
    /// <summary>
    /// Applies velocity to LocalTransform and rotates agents to face their movement direction.
    /// </summary>
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(AgentSteeringSystem))]
    public partial struct AgentMovementSystem : ISystem
    {
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<SpawnerConfig>();
        }

        public void OnUpdate(ref SystemState state)
        {
            new MovementJob
            {
                DeltaTime = SystemAPI.Time.DeltaTime,
            }.ScheduleParallel();
        }

        [BurstCompile]
        [WithAll(typeof(AgentTag))]
        private partial struct MovementJob : IJobEntity
        {
            public float DeltaTime;

            private void Execute(ref LocalTransform transform, in AgentMovement movement)
            {
                transform.Position += movement.Velocity * DeltaTime;
                transform.Position.y = 0f;

                float speedSq = math.lengthsq(movement.Velocity);
                if (speedSq > 0.01f)
                {
                    float angle = math.atan2(movement.Velocity.x, movement.Velocity.z);
                    transform.Rotation = quaternion.RotateY(angle);
                }
            }
        }
    }
}
