using System;
using UnityEngine;

namespace A2BKit.Core
{
    /// <summary>
    /// What happens to a running effect when an endpoint is destroyed mid-flight (FR-13).
    /// It must never throw a MissingReferenceException into gameplay code (AD-8).
    /// </summary>
    public enum A2BEndpointLostPolicy
    {
        /// <summary>Stop, raise Cancelled(EndpointLost), return items to the pool. The default.</summary>
        Cancel,

        /// <summary>Keep flying to the endpoint's last known position, then complete normally.</summary>
        UseLastKnownPosition
    }

    /// <summary>
    /// The authored, reusable configuration of an effect. One definition drives many concurrent
    /// effects, so it is treated as READ-ONLY at play time — the scheduler copies what it needs
    /// into the slot and never mutates this (FR-3).
    ///
    /// A plain [Serializable] class, not a ScriptableObject: Core bans ScriptableObject (AD-1).
    /// A2BEffectAsset in A2BKit.Unity wraps one of these for designers; A2BEffectBuilder builds one
    /// in code. Both are peers producing the same type (FR-1, FR-2).
    /// </summary>
    [Serializable]
    public sealed class A2BEffectDefinition
    {
        [Header("Motion")]
        [Tooltip("Seconds for one item to travel origin to destination, before per-item delay.")]
        [Min(0.01f)] public float Duration = 0.8f;

        [Tooltip("Extra random variation in per-item duration, as a fraction. 0 = every item takes exactly Duration.")]
        [Range(0f, 1f)] public float DurationJitter = 0.15f;

        [SerializeReference, A2BSubclassSelector, Tooltip("Trajectory from origin to destination.")]
        public IA2BPath Path = new A2BBezierPath();

        [SerializeReference, A2BSubclassSelector, Tooltip("Reparameterizes progress over time. Applied before the path.")]
        public IA2BEasing Easing = new A2BStandardEasing(A2BEaseKind.InOutCubic);

        [SerializeReference, A2BSubclassSelector, Tooltip("How many items, when they release, and how they scatter.")]
        public IA2BEmission Emission = new A2BBurstEmission();

        [Header("Appearance")]
        [Tooltip("Item scale over normalized progress. Lets items pop in and shrink on arrival.")]
        public AnimationCurve ScaleOverProgress = AnimationCurve.Constant(0f, 1f, 1f);

        [Tooltip("Item tint over normalized progress. Alpha here drives fades.")]
        public Gradient ColorOverProgress = CreateDefaultGradient();

        [Tooltip("Face the item along its direction of travel. Off keeps the payload's authored rotation.")]
        public bool AlignToVelocity = false;

        [Header("Behaviour")]
        [Tooltip("Use unscaled time so the effect keeps playing while the game is paused.")]
        public bool UseUnscaledTime = false;

        public A2BEndpointLostPolicy EndpointLostPolicy = A2BEndpointLostPolicy.Cancel;

        [Tooltip("Items pre-allocated per pool for this definition. Sized to the common burst avoids growth.")]
        [Min(0)] public int PrewarmCount = 16;

        /// <summary>
        /// Deep-copies this definition so callers can override without mutating the source asset (FR-2).
        /// Allocates — intended for setup, never for the tick path.
        /// </summary>
        public A2BEffectDefinition Clone()
        {
            var clone = (A2BEffectDefinition)MemberwiseClone();

            // Null in, null out. The scheduler treats a null curve as "no curve" (`?? 1f`), so
            // fabricating an empty AnimationCurve here would invent state the source never had —
            // and an empty curve evaluates to 0, which would silently scale every item to nothing.
            clone.ScaleOverProgress = ScaleOverProgress != null
                ? new AnimationCurve(ScaleOverProgress.keys)
                : null;
            clone.ColorOverProgress = ColorOverProgress != null ? CloneGradient(ColorOverProgress) : null;
            // Strategies (Path/Easing/Emission) are shared BY REFERENCE, not deep-copied: they may be
            // user-authored types this assembly cannot clone. They are therefore IMMUTABLE-after-
            // authoring, not stateless — they hold config. Anything that wants to change a setting
            // must copy first (A2BEffectBuilder.EnsureBurst does exactly that); mutating one in place
            // edits every effect that shares it, including the source asset.
            return clone;
        }

        /// <summary>
        /// Validates the parts of a definition that do not depend on runtime endpoints.
        /// Returns false and fills <paramref name="error"/> rather than throwing (AD-8).
        /// </summary>
        public bool Validate(out string error)
        {
            if (Path == null) { error = "Path is not set."; return false; }
            if (Easing == null) { error = "Easing is not set."; return false; }
            if (Emission == null) { error = "Emission is not set."; return false; }
            if (Duration <= 0f) { error = "Duration must be greater than zero."; return false; }
            error = null;
            return true;
        }

        internal float ResolveItemDuration(uint effectSeed, int itemIndex)
        {
            if (DurationJitter <= 0f) return Duration;
            var rng = new A2BRandom(A2BRandom.DeriveSeed(effectSeed ^ 0x5A5A5A5Au, itemIndex));
            return Mathf.Max(0.01f, Duration * (1f + rng.NextFloat(-DurationJitter, DurationJitter)));
        }

        private static Gradient CreateDefaultGradient()
        {
            var g = new Gradient();
            g.SetKeys(
                new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
                new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(1f, 1f) });
            return g;
        }

        private static Gradient CloneGradient(Gradient source)
        {
            if (source == null) return CreateDefaultGradient();
            var g = new Gradient();
            g.SetKeys(source.colorKeys, source.alphaKeys);
            g.mode = source.mode;
            return g;
        }
    }
}
