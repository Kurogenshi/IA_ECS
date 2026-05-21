using Unity.Burst;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;

namespace Crowd.Systems
{
    /// <summary>
    /// Phase 12 — integrates car kinematics: ramp <see cref="CarMovement.CurrentSpeed"/> towards
    /// <see cref="CarMovement.TargetSpeed"/> using asymmetric accel / brake, build the velocity
    /// vector from speed × Forward, push the transform, and yaw the car to match Forward.
    /// </summary>
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(CarLaneFollowingSystem))]
    public partial struct CarMovementSystem : ISystem
    {
        public void OnUpdate(ref SystemState state)
        {
            new CarIntegrateJob
            {
                DeltaTime = SystemAPI.Time.DeltaTime,
            }.ScheduleParallel();
        }

        [BurstCompile]
        [WithAll(typeof(CarTag))]
        private partial struct CarIntegrateJob : IJobEntity
        {
            public float DeltaTime;

            private void Execute(ref LocalTransform xform, ref CarMovement movement, in CarTypeData type)
            {
                float target = math.clamp(movement.TargetSpeed, 0f, type.MaxSpeed);

                if (movement.CurrentSpeed < target)
                {
                    movement.CurrentSpeed = math.min(target, movement.CurrentSpeed + type.Acceleration * DeltaTime);
                }
                else if (movement.CurrentSpeed > target)
                {
                    movement.CurrentSpeed = math.max(target, movement.CurrentSpeed - type.BrakeForce * DeltaTime);
                }

                movement.Velocity = movement.Forward * movement.CurrentSpeed;

                float3 pos = xform.Position + movement.Velocity * DeltaTime;
                pos.y = 0f;
                xform.Position = pos;

                if (math.lengthsq(movement.Forward) > 1e-6f)
                {
                    float yaw = math.atan2(movement.Forward.x, movement.Forward.z);
                    // Smoothly interpolate yaw so cars don't snap at sharp turns. Heading lerp
                    // is cheap — keep it simple here.
                    quaternion target_rot = quaternion.RotateY(yaw);
                    xform.Rotation = math.slerp(xform.Rotation, target_rot, math.saturate(DeltaTime * 6f));
                }
            }
        }
    }
}
