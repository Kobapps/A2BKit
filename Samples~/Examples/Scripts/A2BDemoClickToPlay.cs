using A2BKit.Unity;
using TMPro;
using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace A2BKit.Demo
{
    /// <summary>
    /// Fires an <see cref="A2BEffectPlayer"/> when you click a target — e.g. click a star, a "+250"
    /// pops off it. Each click spawns a fresh popup, so rapid clicks overlap.
    ///
    /// It detects the click WITHOUT an EventSystem, on purpose. The samples avoid an EventSystem
    /// because the correct uGUI input module depends on the project's input backend, and a mismatch
    /// logs an error on scene open that teaches you nothing about A2BKit. Instead this tests the
    /// pointer against the target's rect directly (<see cref="RectTransformUtility"/>) and reads the
    /// pointer through whichever backend is compiled in — so it works under the legacy Input Manager,
    /// the new Input System, or Both, with no EventSystem, no GraphicRaycaster, and no input module.
    ///
    /// <see cref="Play"/> is public, so a UI Button's onClick or a test can drive it too.
    /// </summary>
    [AddComponentMenu("A2BKit/Demo/Click To Play")]
    public sealed class A2BDemoClickToPlay : MonoBehaviour
    {
        [Tooltip("The effect to play. Defaults to an A2BEffectPlayer on this GameObject.")]
        public A2BEffectPlayer Player;

        [Tooltip("Clicking anywhere inside this rect fires the effect. This is the 'star'.")]
        public RectTransform ClickArea;

        [Tooltip("Camera for the rect hit-test. Leave null: overlay canvases need null (the documented " +
                 "Unity rule), and it is resolved automatically from the canvas otherwise.")]
        public Camera Camera;

        [Tooltip("Randomize the popped score, so repeated clicks feel alive. Off uses FixedText.")]
        public bool RandomScore = true;

        [Min(0)] public int MinScore = 50;
        [Min(0)] public int MaxScore = 500;

        [Tooltip("Text popped when RandomScore is off.")]
        public string FixedText = "+250";

        [Tooltip("Optional star kick on click.")]
        public A2BDemoPunch Punch;

        [Tooltip("Optional running-total label. Adds the popped value on each click.")]
        public TextMeshProUGUI TotalLabel;

        private Canvas _canvas;
        private int _total;

        private void Awake()
        {
            if (Player == null) Player = GetComponent<A2BEffectPlayer>();
            if (ClickArea != null) _canvas = ClickArea.GetComponentInParent<Canvas>();
            WriteTotal();
        }

        private void Update()
        {
            if (ClickArea == null || Player == null) return;
            if (!TryGetPointerDown(out Vector2 screenPos)) return;
            if (!RectTransformUtility.RectangleContainsScreenPoint(ClickArea, screenPos, ResolveCamera())) return;

            Play();
        }

        /// <summary>Plays one popup now. Wire to a Button, or call from anywhere.</summary>
        public void Play()
        {
            if (Player == null) return;

            int value = RandomScore ? RandomValue() : 0;
            string text = RandomScore ? "+" + value : FixedText;

            Player.Play(text);

            if (Punch != null) Punch.Punch();

            // A running total, so the demo rewards clicking. Added on click rather than on arrival
            // because the popped VALUE is known here — the arrival event carries only the item index.
            if (RandomScore) _total += value;
            WriteTotal();
        }

        private Camera ResolveCamera()
        {
            if (Camera != null) return Camera;
            if (_canvas != null && _canvas.renderMode == RenderMode.ScreenSpaceOverlay) return null; // overlay: MUST be null
            return _canvas != null ? _canvas.worldCamera : null;
        }

        private int RandomValue()
        {
            // A sample, not the runtime: UnityEngine.Random is fine here. The package bans it in its
            // own hot paths (determinism), but a click-driven score has no such requirement.
            int min = Mathf.Min(MinScore, MaxScore);
            int max = Mathf.Max(MinScore, MaxScore);
            int step = Mathf.Max(1, (max - min) / 10);
            return Mathf.Clamp(Random.Range(min / step, (max / step) + 1) * step, min, max);
        }

        private void WriteTotal()
        {
            if (TotalLabel != null) TotalLabel.text = "Score: " + _total;
        }

        private bool TryGetPointerDown(out Vector2 pos)
        {
            pos = default;
#if ENABLE_INPUT_SYSTEM
            if (Mouse.current != null && Mouse.current.leftButton.wasPressedThisFrame)
            {
                pos = Mouse.current.position.ReadValue();
                return true;
            }
            if (Touchscreen.current != null && Touchscreen.current.primaryTouch.press.wasPressedThisFrame)
            {
                pos = Touchscreen.current.primaryTouch.position.ReadValue();
                return true;
            }
#endif
#if ENABLE_LEGACY_INPUT_MANAGER
            if (Input.GetMouseButtonDown(0)) { pos = Input.mousePosition; return true; }
            if (Input.touchCount > 0 && Input.GetTouch(0).phase == TouchPhase.Began)
            {
                pos = Input.GetTouch(0).position;
                return true;
            }
#endif
            return false;
        }
    }
}
