using UnityEngine;

namespace A2BKit.Core
{
    /// <summary>
    /// A hand-driven clock. The seam that makes motion deterministic (NFR-4): tests advance it in
    /// fixed steps and assert exact positions with no play mode, and the editor preview drives the
    /// identical type — which is why preview cannot drift from runtime behaviour (AD-12, FR-21).
    /// </summary>
    public sealed class A2BManualTimeSource : IA2BTimeSource
    {
        public float DeltaTime { get; private set; }

        /// <summary>Total simulated time advanced so far. Convenience for tests.</summary>
        public float Elapsed { get; private set; }

        public void Advance(float deltaTime)
        {
            DeltaTime = deltaTime;
            Elapsed += deltaTime;
        }

        /// <summary>Sets delta to zero without rewinding Elapsed — parks the clock between steps.</summary>
        public void Hold() => DeltaTime = 0f;

        public void Reset()
        {
            DeltaTime = 0f;
            Elapsed = 0f;
        }
    }

    /// <summary>
    /// Game time — respects Time.timeScale, so effects freeze when the game pauses.
    /// UnityEngine.Time is engine state, not scene graph, so this is permitted in Core (AD-1),
    /// and this type is one of exactly two allowed to read it (AD-12).
    /// </summary>
    public sealed class A2BScaledTimeSource : IA2BTimeSource
    {
        public static readonly A2BScaledTimeSource Instance = new A2BScaledTimeSource();
        public float DeltaTime => UnityEngine.Time.deltaTime;
    }

    /// <summary>
    /// Real time — ignores Time.timeScale. The reward that must keep flying while a paused menu is
    /// open, which is FR-16's motivating case and the reason Tick carries no delta (AD-6).
    /// </summary>
    public sealed class A2BUnscaledTimeSource : IA2BTimeSource
    {
        public static readonly A2BUnscaledTimeSource Instance = new A2BUnscaledTimeSource();
        public float DeltaTime => UnityEngine.Time.unscaledDeltaTime;
    }
}
