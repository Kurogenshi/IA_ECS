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
        /// <summary>Seconds the agent has been near-stationary while wanting to move along a path.
        /// Used by AgentSteeringSystem to skip waypoints when blocked against an obstacle (Phase 1).</summary>
        public float StuckTimer;
    }

    public struct AgentTypeData : IComponentData
    {
        /// <summary>Live behavior — what the agent is doing RIGHT NOW. Swapped to
        /// <see cref="AgentBehavior.Stationary"/> while interacting with a POI (Phase 4)
        /// and reverted to <see cref="BaseBehavior"/> when the agent leaves.</summary>
        public AgentBehavior Behavior;
        /// <summary>Personality — the behavior the agent reverts to when not at a POI.
        /// Set at spawn (Walker or HurriedPedestrian). May occasionally swap between the two
        /// on Idle→Traveling transitions for visual variety.</summary>
        public AgentBehavior BaseBehavior;
        /// <summary>Cruise speed corresponding to <see cref="BaseBehavior"/>. Stored separately
        /// from <see cref="AgentMovement.Speed"/> because the latter is zeroed during interactions.</summary>
        public float BaseSpeed;
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

        // ---- Phase 1: Static obstacles ----

        /// <summary>Beyond this distance from an obstacle's surface, no repulsion is applied. Meters.</summary>
        public float ObstacleRepulsionRadius;
        /// <summary>Weighting of the obstacle repulsion force in the final steering blend.</summary>
        public float ObstacleWeight;
        /// <summary>Cell size of the static obstacle spatial hash. Should be ~= largest expected obstacle footprint.</summary>
        public float ObstacleCellSize;

        // ---- Phase 2: Walkable areas ----

        /// <summary>Cell size of the walkable-area spatial hash. Should be ~= largest expected walkable footprint
        /// (sidewalk length). Typically larger than ObstacleCellSize.</summary>
        public float WalkableCellSize;

        // ---- Phase 3: Local avoidance (ORCA-lite) ----

        /// <summary>How far ahead (seconds) we predict neighbor trajectories. Beyond this horizon
        /// we don't bother diverting — separation handles imminent contacts.</summary>
        public float LookAheadTime;
        /// <summary>Weighting of the anticipation force in the final steering blend.</summary>
        public float AvoidanceWeight;
        /// <summary>If two trajectories would pass within this distance at closest approach,
        /// we treat it as a predicted collision and apply lateral deviation. Roughly 2× agent radius.</summary>
        public float AvoidanceCollisionRadius;
    }

    [InternalBufferCapacity(0)]
    public struct SpawnerPathRef : IBufferElementData
    {
        public Entity PathEntity;
    }
}
