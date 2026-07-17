using System;
using System.Collections.Generic;
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
    /// One intermediate Bézier control point for <see cref="A2BSplinePath"/>.
    ///
    /// Stored relative to the chord, never in absolute units: <see cref="Along"/> slides it between the
    /// endpoints and <see cref="Offset"/> pushes it off the chord in MULTIPLES OF THE CHORD LENGTH. That
    /// is the property that makes one authored curve read the same in world space (metres) and on a
    /// Canvas (pixels) — the arc is a fraction of the distance, so it scales with the effect instead of
    /// vanishing to a two-pixel wobble the way an absolute height does.
    /// </summary>
    [Serializable]
    public sealed class A2BSplineControlPoint
    {
        [Tooltip("Position between the endpoints, 0 = origin, 1 = destination.")]
        [Range(0f, 1f)] public float Along = 0.5f;

        [Tooltip("Offset off the chord, in multiples of the straight-line distance. (0, 0.5, 0) bulges " +
                 "half the endpoint distance upward, in any space.")]
        public Vector3 Offset = new Vector3(0f, 0.5f, 0f);

        public A2BSplineControlPoint() { }

        public A2BSplineControlPoint(float along, Vector3 offset)
        {
            Along = along;
            Offset = offset;
        }
    }

    /// <summary>
    /// A Bézier curve of ANY number of control points (FR-9/FR-10) — the multi-point peer of
    /// <see cref="A2BBezierPath"/>. Add points to sculpt an S-curve, a loop-round, a double-hump; the
    /// editor gives each one a Scene handle.
    ///
    /// The curve is the Bézier over [origin, control points…, destination], evaluated with De Casteljau,
    /// so B(0)==origin and B(1)==destination hold exactly no matter how the middle is shaped (AD-13).
    /// Control offsets are fractions of the chord (see <see cref="A2BSplineControlPoint"/>), so the shape
    /// is space-independent — the same asset arcs visibly whether it plays in metres or pixels.
    ///
    /// Pure and allocation-free on the tick path (AD-3): the De Casteljau scratch is a reused buffer,
    /// grown only when the control-point COUNT changes (an authoring action), never per evaluation.
    /// </summary>
    [Serializable]
    public sealed class A2BSplinePath : IA2BPath
    {
        [Tooltip("Intermediate Bézier control points. The curve still starts at the origin and ends at " +
                 "the destination; these bend the path between them. Add or drag them in the Scene view.")]
        public List<A2BSplineControlPoint> ControlPoints = new List<A2BSplineControlPoint>
        {
            new A2BSplineControlPoint(0.5f, new Vector3(0f, 0.5f, 0f))
        };

        [Tooltip("Per-item random variation applied to the control offsets, as a fraction. 0 = every " +
                 "item follows the identical curve.")]
        [Range(0f, 1f)] public float Jitter = 0f;

        // Reused De Casteljau workspace. NonSerialized: it is runtime scratch, not authored state, and
        // paths are shared by reference across items/effects — safe because the tick is single-threaded
        // and each Evaluate uses the buffer only within its own call (AD-3).
        [NonSerialized] private Vector3[] _work;

        public Vector3 Evaluate(in A2BPathContext ctx, float t)
        {
            int cpCount = ControlPoints?.Count ?? 0;
            int n = cpCount + 2;

            if (_work == null || _work.Length < n) _work = new Vector3[n];

            Vector3 origin = ctx.Origin;
            Vector3 destination = ctx.Destination;
            float len = Vector3.Distance(origin, destination);

            _work[0] = origin;
            _work[n - 1] = destination;

            var rng = new A2BRandom(ctx.Seed);
            for (int i = 0; i < cpCount; i++)
            {
                A2BSplineControlPoint cp = ControlPoints[i];
                Vector3 off = cp.Offset;
                // Same item seed drives the same jitter every frame, so an item holds its curve (AD-10).
                if (Jitter > 0f) off *= 1f + rng.NextFloat(-Jitter, Jitter);
                _work[i + 1] = Vector3.LerpUnclamped(origin, destination, cp.Along) + off * len;
            }

            // In-place De Casteljau. At t=0 every lerp keeps the left point, so the result is _work[0]
            // == origin; at t=1 it walks to _work[n-1] == destination. The endpoint invariant is
            // therefore structural, not something the control points can break (AD-13).
            for (int k = 1; k < n; k++)
                for (int i = 0; i < n - k; i++)
                    _work[i] = Vector3.LerpUnclamped(_work[i], _work[i + 1], t);

            return _work[0];
        }
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
