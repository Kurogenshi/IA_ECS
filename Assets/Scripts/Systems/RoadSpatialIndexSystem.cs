using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace Crowd.Systems
{
    /// <summary>
    /// One-shot bootstrap mirroring <see cref="ObstacleSpatialIndexSystem"/> /
    /// <see cref="WalkableSpatialIndexSystem"/> for road zones (Phase 11).
    /// Always creates the singleton; <see cref="RoadSpatialIndex.HasRoads"/> tells downstream
    /// systems whether the road pushout should be enforced.
    /// </summary>
    [UpdateInGroup(typeof(InitializationSystemGroup))]
    public partial struct RoadSpatialIndexSystem : ISystem
    {
        private EntityQuery _roadQuery;
        private NativeArray<RoadZone> _roads;
        private NativeParallelMultiHashMap<int, int> _index;

        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<SpawnerConfig>();
            _roadQuery = state.GetEntityQuery(ComponentType.ReadOnly<RoadZone>());
        }

        public void OnDestroy(ref SystemState state)
        {
            if (_roads.IsCreated) _roads.Dispose();
            if (_index.IsCreated) _index.Dispose();
        }

        public void OnUpdate(ref SystemState state)
        {
            state.Enabled = false;

            var config = SystemAPI.GetSingleton<SpawnerConfig>();
            float cellSize = math.max(2f, config.RoadCellSize);

            int count = _roadQuery.CalculateEntityCount();
            byte hasRoads;

            if (count == 0)
            {
                _roads = new NativeArray<RoadZone>(0, Allocator.Persistent);
                _index = new NativeParallelMultiHashMap<int, int>(64, Allocator.Persistent);
                hasRoads = 0;
                UnityEngine.Debug.Log("[RoadSpatialIndexSystem] No RoadZone entities found — road constraint disabled.");
            }
            else
            {
                _roads = _roadQuery.ToComponentDataArray<RoadZone>(Allocator.Persistent);
                _index = new NativeParallelMultiHashMap<int, int>(count * 8, Allocator.Persistent);
                for (int i = 0; i < _roads.Length; i++)
                {
                    InsertRoad(i, _roads[i], _index, cellSize);
                }
                hasRoads = 1;
                UnityEngine.Debug.Log($"[RoadSpatialIndexSystem] Built spatial index for {_roads.Length} road zones (cell size {cellSize}m).");
            }

            var singleton = state.EntityManager.CreateEntity();
            state.EntityManager.SetName(singleton, "RoadSpatialIndex");
            state.EntityManager.AddComponentData(singleton, new RoadSpatialIndex
            {
                Roads           = _roads,
                CellToRoadIndex = _index,
                CellSize        = cellSize,
                IsBuilt         = 1,
                HasRoads        = hasRoads,
            });
        }

        private static void InsertRoad(int roadIndex, in RoadZone road,
            NativeParallelMultiHashMap<int, int> index, float cellSize)
        {
            ObstacleMath.WorldAABBOfShape(road.Shape, road.Center, road.HalfExtents, road.RotationY,
                out float3 min, out float3 max);

            int xMin = (int)math.floor(min.x / cellSize);
            int xMax = (int)math.floor(max.x / cellSize);
            int zMin = (int)math.floor(min.z / cellSize);
            int zMax = (int)math.floor(max.z / cellSize);

            for (int x = xMin; x <= xMax; x++)
            {
                for (int z = zMin; z <= zMax; z++)
                {
                    int hash = SpatialHashUtil.HashCell(new int2(x, z));
                    index.Add(hash, roadIndex);
                }
            }
        }
    }
}
