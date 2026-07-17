using A2BKit.Unity;
using UnityEngine;

namespace A2BKit.Demo
{
    /// <summary>
    /// Fires several <see cref="A2BEffectPlayer"/>s at once, on a timer — so one reward moment can
    /// send coins to the wallet AND xp orbs to the level bar at the same time, to different places.
    ///
    /// Each player runs its own independent effect; the scheduler advances them all from one tick and
    /// keeps their items apart, so concurrent effects never disturb each other. That is the point of
    /// the scene this drives — and it is guarded by the A2BConcurrentEffectsTests in the package.
    ///
    /// <see cref="Play"/> is public, so a button or another script can trigger the volley too.
    /// </summary>
    [AddComponentMenu("A2BKit/Demo/Multi Play")]
    public sealed class A2BDemoMultiPlay : MonoBehaviour
    {
        [Tooltip("Every one of these plays together on each fire.")]
        public A2BEffectPlayer[] Players;

        [Tooltip("Seconds between volleys. The scene loops so it is watchable without input.")]
        [Min(0.25f)] public float Interval = 2.4f;

        [Tooltip("Seconds before the first volley, so the scene is visible before it fires.")]
        [Min(0f)] public float InitialDelay = 0.5f;

        private float _next;

        private void OnEnable() => _next = Time.time + InitialDelay;

        private void Update()
        {
            if (Time.time < _next) return;
            _next = Time.time + Mathf.Max(0.25f, Interval);
            Play();
        }

        /// <summary>Fires every assigned player now.</summary>
        public void Play()
        {
            if (Players == null) return;
            for (int i = 0; i < Players.Length; i++)
                if (Players[i] != null) Players[i].Play();
        }
    }
}
