using Unity.Entities;
using Unity.Mathematics;

namespace Crowd
{
    public enum AgentBehavior : byte
    {
        HurriedPedestrian = 0,
        Walker = 1,
        Stationary = 2,
    }

    public struct AgentTag : IComponentData { }

    public struct AgentMovement : IComponentData
    {
        public float Speed;
        public float3 Velocity;
    }

    public struct AgentTypeData : IComponentData
    {
        public AgentBehavior Behavior;
    }

    public struct PathFollower : IComponentData
    {
        public Entity PathEntity;
        public int CurrentWaypoint;
        public byte ReverseDirection;
        public float3 HomePosition;
    }

    [InternalBufferCapacity(0)]
    public struct Waypoint : IBufferElementData
    {
        public float3 Position;
    }

    public struct SpawnerConfig : IComponentData
    {
        public Entity AgentPrefab;
        public int Count;
        public float3 ZoneCenter;
        public float3 ZoneSize;

        public float MinSpeedHurried;
        public float MaxSpeedHurried;
        public float MinSpeedWalker;
        public float MaxSpeedWalker;
        public float StationaryWanderRadius;

        public float PercentHurried;
        public float PercentWalker;

        public uint RandomSeed;

        public float SeparationRadius;
        public float NeighborCellSize;
        public float WaypointArriveDistance;
        public float SteeringSmoothing;

        // ---- Performance tuning ----

        /// <summary>Beyond this distance from the active camera, agents are early-rejected in the shader.</summary>
        public float MaxRenderDistance;
        /// <summary>Beyond this distance, agents stop casting shadows. Should be &lt; MaxRenderDistance.</summary>
        public float MaxShadowDistance;
        /// <summary>Run AgentSteeringSystem every Nth frame (1 = each frame, 2 = every other, ...).</summary>
        public int SteeringInterval;
        /// <summary>Run AgentAnimationSystem every Nth frame (1 = each frame).</summary>
        public int AnimationInterval;
    }

    [InternalBufferCapacity(0)]
    public struct SpawnerPathRef : IBufferElementData
    {
        public Entity PathEntity;
    }
}
