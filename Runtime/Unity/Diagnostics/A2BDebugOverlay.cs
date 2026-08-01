using System.Text;
using A2BKit.Core;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace A2BKit.Unity
{
    /// <summary>
    /// Answers "what is running and where did my pool go" without a profiler session (FR-22, UJ-4).
    ///
    /// Built with TMP rather than OnGUI on purpose. IMGUI allocates every frame by construction, and
    /// a diagnostic that violated the package's own headline constraint — while displaying that
    /// constraint's numbers — would be self-refuting (AD-20).
    ///
    /// The text is rebuilt into a reused StringBuilder and only when a tracked value actually
    /// changes, then handed to TMP via SetText(StringBuilder) — the supported non-allocating path.
    /// So the overlay costs zero per-frame allocation while displayed.
    /// </summary>
    [AddComponentMenu("A2BKit/A2B Debug Overlay")]
    public sealed class A2BDebugOverlay : MonoBehaviour
    {
        [Tooltip("Toggle the overlay at runtime.")]
        public bool Visible = true;

        [Tooltip("Key that toggles the overlay. None disables the shortcut.")]
        public KeyCode ToggleKey = KeyCode.F3;

        [Tooltip("Seconds between refreshes. The overlay is a diagnostic, not a per-frame readout.")]
        [Min(0f)] public float RefreshInterval = 0.1f;

        private readonly StringBuilder _sb = new StringBuilder(256);
        private TextMeshProUGUI _label;
        private Canvas _canvas;
        private float _nextRefresh;

        // Last rendered values. The dirty check is what keeps this allocation-free: without it we
        // would rebuild an identical string every frame.
        private int _lastEffects = -1;
        private int _lastItems = -1;
        private int _lastSlots = -1;

        private static A2BDebugOverlay _instance;

        /// <summary>Creates the overlay if absent, or toggles it. Wired to a menu item in the editor.</summary>
        public static A2BDebugOverlay Show()
        {
            if (_instance != null)
            {
                _instance.Visible = true;
                return _instance;
            }

            var go = new GameObject("[A2BKit Debug Overlay]");
            if (Application.isPlaying) DontDestroyOnLoad(go);        // throws in edit mode
            else go.hideFlags = HideFlags.DontSave;                  // DontSave would outlive play mode
            _instance = go.AddComponent<A2BDebugOverlay>();
            return _instance;
        }

        public static void Hide()
        {
            if (_instance != null) _instance.Visible = false;
        }

        private void Awake()
        {
            if (_instance != null && _instance != this) { Destroy(gameObject); return; }
            _instance = this;
            BuildUI();
        }

        private void BuildUI()
        {
            _canvas = gameObject.AddComponent<Canvas>();
            _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            _canvas.sortingOrder = short.MaxValue; // Always on top of the game's own UI.
            gameObject.AddComponent<CanvasScaler>().uiScaleMode = CanvasScaler.ScaleMode.ConstantPixelSize;

            var labelGo = new GameObject("Label", typeof(RectTransform));
            labelGo.transform.SetParent(transform, false);

            var rect = (RectTransform)labelGo.transform;
            rect.anchorMin = new Vector2(0f, 1f);
            rect.anchorMax = new Vector2(0f, 1f);
            rect.pivot = new Vector2(0f, 1f);
            rect.anchoredPosition = new Vector2(12f, -12f);
            rect.sizeDelta = new Vector2(420f, 160f);

            _label = labelGo.AddComponent<TextMeshProUGUI>();
            _label.fontSize = 18f;
            _label.color = new Color(0.6f, 1f, 0.7f, 0.95f);
            _label.alignment = TextAlignmentOptions.TopLeft;
            _label.raycastTarget = false;
        }

        private void Update()
        {
            if (ToggleKey != KeyCode.None && Input.GetKeyDown(ToggleKey))
                Visible = !Visible;

            if (_label != null && _label.enabled != Visible)
                _label.enabled = Visible;

            if (!Visible) return;

            if (Time.unscaledTime < _nextRefresh) return;
            _nextRefresh = Time.unscaledTime + RefreshInterval;

            Refresh();
        }

        private void Refresh()
        {
            if (!A2BRunner.Exists)
            {
                // Do not bootstrap a runner just to look at it — that would make observing the system
                // change the system.
                if (_lastEffects != 0)
                {
                    _lastEffects = 0;
                    _sb.Clear();
                    _sb.Append("A2BKit — no runner active");
                    _label.SetText(_sb);
                }
                return;
            }

            A2BScheduler scheduler = A2BRunner.Scheduler;
            int effects = scheduler.ActiveEffectCount;
            int items = scheduler.ActiveItemCount;
            int slots = scheduler.SlotCapacity;

            // The dirty check (AD-20): identical values rebuild nothing.
            if (effects == _lastEffects && items == _lastItems && slots == _lastSlots) return;
            _lastEffects = effects;
            _lastItems = items;
            _lastSlots = slots;

            // NOT StringBuilder.Append(int): on Unity's Mono corlib that routes through
            // value.ToString() and allocates a string every call — the exact thing AD-20 forbids,
            // and it would have made this overlay allocate while displaying the zero-allocation
            // numbers. A2BNumberFormat.AppendLong emits digits by hand instead.
            _sb.Clear();
            _sb.Append("A2BKit\n");
            _sb.Append("  active effects : "); A2BNumberFormat.AppendLong(_sb, effects); _sb.Append('\n');
            _sb.Append("  items in flight: "); A2BNumberFormat.AppendLong(_sb, items); _sb.Append('\n');
            _sb.Append("  effect slots   : "); A2BNumberFormat.AppendLong(_sb, slots); _sb.Append(" (pooled)\n");

            _label.SetText(_sb);
        }

        private void OnDestroy()
        {
            if (_instance == this) _instance = null;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics() => _instance = null;
    }
}
