using Unity.Entities;
using UnityEngine;

namespace Crowd.UI
{
    /// <summary>
    /// Drop this on any GameObject in the scene to display FPS / live agent count and to
    /// dial the crowd size up and down at runtime (live demo control).
    ///
    /// Controls:
    ///   - On-screen buttons: -1000 / -500 / -100 / +100 / +500 / +1000.
    ///   - Preset hotkeys: 1=500, 2=1000, 3=2000, 4=5000, 5=10000.
    ///   - +/- keys: increment by 500 (with Shift: by 100).
    ///   - Direct input field: type a number, press Apply.
    ///
    /// Uses OnGUI so the only scene setup is dropping the component on any active GameObject.
    /// </summary>
    public class CrowdHUD : MonoBehaviour
    {
        [Tooltip("How often (seconds) the FPS readout refreshes.")]
        public float FpsRefreshInterval = 0.25f;

        [Tooltip("Optional cap for target framerate. -1 = uncapped.")]
        public int TargetFrameRate = -1;

        [Tooltip("Disable VSync at startup so framerate is not capped to the display.")]
        public bool DisableVSync = true;

        [Tooltip("Maximum target count allowed via the HUD controls.")]
        public int MaxTarget = 20000;

        private float _accumulatedTime;
        private int _accumulatedFrames;
        private float _fps;

        private EntityQuery _agentQuery;
        private World _world;
        private bool _queryReady;

        private string _targetInput = "5000";
        private int _lastKnownTarget = -1;

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

            HandleHotkeys();
        }

        private void HandleHotkeys()
        {
            if (!_queryReady) return;

            // Number-key presets — quick demo cycling.
            if (Input.GetKeyDown(KeyCode.Alpha1)) SetTarget(500);
            else if (Input.GetKeyDown(KeyCode.Alpha2)) SetTarget(1000);
            else if (Input.GetKeyDown(KeyCode.Alpha3)) SetTarget(2000);
            else if (Input.GetKeyDown(KeyCode.Alpha4)) SetTarget(5000);
            else if (Input.GetKeyDown(KeyCode.Alpha5)) SetTarget(10000);

            // +/- relative — large step normally, small with Shift.
            bool shift = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
            int step = shift ? 100 : 500;
            if (Input.GetKeyDown(KeyCode.KeypadPlus) || Input.GetKeyDown(KeyCode.Equals) || Input.GetKeyDown(KeyCode.Plus))
            {
                AdjustTarget(step);
            }
            else if (Input.GetKeyDown(KeyCode.KeypadMinus) || Input.GetKeyDown(KeyCode.Minus))
            {
                AdjustTarget(-step);
            }
        }

        private bool TryGetTarget(out Entity entity, out CrowdRuntimeTarget target)
        {
            entity = Entity.Null;
            target = default;
            if (_world == null || !_world.IsCreated) return false;
            var em = _world.EntityManager;
            var q = em.CreateEntityQuery(ComponentType.ReadWrite<CrowdRuntimeTarget>());
            if (q.CalculateEntityCount() == 0) return false;
            entity = q.GetSingletonEntity();
            target = em.GetComponentData<CrowdRuntimeTarget>(entity);
            return true;
        }

        private void SetTarget(int count)
        {
            if (!TryGetTarget(out var entity, out var target)) return;
            target.TargetCount = Mathf.Clamp(count, 0, MaxTarget);
            _world.EntityManager.SetComponentData(entity, target);
            _targetInput = target.TargetCount.ToString();
        }

        private void AdjustTarget(int delta)
        {
            if (!TryGetTarget(out var entity, out var target)) return;
            target.TargetCount = Mathf.Clamp(target.TargetCount + delta, 0, MaxTarget);
            _world.EntityManager.SetComponentData(entity, target);
            _targetInput = target.TargetCount.ToString();
        }

