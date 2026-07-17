using System.Collections.Generic;
using A2BKit.Core;
using NUnit.Framework;
using UnityEngine;

namespace A2BKit.Tests.EditMode
{
    /// <summary>
    /// AD-13 — every path is pinned at both ends: Evaluate(ctx, 0) == Origin and
    /// Evaluate(ctx, 1) == Destination, within tolerance.
    ///
    /// This is not a nicety. Arrival is defined as t &gt;= 1 and ONLY t &gt;= 1, so a path that drifts
    /// from its destination makes ItemArrived and FirstItemArrived — the package's reason to exist —
    /// silently meaningless. The parameter sets below deliberately include the values a designer
    /// actually reaches for when something looks wrong: an enormous arc, a zero-length hop where
    /// Origin == Destination, and a frequency high enough to alias.
    /// </summary>
    [TestFixture]
    internal sealed class A2BPathConformanceTests
    {
        private static IEnumerable<A2BPathContext> Contexts()
        {
            // Ordinary.
            yield return new A2BPathContext(new Vector3(0f, 0f, 0f), new Vector3(5f, 2f, -3f), 3, 8, 12345u);
            // Zero-length: Origin == Destination. Degenerate axis; the procedural path must early-out.
            yield return new A2BPathContext(new Vector3(2f, 2f, 2f), new Vector3(2f, 2f, 2f), 0, 1, 777u);
            // Perfectly vertical: exercises the procedural path's basis-reference fallback.
            yield return new A2BPathContext(new Vector3(0f, 0f, 0f), new Vector3(0f, 10f, 0f), 5, 6, 99u);
            // Single item: ItemCount == 1 disables spiral-by-item.
            yield return new A2BPathContext(new Vector3(-4f, 1f, 8f), new Vector3(12f, -6f, 0.5f), 0, 1, 1u);
            // Seed 0: the RNG's absorbing-state guard must not perturb the endpoints.
            yield return new A2BPathContext(new Vector3(1f, 1f, 1f), new Vector3(-1f, -1f, -1f), 2, 4, 0u);
        }

        private static IEnumerable<TestCaseData> PathCases()
        {
            var paths = new Dictionary<string, IA2BPath>
            {
                { "Linear", new A2BLinearPath() },
                { "Bezier.Default", new A2BBezierPath() },
                { "Bezier.HugeArc", new A2BBezierPath { ArcHeight = 5000f, ArcBias = 0.95f, ArcJitter = 1f } },
                { "Bezier.NegativeArc", new A2BBezierPath { ArcHeight = -250f, ArcBias = 0.05f, ArcJitter = 0f } },
                // ArcDirection zero must fall back to world up rather than produce NaN.
                { "Bezier.ZeroArcDirection", new A2BBezierPath { ArcHeight = 3f, ArcDirection = Vector3.zero } },
                { "Procedural.Default", new A2BProceduralPath() },
                { "Procedural.HighFrequency", new A2BProceduralPath { Amplitude = 1000f, Frequency = 500f, PhaseJitter = 1f, SpiralByItem = true } },
                { "Procedural.NoJitter", new A2BProceduralPath { Amplitude = 25f, Frequency = 0f, PhaseJitter = 0f, SpiralByItem = false } },
            };

            int contextIndex = 0;
            foreach (A2BPathContext ctx in Contexts())
            {
                foreach (KeyValuePair<string, IA2BPath> entry in paths)
                    yield return new TestCaseData(entry.Value, ctx).SetName(entry.Key + "_ctx" + contextIndex);
                contextIndex++;
            }
        }

        [TestCaseSource(nameof(PathCases))]
        public void EveryBuiltInPath_SatisfiesEndpointInvariant(IA2BPath path, A2BPathContext ctx)
        {
            Assert.IsTrue(
                A2BPathConformance.SatisfiesEndpointInvariant(path, in ctx),
                "AD-13 violated: " + path.GetType().Name + " does not land on both endpoints. " +
                "Evaluate(0)=" + path.Evaluate(in ctx, 0f) + " expected " + ctx.Origin + "; " +
                "Evaluate(1)=" + path.Evaluate(in ctx, 1f) + " expected " + ctx.Destination + ".");
        }

        [TestCaseSource(nameof(PathCases))]
        public void EveryBuiltInPath_IsFiniteAcrossTheWholeDomain(IA2BPath path, A2BPathContext ctx)
        {
            for (int i = 0; i <= 64; i++)
            {
                float t = i / 64f;
                Vector3 p = path.Evaluate(in ctx, t);
                Assert.IsFalse(float.IsNaN(p.x) || float.IsNaN(p.y) || float.IsNaN(p.z),
                    path.GetType().Name + " produced NaN at t=" + t);
                Assert.IsFalse(float.IsInfinity(p.x) || float.IsInfinity(p.y) || float.IsInfinity(p.z),
                    path.GetType().Name + " produced Infinity at t=" + t);
            }
        }

        [TestCaseSource(nameof(PathCases))]
        public void EveryBuiltInPath_IsPure_SameInputsGiveSameOutput(IA2BPath path, A2BPathContext ctx)
        {
            // AD-13: no frame state. Evaluating twice, and out of order, must not change the answer.
            Vector3 first = path.Evaluate(in ctx, 0.37f);
            path.Evaluate(in ctx, 0.9f);
            path.Evaluate(in ctx, 0.1f);
            Vector3 second = path.Evaluate(in ctx, 0.37f);
            Assert.AreEqual(first, second, path.GetType().Name + " is not a pure function of (ctx, t).");
        }

        [Test]
        public void PathConformance_RejectsNullPath()
        {
            var ctx = new A2BPathContext(Vector3.zero, Vector3.one, 0, 1, 1u);
            Assert.IsFalse(A2BPathConformance.SatisfiesEndpointInvariant(null, in ctx));
        }

        [Test]
        public void PathConformance_DetectsAPathThatDoesNotLandOnTheDestination()
        {
            // The conformance contract must actually be capable of failing, or asserting it proves nothing.
            var ctx = new A2BPathContext(Vector3.zero, new Vector3(10f, 0f, 0f), 0, 1, 1u);
            Assert.IsFalse(A2BPathConformance.SatisfiesEndpointInvariant(new DriftingPath(), in ctx));
        }

        /// <summary>Deliberately non-conforming: stops short of the destination.</summary>
        private sealed class DriftingPath : IA2BPath
        {
            public Vector3 Evaluate(in A2BPathContext ctx, float t)
                => Vector3.LerpUnclamped(ctx.Origin, ctx.Destination, t * 0.9f);
        }
    }
}
