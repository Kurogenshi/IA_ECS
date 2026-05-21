using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace Crowd.Systems
{
    /// <summary>
    /// One-shot bootstrap: gathers every <see cref="StaticObstacle"/> baked in the scene,
    /// packs them into a flat array, and builds a spatial multi-hash (cell -> obstacle index).
    /// Both native containers live in the <see cref="ObstacleSpatialIndex"/> singleton, owned
    /// here and disposed in <see cref="OnDestroy"/>.
    ///
    /// Runs in <see cref="InitializationSystemGroup"/>; after the first successful build the
    /// system disables itself. Always creates the singleton, even with zero obstacles, so
    /// downstream systems can rely on it existing.
    /// </summary>
    [UpdateInGroup(typeof(InitializationSystemGroup))]
    public partial struct ObstacleSpatialIndexSystem : ISystem
    {
        private EntityQuery _obstacleQuery;
        private NativeArray<StaticObstacle> _obstacles;
        private NativeParallelMultiHashMap<int, int> _index;

        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<SpawnerConfig>();
            _obstacleQuery = state.GetEntityQuery(ComponentType.ReadOnly<StaticObstacle>());
        }

        public void OnDestroy(ref SystemState state)
        {
            if (_obstacles.IsCreated) _obstacles.Dispose();
            if (_index.IsCreated)     _index.Dispose();
        }

        public void OnUpdate(ref SystemState state)
        {
            state.Enabled = false;

            var config = SystemAPI.GetSingleton<SpawnerConfig>();
            float cellSize = math.max(0.5f, config.ObstacleCellSize);

            int count = _obstacleQuery.CalculateEntityCount();
            if (count == 0)
            {
                _obstacles = new NativeArray<StaticObstacle>(0, Allocator.Persistent);
                _index     = new NativeParallelMultiHashMap<int, int>(64, Allocator.Persistent);
                UnityEngine.Debug.Log("[ObstacleSpatialIndexSystem] No StaticObstacle entities found — empty index created.");
            }
            else
            {
                _obstacles = _obstacleQuery.ToComponentDataArray<StaticObstacle>(Allocator.Persistent);
                _index = new NativeParallelMultiHashMap<int, int>(count * 8, Allocator.Persistent);

                for (int i = 0; i < _obstacles.Length; i++)
                {
                    InsertObstacle(i, _obstacles[i], _index, cellSize);
                }
                UnityEngine.Debug.Log($"[ObstacleSpatialIndexSystem] Built spatial index for {_obstacles.Length} obstacles (cell size {cellSize}m).");
            }

            var singleton = state.EntityManager.CreateEntity();
            state.EntityManager.SetName(singleton, "ObstacleSpatialIndex");
            state.EntityManager.AddComponentData(singleton, new ObstacleSpatialIndex
            {
                Obstacles           = _obstacles,
                CellToObstacleIndex = _index,
                CellSize            = cellSize,
                IsBuilt             = 1,
            });
        }

        /// <summary>
        /// Inserts an obstacle into every cell its world AABB overlaps. Done once at boot.
        /// Cells are hashed with the same <see cref="SpatialHashUtil"/> used for agent neighbors.
        /// </summary>
        private static void InsertObstacle(int obstacleIndex, in StaticObstacle obs,
            NativeParallelMultiHashMap<int, int> index, float cellSize)
        {
            ObstacleMath.WorldAABB(obs, out float3 min, out float3 max);
            int xMin = (int)math.floor(min.x / cellSize);
            int xMax = (int)math.floor(max.x / cellSize);
            int zMin = (int)math.floor(min.z / cellSize);
            int zMax = (int)math.floor(max.z / cellSize);

            for (int x = xMin; x <= xMax; x++)
            {
                for (int z = zMin; z <= zMax; z++)
                {
                    int hash = SpatialHashUtil.HashCell(new int2(x, z));
                    index.Add(hash, obstacleIndex);
                }
            }
        }
    }
}
