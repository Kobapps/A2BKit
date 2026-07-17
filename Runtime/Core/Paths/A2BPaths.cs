using System;
using UnityEngine;

namespace A2BKit.Core
{
    /// <summary>Straight line from origin to destination.</summary>
    [Serializable]
    public sealed class A2BLinearPath : IA2BPath
    {
        public Vector3 Evaluate(in A2BPathContext ctx, float t)
            => Vector3.LerpUnclamped(ctx.Origin, ctx.Destination, t);
    }

    /// <summary>
    /// Quadratic bezier with a designer-facing arc instead of raw control points (FR-9):
    /// you say "arc 2 units up", not "control point at (x,y,z)".
    /// </summary>
    [Serializable]
    public sealed class A2BBezierPath : IA2BPath
    {
        [Tooltip("Peak height of the arc, in working-space units. Negative arcs downward.")]
        public float ArcHeight = 2f;

        [Tooltip("Direction the arc bulges toward. Normalized on use; zero falls back to world up.")]
        public Vector3 ArcDirection = Vector3.up;

        [Tooltip("Where along the path the arc peaks. 0.5 is symmetric.")]
        [Range(0.05f, 0.95f)]
        public float ArcBias = 0.5f;

        [Tooltip("Per-item random variation applied to arc height, as a fraction. 0 = every item arcs identically.")]
        [Range(0f, 1f)]
        public float ArcJitter = 0.25f;

        public Vector3 Evaluate(in A2BPathContext ctx, float t)
        {
            Vector3 dir = ArcDirection.sqrMagnitude < 1e-6f ? Vector3.up : ArcDirection.normalized;

            float height = ArcHeight;
            if (ArcJitter > 0f)
            {
                // Derived from the item seed, so the same item arcs the same way every frame (AD-10).
                var rng = new A2BRandom(ctx.Seed);
                height *= 1f + rng.NextFloat(-ArcJitter, ArcJitter);
            }

            // Control point sits off the chord at ArcBias. Because the control point is only ever
            // *added* to the lerp, B(0)==Origin and B(1)==Destination hold exactly (AD-13).
            Vector3 chord = Vector3.LerpUnclamped(ctx.Origin, ctx.Destination, ArcBias);
            Vector3 control = chord + dir * height;

            float u = 1f - t;
            return (u * u) * ctx.Origin + (2f * u * t) * control + (t * t) * ctx.Destination;
        }
    }

    /// <summary>
    /// Parameterized procedural motion: a spiral/wave around the straight line, decaying to zero at
    /// both ends so the endpoint invariant holds (AD-13) no matter how wild the parameters get.
    /// </summary>
    [Serializable]
    public sealed class A2BProceduralPath : IA2BPath
    {
        [Tooltip("Peak lateral displacement from the straight line, in working-space units.")]
        public float Amplitude = 1f;

        [Tooltip("Number of oscillations over the full path.")]
        public float Frequency = 2f;

        [Tooltip("Rotate each item's oscillation plane by its index, producing a spiral spread.")]
        public bool SpiralByItem = true;

        [Tooltip("Randomize each item's starting phase so a burst does not move as one rigid sheet.")]
        [Range(0f, 1f)]
        public float PhaseJitter = 1f;

        public Vector3 Evaluate(in A2BPathContext ctx, float t)
        {
            Vector3 straight = Vector3.LerpUnclamped(ctx.Origin, ctx.Destination, t);

            Vector3 axis = ctx.Destination - ctx.Origin;
            if (axis.sqrMagnitude < 1e-6f)
                return straight;
            axis.Normalize();

            // Any vector not parallel to the axis works as a basis seed.
            Vector3 reference = Mathf.Abs(Vector3.Dot(axis, Vector3.up)) > 0.99f ? Vector3.right : Vector3.up;
            Vector3 lateral = Vector3.Cross(axis, reference).normalized;
            Vector3 binormal = Vector3.Cross(axis, lateral);

            float phase = 0f;
            if (SpiralByItem && ctx.ItemCount > 1)
                phase += (ctx.ItemIndex / (float)ctx.ItemCount) * Mathf.PI * 2f;
            if (PhaseJitter > 0f)
            {
                var rng = new A2BRandom(ctx.Seed);
                phase += rng.NextFloat(0f, Mathf.PI * 2f) * PhaseJitter;
            }

            // sin(pi*t) envelope: exactly 0 at t=0 and t=1, so both endpoints stay pinned (AD-13).
            float envelope = Mathf.Sin(t * Mathf.PI);
            float angle = phase + t * Frequency * Mathf.PI * 2f;
            float scale = Amplitude * envelope;

            return straight + lateral * (Mathf.Cos(angle) * scale) + binormal * (Mathf.Sin(angle) * scale);
        }
    }

