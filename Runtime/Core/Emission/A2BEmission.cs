using System;
using UnityEngine;

namespace A2BKit.Core
{
    public enum A2BReleaseMode
    {
        /// <summary>Every item starts at once.</summary>
        AllAtOnce,

        /// <summary>Item n starts at n * StaggerInterval.</summary>
        FixedStagger,

        /// <summary>Items spread evenly across SpreadDuration, regardless of count.</summary>
        SpreadOverDuration
    }

    /// <summary>
    /// Count, stagger and scatter (FR-26).
    ///
    /// Every value here is a pure function of (effectSeed, itemIndex) — the obvious implementation
    /// (build a List&lt;float&gt; of delays and a List&lt;Vector3&gt; of offsets at play) allocates per play
    /// AND scales with item count, breaking both halves of the budget (AD-10).
    ///
    /// Scatter is returned UNITLESS in [-1,1]^3; the presenter assigns units (AD-16), because
    /// emission cannot know whether "radius 50" means pixels or metres.
    /// </summary>
    [Serializable]
    public sealed class A2BBurstEmission : IA2BEmission
    {
        [Tooltip("Minimum number of items to emit.")]
        [Min(1)] public int MinCount = 12;

        [Tooltip("Maximum number of items to emit. Equal to MinCount for a fixed count.")]
        [Min(1)] public int MaxCount = 12;

        public A2BReleaseMode ReleaseMode = A2BReleaseMode.FixedStagger;

        [Tooltip("Seconds between consecutive items when ReleaseMode is FixedStagger.")]
        [Min(0f)] public float StaggerInterval = 0.03f;

        [Tooltip("Total seconds over which all items are released when ReleaseMode is SpreadOverDuration.")]
        [Min(0f)] public float SpreadDuration = 0.4f;

        [Tooltip("Extra random delay added per item, in seconds.")]
        [Min(0f)] public float DelayJitter = 0f;

        [Tooltip("Scatter radius around the origin, in working-space units. The space adapter applies the units.")]
        [SerializeField, Min(0f)] private float _scatterRadius = 0.5f;

        /// <summary>Backs <see cref="IA2BEmission.ScatterRadius"/>; the adapter turns it into units (AD-16).</summary>
        public float ScatterRadius
        {
            get => _scatterRadius;
            set => _scatterRadius = value < 0f ? 0f : value;
        }

        [Tooltip("Per-axis scatter weighting. Zero an axis to scatter in a plane (e.g. (1,1,0) for 2D).")]
        public Vector3 ScatterAxisWeights = Vector3.one;

        /// <summary>
        /// A private copy carrying the same configuration.
        ///
        /// Exists because this type holds authored CONFIG, not nothing: anything that wants to change
        /// a setting must copy first, or it edits the instance every other effect (and the source
        /// asset) is reading. All fields are value types, so a shallow copy is a full copy.
        /// </summary>
        public A2BBurstEmission Copy() => (A2BBurstEmission)MemberwiseClone();

        public int ResolveItemCount(uint effectSeed)
        {
            int min = Mathf.Max(1, MinCount);
            int max = Mathf.Max(min, MaxCount);
            if (min == max) return min;
            var rng = new A2BRandom(effectSeed);
            return rng.NextInt(min, max + 1);
        }

        public float ResolveDelay(uint effectSeed, int itemIndex, int itemCount)
        {
            float baseDelay;
            switch (ReleaseMode)
            {
                case A2BReleaseMode.AllAtOnce:
                    baseDelay = 0f;
                    break;
                case A2BReleaseMode.SpreadOverDuration:
                    baseDelay = itemCount <= 1 ? 0f : (itemIndex / (float)(itemCount - 1)) * SpreadDuration;
                    break;
                default:
                    baseDelay = itemIndex * StaggerInterval;
                    break;
            }

            if (DelayJitter > 0f)
            {
                // Offset the seed so delay jitter and scatter don't draw the same numbers.
                var rng = new A2BRandom(A2BRandom.DeriveSeed(effectSeed ^ 0xA5A5A5A5u, itemIndex));
                baseDelay += rng.NextFloat(0f, DelayJitter);
            }

            return baseDelay < 0f ? 0f : baseDelay;
        }

        public Vector3 ResolveScatter(uint effectSeed, int itemIndex, int itemCount)
        {
            if (ScatterRadius <= 0f) return Vector3.zero;

            var rng = new A2BRandom(A2BRandom.DeriveSeed(effectSeed, itemIndex));
            Vector3 dir = rng.NextUnitSphere();

            // Cube-root keeps points uniform through the volume instead of clustering at the centre.
            float r = Mathf.Pow(rng.NextFloat(), 1f / 3f);

            Vector3 unit = new Vector3(
                dir.x * r * ScatterAxisWeights.x,
                dir.y * r * ScatterAxisWeights.y,
                dir.z * r * ScatterAxisWeights.z);

            return unit;
        }
    }
}
