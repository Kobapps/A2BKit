using System;
using System.Collections.Generic;
using A2BKit.Core;
using NUnit.Framework;
using UnityEngine;

namespace A2BKit.Tests.EditMode
{
    /// <summary>
    /// FR-11 — the easing library. Every kind must be normalized: Evaluate(0) == 0 and
    /// Evaluate(1) == 1. An easing that does not end at 1 never lets t reach 1, and arrival
    /// (AD-13: t &gt;= 1) never fires.
    /// </summary>
    [TestFixture]
    internal sealed class A2BEasingTests
    {
        private const float Tolerance = 1e-4f;

        /// <summary>Kinds that legitimately overshoot [0,1] and are therefore not monotonic by design.</summary>
        private static readonly HashSet<A2BEaseKind> Overshooting = new HashSet<A2BEaseKind>
        {
            A2BEaseKind.InBack, A2BEaseKind.OutBack, A2BEaseKind.InOutBack,
            A2BEaseKind.OutElastic, A2BEaseKind.OutBounce
        };

        private static IEnumerable<A2BEaseKind> AllKinds()
            => (A2BEaseKind[])Enum.GetValues(typeof(A2BEaseKind));

        private static IEnumerable<A2BEaseKind> MonotonicKinds()
        {
            foreach (A2BEaseKind kind in AllKinds())
                if (!Overshooting.Contains(kind))
                    yield return kind;
        }

        [TestCaseSource(nameof(AllKinds))]
        public void Ease_StartsAtZero(A2BEaseKind kind)
            => Assert.AreEqual(0f, A2BEase.Evaluate(kind, 0f), Tolerance, kind + " does not start at 0.");

        [TestCaseSource(nameof(AllKinds))]
        public void Ease_EndsAtOne(A2BEaseKind kind)
            => Assert.AreEqual(1f, A2BEase.Evaluate(kind, 1f), Tolerance, kind + " does not end at 1.");

        [TestCaseSource(nameof(AllKinds))]
        public void Ease_ClampsInputOutsideUnitRange(A2BEaseKind kind)
        {
            Assert.AreEqual(A2BEase.Evaluate(kind, 0f), A2BEase.Evaluate(kind, -5f), Tolerance,
                kind + " does not clamp t below 0.");
            Assert.AreEqual(A2BEase.Evaluate(kind, 1f), A2BEase.Evaluate(kind, 5f), Tolerance,
                kind + " does not clamp t above 1.");
        }

        [TestCaseSource(nameof(AllKinds))]
        public void Ease_IsFiniteAcrossTheDomain(A2BEaseKind kind)
        {
            for (int i = 0; i <= 200; i++)
            {
                float v = A2BEase.Evaluate(kind, i / 200f);
                Assert.IsFalse(float.IsNaN(v) || float.IsInfinity(v), kind + " is not finite at t=" + (i / 200f));
            }
        }

        [TestCaseSource(nameof(MonotonicKinds))]
        public void MonotonicKinds_NeverGoBackwards(A2BEaseKind kind)
        {
            // Back/Elastic/Bounce are excluded: overshoot is their whole point.
            float previous = A2BEase.Evaluate(kind, 0f);
            for (int i = 1; i <= 500; i++)
            {
                float v = A2BEase.Evaluate(kind, i / 500f);
                Assert.GreaterOrEqual(v, previous - Tolerance,
                    kind + " went backwards at t=" + (i / 500f) + " (" + previous + " -> " + v + ").");
                previous = v;
            }
        }

        [TestCaseSource(nameof(MonotonicKinds))]
        public void MonotonicKinds_StayWithinUnitRange(A2BEaseKind kind)
        {
            for (int i = 0; i <= 500; i++)
            {
                float v = A2BEase.Evaluate(kind, i / 500f);
                Assert.GreaterOrEqual(v, -Tolerance, kind + " undershot below 0.");
                Assert.LessOrEqual(v, 1f + Tolerance, kind + " overshot above 1.");
            }
        }

        [TestCaseSource(nameof(AllKinds))]
        public void StandardEasing_DelegatesToTheLibrary(A2BEaseKind kind)
        {
            IA2BEasing easing = new A2BStandardEasing(kind);
            for (int i = 0; i <= 10; i++)
            {
                float t = i / 10f;
                Assert.AreEqual(A2BEase.Evaluate(kind, t), easing.Evaluate(t), Tolerance);
            }
        }

        [Test]
        public void OvershootingKinds_ActuallyOvershoot()
        {
            // Guards the exclusion list above: if these ever stopped overshooting, the monotonicity
            // tests would be silently skipping kinds that no longer need skipping.
            Assert.Less(A2BEase.Evaluate(A2BEaseKind.InBack, 0.2f), 0f, "InBack no longer undershoots.");
            Assert.Greater(A2BEase.Evaluate(A2BEaseKind.OutBack, 0.8f), 1f, "OutBack no longer overshoots.");
            Assert.Greater(A2BEase.Evaluate(A2BEaseKind.OutElastic, 0.15f), 1f, "OutElastic no longer overshoots.");
            Assert.Less(A2BEase.Evaluate(A2BEaseKind.OutBounce, 0.5f), A2BEase.Evaluate(A2BEaseKind.OutBounce, 0.45f),
                "OutBounce no longer dips.");
        }

        [Test]
        public void CurveEasing_EvaluatesItsCurve()
        {
            var easing = new A2BCurveEasing { Curve = AnimationCurve.Linear(0f, 0f, 1f, 1f) };
            Assert.AreEqual(0f, easing.Evaluate(0f), Tolerance);
            Assert.AreEqual(0.5f, easing.Evaluate(0.5f), Tolerance);
            Assert.AreEqual(1f, easing.Evaluate(1f), Tolerance);
        }

        [Test]
        public void CurveEasing_WithNullCurve_FallsBackToLinearRatherThanThrowing()
        {
            // AD-8: a half-configured asset degrades, it does not throw into gameplay.
            var easing = new A2BCurveEasing { Curve = null };
            Assert.AreEqual(0.42f, easing.Evaluate(0.42f), Tolerance);
        }
    }
}