    /// <summary>
    /// Coins explode out of the chest, hang for a beat, then get sucked into the wallet.
    ///
    /// This is the canonical f2p reward, and it is a genuinely different shape from
    /// <see cref="A2BBezierPath"/> + scatter. Scatter (on emission) offsets where an item *starts*,
    /// so an arc with scatter reads as "coins already spread out, now flying" — one continuous move.
    /// The burst reads as two beats: outward against the direction of travel, a pause at the peak,
    /// then the gather. That pause is what sells it, and no single-arc configuration produces it.
    ///
    /// Built-in rather than an example, because a sample that hand-rolled the most-wanted shape in the
    /// genre would be teaching people to route around the package.
    ///
    /// Direction and radius come from the item seed, so the spray is deterministic (AD-10) and the
    /// same effect replays identically. Pure and allocation-free like every path (AD-13).
    /// </summary>
    [Serializable]
    public sealed class A2BBurstGatherPath : IA2BPath
    {
        [Tooltip("How far items fly outward before turning for the destination, in working-space units. " +
                 "Canvas units are pixels, so this wants to be much larger there than in world space.")]
        public float BurstRadius = 2f;

        [Tooltip("Fraction of the flight spent bursting outward. The rest is hold + gather.")]
        [Range(0.05f, 0.9f)] public float BurstFraction = 0.35f;

        [Tooltip("Fraction of the flight spent hanging at the peak before the gather. This pause is " +
                 "what makes it read as two beats rather than one curve. 0 turns immediately.")]
        [Range(0f, 0.5f)] public float HoldFraction = 0.12f;

        [Tooltip("Per-axis weighting of the spray. (1,1,0) keeps a Canvas or 2D burst in-plane; " +
                 "(1,1,1) sprays in all directions for World3D.")]
        public Vector3 BurstAxisWeights = new Vector3(1f, 1f, 0f);

        [Tooltip("Per-item variation in burst distance, as a fraction. 0 makes every item reach the " +
                 "same radius, which reads as a ring rather than a spray.")]
        [Range(0f, 1f)] public float RadiusJitter = 0.45f;

        [Tooltip("Bias the spray. Up-ish looks like the chest threw them; zero sprays evenly.")]
        public Vector3 BurstBias = new Vector3(0f, 0.6f, 0f);

        [Tooltip("Outward motion. Decelerating (OutCubic/OutQuad) reads as a pop that runs out of steam.")]
        public A2BEaseKind BurstEase = A2BEaseKind.OutCubic;

        [Tooltip("Inward motion. Accelerating (InCubic/InBack) reads as the wallet pulling them in.")]
        public A2BEaseKind GatherEase = A2BEaseKind.InCubic;

        public Vector3 Evaluate(in A2BPathContext ctx, float t)
        {
            Vector3 peak = ResolvePeak(in ctx);

            float burstEnd = Mathf.Clamp(BurstFraction, 0.05f, 0.9f);
            float gatherStart = Mathf.Min(burstEnd + HoldFraction, 0.95f);

            // Phase 1 — outward.
            if (t <= burstEnd)
            {
                float u = burstEnd <= 0f ? 1f : t / burstEnd;
                return Vector3.LerpUnclamped(ctx.Origin, peak, A2BEase.Evaluate(BurstEase, u));
            }

            // Phase 2 — hang. Deliberately dead time: the beat between the two moves.
            if (t <= gatherStart) return peak;

            // Phase 3 — gather. Pinned so Evaluate(1) is exactly the destination (AD-13), which is
            // what keeps arrival (t >= 1) honest.
            float g = (t - gatherStart) / (1f - gatherStart);
            return Vector3.LerpUnclamped(peak, ctx.Destination, A2BEase.Evaluate(GatherEase, g));
        }

        /// <summary>Where this item flies out to, derived from its seed alone.</summary>
        private Vector3 ResolvePeak(in A2BPathContext ctx)
        {
            var rng = new A2BRandom(ctx.Seed);

            Vector3 dir = rng.NextUnitSphere();
            dir = new Vector3(
                dir.x * BurstAxisWeights.x,
                dir.y * BurstAxisWeights.y,
                dir.z * BurstAxisWeights.z) + BurstBias;

            // Every weight zeroed, or the bias exactly cancelling the direction: fall back to "up"
            // rather than normalizing a zero vector into NaN and teleporting the item (AD-8).
            dir = dir.sqrMagnitude < 1e-6f ? Vector3.up : dir.normalized;

            float radius = BurstRadius;
            if (RadiusJitter > 0f) radius *= 1f + rng.NextFloat(-RadiusJitter, RadiusJitter);

            return ctx.Origin + dir * radius;
        }
    }

    /// <summary>
    /// Shared conformance contract for AD-13. Any custom path can run this; the test suite runs it
    /// against every built-in. Lives in Core (not the test assembly) so package consumers can assert
    /// their own paths without depending on our tests.
    /// </summary>
    public static class A2BPathConformance
    {
        /// <summary>
        /// Returns true when the path lands exactly on both endpoints. This is the invariant that
        /// makes Arrival (t &gt;= 1) meaningful; a path that fails it silently breaks FirstItemArrived.
        /// </summary>
        public static bool SatisfiesEndpointInvariant(IA2BPath path, in A2BPathContext ctx, float tolerance = 1e-3f)
        {
            if (path == null) return false;
            Vector3 atZero = path.Evaluate(in ctx, 0f);
            Vector3 atOne = path.Evaluate(in ctx, 1f);
            return Vector3.Distance(atZero, ctx.Origin) <= tolerance
                && Vector3.Distance(atOne, ctx.Destination) <= tolerance;
        }
    }
}
