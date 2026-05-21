using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace Crowd
{
    /// <summary>
    /// Zone where cars drive. Pedestrians MUST stay out (snapped back to walkable) unless
    /// they're inside an overlapping <see cref="CrosswalkZone"/>. Geometry is shared with
    /// <see cref="StaticObstacle"/> / <see cref="WalkableArea"/> via <see cref="ObstacleShape"/>
    /// and <see cref="ObstacleMath"/>.
    /// </summary>
    public struct RoadZone : IComponentData
    {
        public ObstacleShape Shape;
        public float3 Center;
        public float3 HalfExtents;
        public float RotationY;
        /// <summary>Speed cap (m/s) cars adopt while on this road. Informational in Phase 11.</summary>
        public float SpeedLimit;
        /// <summary>Number of lanes hinted to the designer; not enforced in Phase 11.</summary>
        public byte LaneCount;
    }

    public enum CrosswalkSignal : byte
    {
        /// <summary>No traffic light — pedestrians always have priority.</summary>
        AlwaysGreen = 0,
        /// <summary>Cycles between pedestrian / car phases (driven in Phase 14).</summary>
        Timed = 1,
        /// <summary>Activated by pedestrian presence (Phase 14+).</summary>
        Demand = 2,
    }

    /// <summary>
    /// A pedestrian crossing area. Overlapping a <see cref="RoadZone"/>, it acts as a "hole"
    /// in the road for pedestrians — agents inside a crosswalk are exempt from the road
    /// pushout (Phase 11). Signal logic comes in Phase 14.
    /// </summary>
    public struct CrosswalkZone : IComponentData
    {
        public ObstacleShape Shape;
        public float3 Center;
        public float3 HalfExtents;
        public float RotationY;
        public CrosswalkSignal SignalType;
        public float SignalCycleDuration;
        public float SignalPhaseOffset;
    }

    /// <summary>
    /// Singleton built once at startup by <see cref="Crowd.Systems.RoadSpatialIndexSystem"/>.
    /// Mirror of <see cref="ObstacleSpatialIndex"/> / <see cref="WalkableSpatialIndex"/>.
    /// </summary>
    public struct RoadSpatialIndex : IComponentData
    {
        public NativeArray<RoadZone> Roads;
        public NativeParallelMultiHashMap<int, int> CellToRoadIndex;
        public float CellSize;
        public byte IsBuilt;
        public byte HasRoads;
    }

    /// <summary>
    /// Singleton built once at startup by <see cref="Crowd.Systems.CrosswalkSpatialIndexSystem"/>.
    /// </summary>
    public struct CrosswalkSpatialIndex : IComponentData
    {
        public NativeArray<CrosswalkZone> Crosswalks;
        public NativeParallelMultiHashMap<int, int> CellToCrosswalkIndex;
        public float CellSize;
        public byte IsBuilt;
        public byte HasCrosswalks;
    }
}
