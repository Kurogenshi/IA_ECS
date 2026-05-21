using Unity.Entities;
using Unity.Mathematics;

namespace Crowd
{
    /// <summary>Coarse category of a point of interest. Drives gizmo color and (eventually) which
    /// agent archetypes are interested. Free to extend.</summary>
    public enum POIType : byte
    {
        Bench = 0,
        ShopWindow = 1,
        Fountain = 2,
        BusStop = 3,
        StreetFood = 4,
        Generic = 5,
    }

    /// <summary>
    /// A point of interest in the scene that agents can travel to and interact with (Phase 4).
    /// One entity per <see cref="Authoring.POIAuthoring"/>; modified by <see cref="Systems.AgentGoalSystem"/>
    /// to track live occupancy.
    /// </summary>
    public struct PointOfInterest : IComponentData
    {
        public POIType Type;
        public float3 Position;
        public int Capacity;
        public int CurrentOccupancy;
        /// <summary>Distance from the POI's position at which an arriving agent considers itself "there".</summary>
        public float InteractionRadius;
        /// <summary>x = min seconds an agent dwells, y = max. Sampled at arrival.</summary>
        public float2 DwellTimeRange;
    }

    /// <summary>Buffer attached to the crowd-spawner singleton. Lists every POI entity in the
    /// scene so the goal system can pick destinations without doing a per-frame entity query.</summary>
    [InternalBufferCapacity(0)]
    public struct POIRef : IBufferElementData
    {
        public Entity POIEntity;
    }

    public enum AgentGoalState : byte
    {
        /// <summary>No goal — the goal system will pick one (or fall through to PathFollower if no POIs exist).</summary>
        Idle = 0,
        /// <summary>Traveling to <see cref="AgentGoal.TargetPOI"/>.</summary>
        Traveling = 1,
        /// <summary>Arrived; occupying the POI for <see cref="AgentGoal.Timer"/> more seconds.</summary>
        Interacting = 2,
    }

    /// <summary>
    /// Per-agent goal state (Phase 4). When <see cref="State"/> is anything other than
    /// <see cref="AgentGoalState.Idle"/>, the steering system uses <see cref="TargetPosition"/>
    /// instead of the agent's <see cref="PathFollower"/>. Stationary agents keep their
    /// default Idle and fall through to the existing wander behavior.
    /// </summary>
    public struct AgentGoal : IComponentData
    {
        public Entity TargetPOI;
        public AgentGoalState State;
        /// <summary>Dwell countdown when Interacting; ignored otherwise.</summary>
        public float Timer;
        /// <summary>Cached at travel start so the steering job doesn't need a per-frame POI lookup.</summary>
        public float3 TargetPosition;
    }
}
