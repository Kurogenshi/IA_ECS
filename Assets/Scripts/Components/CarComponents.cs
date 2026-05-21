using Unity.Entities;
using Unity.Mathematics;

namespace Crowd
{
    /// <summary>
    /// Tag identifying a car entity. Cars are a separate archetype from pedestrians — no
    /// AgentTag, no animation components. Static mesh only.
    /// </summary>
    public struct CarTag : IComponentData { }

    /// <summary>
    /// Runtime kinematic state for a car. Cars are not driven by Unity Physics; their velocity
    /// is integrated by <see cref="Crowd.Systems.CarMovementSystem"/> using realistic asymmetric
    /// accel / brake values, capped by <see cref="CarTypeData.MaxSpeed"/>.
    /// </summary>
    public struct CarMovement : IComponentData
    {
        /// <summary>World-space velocity (m/s). Derived from <see cref="CurrentSpeed"/> × forward direction.</summary>
        public float3 Velocity;
        /// <summary>Current scalar speed in m/s. Always non-negative — cars never go backwards in Phase 12.</summary>
        public float CurrentSpeed;
        /// <summary>Target speed (m/s) set by upstream systems. Phase 12: capped to lane MaxSpeed.</summary>
        public float TargetSpeed;
        /// <summary>Forward facing direction in world space (unit vector on XZ plane).</summary>
        public float3 Forward;
    }

    /// <summary>
    /// Static per-car parameters baked from <see cref="Crowd.Authoring.CarAuthoring"/>.
    /// </summary>
    public struct CarTypeData : IComponentData
    {
        /// <summary>Maximum cruise speed in m/s. Cap of <see cref="CarMovement.TargetSpeed"/>.</summary>
        public float MaxSpeed;
        /// <summary>Positive acceleration in m/s² used when CurrentSpeed &lt; TargetSpeed.</summary>
        public float Acceleration;
        /// <summary>Positive deceleration in m/s² used when CurrentSpeed &gt; TargetSpeed.</summary>
        public float BrakeForce;
        /// <summary>Length (front-to-back) — used by adaptive cruise (Phase 13).</summary>
        public float Length;
        /// <summary>Width (side-to-side) — used by adaptive cruise (Phase 13).</summary>
        public float Width;
    }

    /// <summary>
    /// Lane-following state for a car. The car interpolates between two consecutive nodes
    /// of its current lane; on reaching the last segment it picks a random connection from
    /// the lane's <see cref="LaneConnection"/> buffer and resets to the first segment.
    /// </summary>
    public struct LaneFollower : IComponentData
    {
        /// <summary>Entity carrying the <see cref="LaneTag"/> and <see cref="LaneNode"/> buffer.</summary>
        public Entity CurrentLane;
        /// <summary>Index of the start node of the current segment. Segment runs [NodeIndex, NodeIndex+1].</summary>
        public int NodeIndex;
        /// <summary>Speed cap of the current lane (m/s).</summary>
        public float LaneMaxSpeed;
    }

    /// <summary>Tags a lane entity. Lane entities own a <see cref="LaneNode"/> + <see cref="LaneConnection"/> buffer.</summary>
    public struct LaneTag : IComponentData
    {
        /// <summary>Cached lane speed cap (m/s) — read into <see cref="LaneFollower.LaneMaxSpeed"/> when a car enters the lane.</summary>
        public float MaxSpeed;
    }

    /// <summary>One node (waypoint) of a lane. Cars follow segments between consecutive nodes.</summary>
    [InternalBufferCapacity(0)]
    public struct LaneNode : IBufferElementData
    {
        public float3 Position;
    }

    /// <summary>
    /// One possible successor lane at the end of the current lane. The car-lane-following
    /// system picks one at random on transition (Phase 12). Phase 13 will weight by trafic.
    /// </summary>
    [InternalBufferCapacity(2)]
    public struct LaneConnection : IBufferElementData
    {
        public Entity NextLane;
    }

    /// <summary>Singleton spawner config for cars. Lives on the same entity as the
    /// <see cref="Crowd.Authoring.CarSpawnerAuthoring"/> baker output.</summary>
    public struct CarSpawnerConfig : IComponentData
    {
        public Entity CarPrefab;
        public int Count;
        public uint RandomSeed;
        /// <summary>If > 0, spawn this many cars per second up to <see cref="Count"/> (continuous spawn).
        /// 0 means spawn all at start.</summary>
        public float SpawnRatePerSec;
    }

    /// <summary>Buffer of starting lanes available to <see cref="Crowd.Systems.CarSpawnerSystem"/>.</summary>
    [InternalBufferCapacity(0)]
    public struct CarSpawnLaneRef : IBufferElementData
    {
        public Entity LaneEntity;
    }
}
