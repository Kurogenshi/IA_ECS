using System.Collections.Generic;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

namespace Crowd.Authoring
{
    /// <summary>
    /// Place on an empty GameObject. Reference a Car prefab (containing
    /// <see cref="CarAuthoring"/>) and the lanes on which cars may spawn. Cars are
    /// distributed across the lanes round-robin with progressive offsets to avoid
    /// stacking on the same node.
    /// </summary>
    [DisallowMultipleComponent]
    public class CarSpawnerAuthoring : MonoBehaviour
    {
        [Header("Prefab")]
        [Tooltip("Car prefab GameObject. Must contain a CarAuthoring + static mesh renderer.")]
        public GameObject CarPrefab;

        [Header("Spawn Settings")]
        [Min(0)] public int Count = 20;
        public uint RandomSeed = 5678u;

        [Tooltip("Lanes on which cars may spawn. Empty = no cars.")]
        public List<LaneAuthoring> StartLanes = new List<LaneAuthoring>();

        [Tooltip("0 = spawn everything at start. >0 = spawn this many cars/sec until Count is reached. Phase 12 only honors the start case; the rate is reserved for Phase 8 wiring.")]
        [Min(0f)] public float SpawnRatePerSec = 0f;

        private void OnDrawGizmosSelected()
        {
            if (StartLanes == null) return;
            Gizmos.color = new Color(1f, 0.4f, 0.1f, 0.9f);
            foreach (var lane in StartLanes)
            {
                if (lane == null) continue;
                Gizmos.DrawWireSphere(lane.transform.position, 1.2f);
            }
        }

        private class Baker : Baker<CarSpawnerAuthoring>
        {
            public override void Bake(CarSpawnerAuthoring authoring)
            {
                var entity = GetEntity(TransformUsageFlags.None);

                Entity prefabEntity = Entity.Null;
                if (authoring.CarPrefab != null)
                {
                    prefabEntity = GetEntity(authoring.CarPrefab, TransformUsageFlags.Dynamic);
                }

                AddComponent(entity, new CarSpawnerConfig
                {
                    CarPrefab       = prefabEntity,
                    Count           = math.max(0, authoring.Count),
                    RandomSeed      = authoring.RandomSeed == 0u ? 1u : authoring.RandomSeed,
                    SpawnRatePerSec = math.max(0f, authoring.SpawnRatePerSec),
                });

                var laneBuf = AddBuffer<CarSpawnLaneRef>(entity);
                if (authoring.StartLanes != null)
                {
                    foreach (var lane in authoring.StartLanes)
                    {
                        if (lane == null) continue;
                        laneBuf.Add(new CarSpawnLaneRef
                        {
                            LaneEntity = GetEntity(lane, TransformUsageFlags.None),
                        });
                    }
                }
            }
        }
    }
}