        private void OnGUI()
        {
            int agentCount = 0;
            int targetCount = 0;
            bool hasTarget = false;

            if (_queryReady && _world != null && _world.IsCreated)
            {
                agentCount = _agentQuery.CalculateEntityCount();
                if (TryGetTarget(out _, out var t))
                {
                    targetCount = t.TargetCount;
                    hasTarget = true;
                    if (_lastKnownTarget != targetCount)
                    {
                        _targetInput = targetCount.ToString();
                        _lastKnownTarget = targetCount;
                    }
                }
            }

            // Background panel: large enough for stats + controls + hint line.
            const int panelW = 360;
            const int panelH = 178;
            GUI.color = new Color(0f, 0f, 0f, 0.6f);
            GUI.DrawTexture(new Rect(8, 8, panelW, panelH), Texture2D.whiteTexture);
            GUI.color = Color.white;

            var statStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize  = 20,
                fontStyle = FontStyle.Bold,
            };
            statStyle.normal.textColor = (_fps >= 60f) ? new Color(0.55f, 1f, 0.55f)
                : (_fps >= 30f ? new Color(1f, 0.85f, 0.3f) : new Color(1f, 0.4f, 0.4f));

            GUI.Label(new Rect(16, 12, panelW - 16, 28), $"FPS  : {_fps,5:F1}", statStyle);

            var whiteStyle = new GUIStyle(statStyle);
            whiteStyle.normal.textColor = Color.white;
            string agentLine = hasTarget
                ? $"Agents: {agentCount} / {targetCount}"
                : $"Agents: {agentCount}";
            GUI.Label(new Rect(16, 40, panelW - 16, 28), agentLine, whiteStyle);

            // Control row 1: -1000 / -500 / -100 / +100 / +500 / +1000.
            var btnStyle = new GUIStyle(GUI.skin.button) { fontSize = 13, fontStyle = FontStyle.Bold };
            float bx = 16f, by = 74f, bw = 52f, bh = 24f, gap = 4f;
            if (GUI.Button(new Rect(bx,                        by, bw, bh), "-1000", btnStyle)) AdjustTarget(-1000);
            if (GUI.Button(new Rect(bx + 1*(bw+gap),           by, bw, bh), "-500",  btnStyle)) AdjustTarget(-500);
            if (GUI.Button(new Rect(bx + 2*(bw+gap),           by, bw, bh), "-100",  btnStyle)) AdjustTarget(-100);
            if (GUI.Button(new Rect(bx + 3*(bw+gap),           by, bw, bh), "+100",  btnStyle)) AdjustTarget(+100);
            if (GUI.Button(new Rect(bx + 4*(bw+gap),           by, bw, bh), "+500",  btnStyle)) AdjustTarget(+500);
            if (GUI.Button(new Rect(bx + 5*(bw+gap),           by, bw, bh), "+1000", btnStyle)) AdjustTarget(+1000);

            // Control row 2: direct input + apply button.
            var inputStyle = new GUIStyle(GUI.skin.textField) { fontSize = 13, alignment = TextAnchor.MiddleCenter };
            float ix = 16f, iy = 104f, iw = 88f, ih = 24f;
            _targetInput = GUI.TextField(new Rect(ix, iy, iw, ih), _targetInput, 6, inputStyle);
            if (GUI.Button(new Rect(ix + iw + 6f, iy, 64f, ih), "Apply", btnStyle))
            {
                if (int.TryParse(_targetInput, out int parsed)) SetTarget(parsed);
            }

            // Preset row.
            float px = ix + iw + 76f, py = iy, pw = 38f, pgap = 3f;
            if (GUI.Button(new Rect(px,                py, pw, ih), "500",   btnStyle)) SetTarget(500);
            if (GUI.Button(new Rect(px + 1*(pw+pgap),  py, pw, ih), "1k",    btnStyle)) SetTarget(1000);
            if (GUI.Button(new Rect(px + 2*(pw+pgap),  py, pw, ih), "5k",    btnStyle)) SetTarget(5000);

            // Hint line.
            var hintStyle = new GUIStyle(GUI.skin.label) { fontSize = 11 };
            hintStyle.normal.textColor = new Color(0.75f, 0.75f, 0.75f);
            GUI.Label(new Rect(16, 134, panelW - 16, 18), "Hotkeys: 1=500  2=1k  3=2k  4=5k  5=10k", hintStyle);
            GUI.Label(new Rect(16, 152, panelW - 16, 18), "+/- = ±500  (Shift: ±100)", hintStyle);
        }
    }
}
