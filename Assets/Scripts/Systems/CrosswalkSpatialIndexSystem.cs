using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace Crowd.Systems
{
    /// <summary>
    /// One-shot bootstrap mirroring <see cref="RoadSpatialIndexSystem"/> for crosswalk zones.
    /// Always creates the singleton; <see cref="CrosswalkSpatialIndex.HasCrosswalks"/> tells
    /// downstream systems whether to test the pedestrian-on-crosswalk exemption.
    /// </summary>
    [UpdateInGroup(typeof(InitializationSystemGroup))]
    public partial struct CrosswalkSpatialIndexSystem : ISystem
    {
        private EntityQuery _crosswalkQuery;
        private NativeArray<CrosswalkZone> _crosswalks;
        private NativeParallelMultiHashMap<int, int> _index;

        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<SpawnerConfig>();
            _crosswalkQuery = state.GetEntityQuery(ComponentType.ReadOnly<CrosswalkZone>());
        }

        public void OnDestroy(ref SystemState state)
        {
            if (_crosswalks.IsCreated) _crosswalks.Dispose();
            if (_index.IsCreated)      _index.Dispose();
        }

        public void OnUpdate(ref SystemState state)
        {
            state.Enabled = false;

            var config = SystemAPI.GetSingleton<SpawnerConfig>();
            float cellSize = math.max(1f, config.CrosswalkCellSize);

            int count = _crosswalkQuery.CalculateEntityCount();
            byte hasCrosswalks;

            if (count == 0)
            {
                _crosswalks = new NativeArray<CrosswalkZone>(0, Allocator.Persistent);
                _index      = new NativeParallelMultiHashMap<int, int>(64, Allocator.Persistent);
                hasCrosswalks = 0;
                UnityEngine.Debug.Log("[CrosswalkSpatialIndexSystem] No CrosswalkZone entities found — crosswalk exemption disabled.");
            }
            else
            {
                _crosswalks = _crosswalkQuery.ToComponentDataArray<CrosswalkZone>(Allocator.Persistent);
                _index      = new NativeParallelMultiHashMap<int, int>(count * 4, Allocator.Persistent);
                for (int i = 0; i < _crosswalks.Length; i++)
                {
                    InsertCrosswalk(i, _crosswalks[i], _index, cellSize);
                }
                hasCrosswalks = 1;
                UnityEngine.Debug.Log($"[CrosswalkSpatialIndexSystem] Built spatial index for {_crosswalks.Length} crosswalks (cell size {cellSize}m).");
            }

            var singleton = state.EntityManager.CreateEntity();
            state.EntityManager.SetName(singleton, "CrosswalkSpatialIndex");
            state.EntityManager.AddComponentData(singleton, new CrosswalkSpatialIndex
            {
                Crosswalks            = _crosswalks,
                CellToCrosswalkIndex  = _index,
                CellSize              = cellSize,
                IsBuilt               = 1,
                HasCrosswalks         = hasCrosswalks,
            });
        }

        private static void InsertCrosswalk(int idx, in CrosswalkZone cw,
            NativeParallelMultiHashMap<int, int> index, float cellSize)
        {
            ObstacleMath.WorldAABBOfShape(cw.Shape, cw.Center, cw.HalfExtents, cw.RotationY,
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
                    index.Add(hash, idx);
                }
            }
        }
    }
}
