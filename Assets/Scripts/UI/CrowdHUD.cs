using Unity.Entities;
using UnityEngine;

namespace Crowd.UI
{
    /// <summary>
    /// Drop this on any GameObject in the scene to display FPS and live agent count.
    /// Uses OnGUI so it needs zero scene setup.
    /// </summary>
    public class CrowdHUD : MonoBehaviour
    {
        [Tooltip("How often (seconds) the FPS readout refreshes.")]
        public float FpsRefreshInterval = 0.25f;

        [Tooltip("Optional cap for target framerate. -1 = uncapped.")]
        public int TargetFrameRate = -1;

        [Tooltip("Disable VSync at startup so framerate is not capped to the display.")]
        public bool DisableVSync = true;

        private float _accumulatedTime;
        private int _accumulatedFrames;
        private float _fps;

        private EntityQuery _agentQuery;
        private World _world;
        private bool _queryReady;

        private void Awake()
        {
            if (DisableVSync) QualitySettings.vSyncCount = 0;
            Application.targetFrameRate = TargetFrameRate;
        }

        private void Update()
        {
            _accumulatedTime += Time.unscaledDeltaTime;
            _accumulatedFrames++;
            if (_accumulatedTime >= FpsRefreshInterval)
            {
                _fps = _accumulatedFrames / _accumulatedTime;
                _accumulatedTime = 0f;
                _accumulatedFrames = 0;
            }

            if (!_queryReady)
            {
                _world = World.DefaultGameObjectInjectionWorld;
                if (_world != null && _world.IsCreated)
                {
                    _agentQuery = _world.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<AgentTag>());
                    _queryReady = true;
                }
            }
        }

        private void OnGUI()
        {
            int agentCount = 0;
            if (_queryReady && _world != null && _world.IsCreated)
            {
                agentCount = _agentQuery.CalculateEntityCount();
            }

            GUI.color = new Color(0f, 0f, 0f, 0.55f);
            GUI.DrawTexture(new Rect(8, 8, 240, 76), Texture2D.whiteTexture);
            GUI.color = Color.white;

            var style = new GUIStyle(GUI.skin.label)
            {
                fontSize = 20,
                fontStyle = FontStyle.Bold,
            };
            style.normal.textColor = (_fps >= 60f) ? new Color(0.55f, 1f, 0.55f)
                : (_fps >= 30f ? new Color(1f, 0.85f, 0.3f) : new Color(1f, 0.4f, 0.4f));

            GUI.Label(new Rect(16, 12, 230, 28), $"FPS  : {_fps,5:F1}", style);

            var agentStyle = new GUIStyle(style);
            agentStyle.normal.textColor = Color.white;
            GUI.Label(new Rect(16, 44, 230, 28), $"Agents: {agentCount}", agentStyle);
        }
    }
}
