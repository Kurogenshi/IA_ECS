using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

namespace Crowd.Authoring
{
    /// <summary>
    /// Place on the car prefab. The prefab's MeshFilter/MeshRenderer should hold a static
    /// vehicle mesh (no animation). The Baker emits the runtime data; the spawner system
    /// stamps the lane assignment at spawn time.
    /// </summary>
    [DisallowMultipleComponent]
    public class CarAuthoring : MonoBehaviour
    {
        [Header("Performance")]
        [Tooltip("Top speed in km/h (converted to m/s at bake).")]
        [Min(1f)] public float MaxSpeedKmh = 50f;

        [Tooltip("Acceleration in m/s². 2-3 is typical for city traffic.")]
        [Min(0.1f)] public float Acceleration = 2.5f;

        [Tooltip("Braking deceleration in m/s² (positive number). Higher than acceleration.")]
        [Min(0.1f)] public float BrakeForce = 5.0f;

        [Header("Footprint")]
        [Tooltip("Total length of the car (front-to-back) in meters.")]
        [Min(0.5f)] public float Length = 4.0f;

        [Tooltip("Total width of the car (side-to-side) in meters.")]
        [Min(0.5f)] public float Width = 1.8f;

        private class Baker : Baker<CarAuthoring>
        {
            public override void Bake(CarAuthoring authoring)
            {
                var entity = GetEntity(TransformUsageFlags.Dynamic);
                AddComponent<CarTag>(entity);
                AddComponent(entity, new CarMovement
                {
                    Velocity     = float3.zero,
                    CurrentSpeed = 0f,
                    TargetSpeed  = 0f,
                    Forward      = new float3(0f, 0f, 1f),
                });
                AddComponent(entity, new CarTypeData
                {
                    MaxSpeed     = math.max(1f, authoring.MaxSpeedKmh) * (1f / 3.6f),
                    Acceleration = math.max(0.1f, authoring.Acceleration),
                    BrakeForce   = math.max(0.1f, authoring.BrakeForce),
                    Length       = math.max(0.5f, authoring.Length),
                    Width        = math.max(0.5f, authoring.Width),
                });
                AddComponent(entity, new LaneFollower
                {
                    CurrentLane  = Entity.Null,
                    NodeIndex    = 0,
                    LaneMaxSpeed = 0f,
                });
            }
        }
    }
}
