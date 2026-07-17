using A2BKit.Unity;
using UnityEngine;

namespace A2BKit.Demo
{
    /// <summary>
    /// Fires an <see cref="A2BEffectPlayer"/> on a timer so a sample scene is watchable the moment you
    /// press Play.
    ///
    /// A timer rather than a button: which uGUI input module is correct depends on whether the project
    /// enabled the new Input System backend, and a sample that logs an input-backend error on open
    /// teaches you nothing about A2BKit. Call <see cref="Play"/> from your own button when you have one.
    /// </summary>
    [AddComponentMenu("A2BKit/Demo/Auto Play")]
    [RequireComponent(typeof(A2BEffectPlayer))]
    public sealed class A2BDemoAutoPlay : MonoBehaviour
    {
        [Tooltip("Seconds between plays.")]
        [Min(0.25f)] public float Interval = 2f;

        [Tooltip("Seconds to wait before the first play, so the scene is visible before it fires.")]
        [Min(0f)] public float InitialDelay = 0.4f;

        [Tooltip("Optional text passed to the effect — text payloads render it (e.g. \"+250\").")]
        public string Text;

        [Tooltip("Optional numeric value passed to the effect. Text payloads format it without allocating.")]
        public float Value;

        private A2BEffectPlayer _player;
        private float _next;

        private void Awake() => _player = GetComponent<A2BEffectPlayer>();

        private void OnEnable() => _next = Time.time + InitialDelay;

        private void Update()
        {
            if (Time.time < _next) return;
            _next = Time.time + Mathf.Max(0.25f, Interval);
            Play();
        }

        /// <summary>Plays now. Safe to wire to a Button's onClick.</summary>
        public void Play()
        {
            if (_player == null) return;
            if (!string.IsNullOrEmpty(Text)) _player.Play(Text);
            else if (Value != 0f) _player.Play(Value);
            else _player.Play();
        }
    }
}
