using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace Crowd.Systems
{
    /// <summary>
    /// One-shot bootstrap mirroring <see cref="ObstacleSpatialIndexSystem"/> for walkable areas.
    /// Collects every <see cref="WalkableArea"/> entity, packs them into a flat <see cref="NativeArray{T}"/>,
    /// and builds a cell -> area-index multi-hash for fast inside / closest-point queries.
    ///
    /// Always creates the <see cref="WalkableSpatialIndex"/> singleton, even when no walkable areas
    /// are baked — the <see cref="WalkableSpatialIndex.HasAreas"/> flag tells downstream systems
    /// whether to enforce the constraint, so scenes that haven't been migrated to Phase 2 yet
    /// keep working unchanged.
    /// </summary>
    [UpdateInGroup(typeof(InitializationSystemGroup))]
    public partial struct WalkableSpatialIndexSystem : ISystem
    {
        private EntityQuery _areaQuery;
        private NativeArray<WalkableArea> _areas;
        private NativeParallelMultiHashMap<int, int> _index;

        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<SpawnerConfig>();
            _areaQuery = state.GetEntityQuery(ComponentType.ReadOnly<WalkableArea>());
        }

        public void OnDestroy(ref SystemState state)
        {
            if (_areas.IsCreated) _areas.Dispose();
            if (_index.IsCreated) _index.Dispose();
        }

        public void OnUpdate(ref SystemState state)
        {
            state.Enabled = false;

            var config = SystemAPI.GetSingleton<SpawnerConfig>();
            float cellSize = math.max(1f, config.WalkableCellSize);

            int count = _areaQuery.CalculateEntityCount();
            byte hasAreas;

            if (count == 0)
            {
                _areas = new NativeArray<WalkableArea>(0, Allocator.Persistent);
                _index = new NativeParallelMultiHashMap<int, int>(64, Allocator.Persistent);
                hasAreas = 0;
                UnityEngine.Debug.Log("[WalkableSpatialIndexSystem] No WalkableArea entities found — walkable constraint disabled.");
            }
            else
            {
                _areas = _areaQuery.ToComponentDataArray<WalkableArea>(Allocator.Persistent);
                _index = new NativeParallelMultiHashMap<int, int>(count * 8, Allocator.Persistent);
                for (int i = 0; i < _areas.Length; i++)
                {
                    InsertArea(i, _areas[i], _index, cellSize);
                }
                hasAreas = 1;
                UnityEngine.Debug.Log($"[WalkableSpatialIndexSystem] Built spatial index for {_areas.Length} walkable areas (cell size {cellSize}m).");
            }

            var singleton = state.EntityManager.CreateEntity();
            state.EntityManager.SetName(singleton, "WalkableSpatialIndex");
            state.EntityManager.AddComponentData(singleton, new WalkableSpatialIndex
            {
                Areas           = _areas,
                CellToAreaIndex = _index,
                CellSize        = cellSize,
                IsBuilt         = 1,
                HasAreas        = hasAreas,
            });
        }

        private static void InsertArea(int areaIndex, in WalkableArea area,
            NativeParallelMultiHashMap<int, int> index, float cellSize)
        {
            ObstacleMath.WorldAABBOfShape(area.Shape, area.Center, area.HalfExtents, area.RotationY,
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
                    index.Add(hash, areaIndex);
                }
            }
        }
    }
}
