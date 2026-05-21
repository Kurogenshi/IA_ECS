using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;

namespace Crowd.Systems
{
    /// <summary>
    /// Phase 12 — spawns the requested number of cars across the available lanes, one-shot.
    /// Cars are distributed round-robin and placed at progressive node offsets on each lane
    /// so we don't stack two cars on the same waypoint. The system disables itself after
    /// running once.
    /// </summary>
    [UpdateInGroup(typeof(InitializationSystemGroup))]
    public partial struct CarSpawnerSystem : ISystem
    {
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<CarSpawnerConfig>();
        }

        public void OnUpdate(ref SystemState state)
        {
            state.Enabled = false;

            var configEntity = SystemAPI.GetSingletonEntity<CarSpawnerConfig>();
            var config       = SystemAPI.GetComponent<CarSpawnerConfig>(configEntity);
            var laneBuffer   = SystemAPI.GetBuffer<CarSpawnLaneRef>(configEntity);

            if (config.CarPrefab == Entity.Null)
            {
                UnityEngine.Debug.LogWarning("[CarSpawnerSystem] No CarPrefab assigned — skipping car spawn.");
                return;
            }

            int laneCount = laneBuffer.Length;
            if (laneCount == 0 || config.Count == 0)
            {
                UnityEngine.Debug.Log("[CarSpawnerSystem] No lanes or Count=0 — no cars spawned.");
                return;
            }

            var laneNodeLookup = SystemAPI.GetBufferLookup<LaneNode>(true);
            var laneTagLookup  = SystemAPI.GetComponentLookup<LaneTag>(true);

            var lanes = new NativeArray<Entity>(laneCount, Allocator.Temp);
            for (int i = 0; i < laneCount; i++) lanes[i] = laneBuffer[i].LaneEntity;

            var ecb = new EntityCommandBuffer(Allocator.Temp);
            int spawned = 0;
            for (int i = 0; i < config.Count; i++)
            {
                Entity lane = lanes[i % laneCount];
                if (!laneNodeLookup.HasBuffer(lane)) continue;
                var nodes = laneNodeLookup[lane];
                if (nodes.Length < 2) continue;

                // Distribute cars along the lane: car k of N on this lane lands at node k * step.
                int onThisLane    = (i / laneCount) + 1;
                int nodeIndex     = math.min((onThisLane - 1) % math.max(1, nodes.Length - 1), nodes.Length - 2);

                float3 a = nodes[nodeIndex].Position;
                float3 b = nodes[nodeIndex + 1].Position;
                float3 dir = b - a; dir.y = 0f;
                if (math.lengthsq(dir) < 1e-6f) dir = new float3(0f, 0f, 1f);
                dir = math.normalize(dir);

                float3 pos = a; pos.y = 0f;
                quaternion yaw = quaternion.RotateY(math.atan2(dir.x, dir.z));

                float laneMax = laneTagLookup.HasComponent(lane) ? laneTagLookup[lane].MaxSpeed : 10f;

                var car = ecb.Instantiate(config.CarPrefab);
                ecb.SetComponent(car, LocalTransform.FromPositionRotation(pos, yaw));
                ecb.SetComponent(car, new CarMovement
                {
                    Velocity     = float3.zero,
                    CurrentSpeed = 0f,
                    TargetSpeed  = laneMax,
                    Forward      = dir,
                });
                ecb.SetComponent(car, new LaneFollower
                {
                    CurrentLane  = lane,
                    NodeIndex    = nodeIndex,
                    LaneMaxSpeed = laneMax,
                });

                spawned++;
            }

            ecb.Playback(state.EntityManager);
            ecb.Dispose();
            lanes.Dispose();

            UnityEngine.Debug.Log($"[CarSpawnerSystem] Spawned {spawned} cars across {laneCount} lanes.");
        }
    }
}
