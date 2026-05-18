using System.Collections.Generic;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

namespace Crowd.Authoring
{
    /// <summary>
    /// Place this on an empty GameObject in the scene. Reference an Agent prefab (containing
    /// AgentAuthoring + MeshRenderer) and one or more PathAuthoring objects.
    /// </summary>
    [DisallowMultipleComponent]
    public class CrowdSpawnerAuthoring : MonoBehaviour
    {
        [Header("Prefab")]
        [Tooltip("Agent prefab GameObject. Must contain an AgentAuthoring + MeshRenderer.")]
        public GameObject AgentPrefab;

        [Header("Spawn Settings")]
        [Min(1)] public int Count = 5000;
        public Vector3 ZoneCenter = Vector3.zero;
        public Vector3 ZoneSize = new Vector3(80f, 0f, 80f);
        public uint RandomSeed = 1234u;

        [Header("Agent Behavior Distribution (must sum <= 1)")]
        [Range(0f, 1f)] public float PercentHurried = 0.3f;
        [Range(0f, 1f)] public float PercentWalker = 0.55f;
        // Remainder => Stationary

        [Header("Speeds (units/sec)")]
        public float HurriedSpeedMin = 3.0f;
        public float HurriedSpeedMax = 4.5f;
        public float WalkerSpeedMin = 1.0f;
        public float WalkerSpeedMax = 1.8f;
        public float StationaryWanderRadius = 1.5f;

        [Header("Steering Tuning")]
        public float SeparationRadius = 1.0f;
        public float NeighborCellSize = 1.5f;
        public float WaypointArriveDistance = 1.2f;
        public float SteeringSmoothing = 5f;

        [Header("Performance")]
        [Tooltip("Beyond this distance from the camera, agents are skipped in the vertex shader.")]
        public float MaxRenderDistance = 80f;
        [Tooltip("Beyond this distance, agents stop casting shadows. Smaller than MaxRenderDistance.")]
        public float MaxShadowDistance = 35f;
        [Tooltip("Run steering every Nth frame (2 = every other frame). 1 disables the optimization.")]
        [Min(1)] public int SteeringInterval = 2;
        [Tooltip("Run animation update every Nth frame. 1 = each frame.")]
        [Min(1)] public int AnimationInterval = 1;

        [Header("Paths")]
        public List<PathAuthoring> Paths = new List<PathAuthoring>();

        private void OnDrawGizmosSelected()
        {
            Gizmos.color = new Color(1f, 0.9f, 0.1f, 0.4f);
            var size = ZoneSize;
            if (size.y < 0.05f) size.y = 0.05f;
            Gizmos.DrawWireCube(transform.position + ZoneCenter, size);
        }

        private class Baker : Baker<CrowdSpawnerAuthoring>
        {
            public override void Bake(CrowdSpawnerAuthoring authoring)
            {
                var entity = GetEntity(TransformUsageFlags.None);

                Entity prefabEntity = Entity.Null;
                if (authoring.AgentPrefab != null)
                {
                    prefabEntity = GetEntity(authoring.AgentPrefab, TransformUsageFlags.Dynamic);
                }

                AddComponent(entity, new SpawnerConfig
                {
                    AgentPrefab = prefabEntity,
                    Count = authoring.Count,
                    ZoneCenter = (float3)(authoring.transform.position + authoring.ZoneCenter),
                    ZoneSize = authoring.ZoneSize,
                    MinSpeedHurried = authoring.HurriedSpeedMin,
                    MaxSpeedHurried = authoring.HurriedSpeedMax,
                    MinSpeedWalker = authoring.WalkerSpeedMin,
                    MaxSpeedWalker = authoring.WalkerSpeedMax,
                    StationaryWanderRadius = authoring.StationaryWanderRadius,
                    PercentHurried = authoring.PercentHurried,
                    PercentWalker = authoring.PercentWalker,
                    RandomSeed = authoring.RandomSeed == 0u ? 1u : authoring.RandomSeed,
                    SeparationRadius = math.max(0.1f, authoring.SeparationRadius),
                    NeighborCellSize = math.max(0.5f, authoring.NeighborCellSize),
                    WaypointArriveDistance = math.max(0.2f, authoring.WaypointArriveDistance),
                    SteeringSmoothing = math.max(0.5f, authoring.SteeringSmoothing),
                    MaxRenderDistance = math.max(5f, authoring.MaxRenderDistance),
                    MaxShadowDistance = math.max(2f, authoring.MaxShadowDistance),
                    SteeringInterval  = math.max(1, authoring.SteeringInterval),
                    AnimationInterval = math.max(1, authoring.AnimationInterval),
                });

                var buffer = AddBuffer<SpawnerPathRef>(entity);
                if (authoring.Paths != null)
                {
                    foreach (var p in authoring.Paths)
                    {
                        if (p == null) continue;
                        buffer.Add(new SpawnerPathRef
                        {
                            PathEntity = GetEntity(p, TransformUsageFlags.None),
                        });
                    }
                }
            }
        }
    }
}
