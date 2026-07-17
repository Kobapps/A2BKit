using System.Collections.Generic;
using A2BKit.Core;
using NUnit.Framework;
using UnityEngine;

namespace A2BKit.Tests.EditMode
{
    /// <summary>
    /// AD-10 / FR-26 — emission variation is COMPUTED from (seed, index), never stored.
    /// The consequence that matters to users is determinism: same seed, same burst, every time
    /// (NFR-4). The consequence that matters to AD-16 is that scatter leaves here unitless.
    /// </summary>
    [TestFixture]
    internal sealed class A2BEmissionTests
    {
        private const uint SeedA = 0xC0FFEEu;
        private const uint SeedB = 0xBADF00Du;

        private static A2BBurstEmission Jittered() => new A2BBurstEmission
        {
            MinCount = 16,
            MaxCount = 16,
            ReleaseMode = A2BReleaseMode.FixedStagger,
            StaggerInterval = 0.02f,
            DelayJitter = 0.5f,
            ScatterRadius = 1f,
            ScatterAxisWeights = Vector3.one
        };

        // ---- determinism -----------------------------------------------------------------------

        [Test]
        public void ResolveDelay_SameSeed_IsIdenticalAcrossCalls()
        {
            A2BBurstEmission e = Jittered();
            for (int i = 0; i < 16; i++)
            {
                float first = e.ResolveDelay(SeedA, i, 16);
                float second = e.ResolveDelay(SeedA, i, 16);
                Assert.AreEqual(first, second, 0f, "Delay for item " + i + " is not a pure function of (seed, index).");
            }
        }

        [Test]
        public void ResolveScatter_SameSeed_IsIdenticalAcrossCalls()
        {
            A2BBurstEmission e = Jittered();
            for (int i = 0; i < 16; i++)
            {
                Vector3 first = e.ResolveScatter(SeedA, i, 16);
                Vector3 second = e.ResolveScatter(SeedA, i, 16);
                Assert.AreEqual(first, second, "Scatter for item " + i + " is not a pure function of (seed, index).");
            }
        }

        [Test]
        public void ResolveScatter_IsIndependentOfCallOrder()
        {
            // The struct RNG must be constructed per call. A shared RNG field would make the answer
            // depend on how many times the method has been called — the classic AD-2 statefulness bug.
            A2BBurstEmission e = Jittered();
            Vector3 forwardItem3 = e.ResolveScatter(SeedA, 3, 16);

            for (int i = 15; i >= 0; i--) e.ResolveScatter(SeedA, i, 16);

            Assert.AreEqual(forwardItem3, e.ResolveScatter(SeedA, 3, 16),
                "Scatter depends on call order: the emission strategy is holding state (AD-2).");
        }

        [Test]
        public void ResolveDelay_DifferentSeeds_ProduceDifferentJitter()
        {
            A2BBurstEmission e = Jittered();
            bool anyDifferent = false;
            for (int i = 0; i < 16; i++)
                if (!Mathf.Approximately(e.ResolveDelay(SeedA, i, 16), e.ResolveDelay(SeedB, i, 16)))
                    anyDifferent = true;

            Assert.IsTrue(anyDifferent, "Two different seeds produced identical delays for all 16 items.");
        }

        [Test]
        public void ResolveScatter_DifferentSeeds_ProduceDifferentLayouts()
        {
            A2BBurstEmission e = Jittered();
            bool anyDifferent = false;
            for (int i = 0; i < 16; i++)
                if (Vector3.Distance(e.ResolveScatter(SeedA, i, 16), e.ResolveScatter(SeedB, i, 16)) > 1e-5f)
                    anyDifferent = true;

            Assert.IsTrue(anyDifferent, "Two different seeds produced an identical scatter layout.");
        }

        // ---- AD-16: unitless scatter -----------------------------------------------------------

        [Test]
        public void ResolveScatter_IsUnitless_WithinTheUnitCube()
        {
            // AD-16: emission cannot know whether "radius 50" is pixels or metres, so it emits a
            // normalized offset in [-1,1]^3 and the presenter assigns the units.
            A2BBurstEmission e = Jittered();
            for (uint seed = 1u; seed <= 64u; seed++)
            {
                for (int i = 0; i < 16; i++)
                {
                    Vector3 s = e.ResolveScatter(seed * 2654435761u, i, 16);
                    Assert.LessOrEqual(Mathf.Abs(s.x), 1f + 1e-5f, "Scatter X left [-1,1].");
                    Assert.LessOrEqual(Mathf.Abs(s.y), 1f + 1e-5f, "Scatter Y left [-1,1].");
                    Assert.LessOrEqual(Mathf.Abs(s.z), 1f + 1e-5f, "Scatter Z left [-1,1].");
                }
            }
        }

        [Test]
        public void ResolveScatter_IsUnitless_WithinTheUnitSphere()
        {
            // Stronger than the cube: the offset is a direction on the unit sphere scaled by r <= 1.
            A2BBurstEmission e = Jittered();
            for (uint seed = 1u; seed <= 64u; seed++)
                for (int i = 0; i < 16; i++)
                    Assert.LessOrEqual(e.ResolveScatter(seed * 40503u, i, 16).magnitude, 1f + 1e-4f,
                        "Scatter left the unit sphere.");
        }

        [Test]
        public void ResolveScatter_IsIndependentOfScatterRadius()
        {
            // AD-16 again: the radius is the presenter's business. Changing it here must not change
            // the normalized layout, otherwise "radius" would be baked into the unitless value twice.
            A2BBurstEmission a = Jittered();
            A2BBurstEmission b = Jittered();
            b.ScatterRadius = 500f;

            for (int i = 0; i < 16; i++)
                Assert.AreEqual(a.ResolveScatter(SeedA, i, 16), b.ResolveScatter(SeedA, i, 16),
                    "Scatter radius leaked into the unitless offset (AD-16).");
        }

        [Test]
        public void ScatterRadius_IsExposedOnThePort_NotOnlyOnTheConcreteType()
        {
            // FR-10: the scheduler reads the radius through IA2BEmission. If it were only on
            // A2BBurstEmission, every custom emission would silently get a radius of zero.
            IA2BEmission port = new A2BBurstEmission { ScatterRadius = 2.5f };
            Assert.AreEqual(2.5f, port.ScatterRadius, 1e-6f);
        }

        [Test]
        public void ScatterRadius_NegativeValue_ClampsToZero()
        {
            var e = new A2BBurstEmission { ScatterRadius = -7f };
            Assert.AreEqual(0f, e.ScatterRadius, 1e-6f, "A negative scatter radius was stored verbatim.");
            Assert.AreEqual(Vector3.zero, e.ResolveScatter(SeedA, 0, 4));
        }

        [Test]
        public void ResolveScatter_ZeroRadius_ProducesNoScatter()
        {
            A2BBurstEmission e = Jittered();
            e.ScatterRadius = 0f;
            for (int i = 0; i < 16; i++)
                Assert.AreEqual(Vector3.zero, e.ResolveScatter(SeedA, i, 16));
        }

        [TestCase(0f, 1f, 1f)]
        [TestCase(1f, 0f, 1f)]
        [TestCase(1f, 1f, 0f)] // the 2D case: scatter in the XY plane
        [TestCase(1f, 0f, 0f)]
        public void ScatterAxisWeights_ZeroAnAxis_FlattensIt(float wx, float wy, float wz)
        {
            A2BBurstEmission e = Jittered();
            e.ScatterAxisWeights = new Vector3(wx, wy, wz);

            bool sawNonZeroOnALiveAxis = false;
            for (int i = 0; i < 16; i++)
            {
                Vector3 s = e.ResolveScatter(SeedA, i, 16);
                if (wx == 0f) Assert.AreEqual(0f, s.x, 0f, "X was zeroed but scatter still has an X component.");
                if (wy == 0f) Assert.AreEqual(0f, s.y, 0f, "Y was zeroed but scatter still has a Y component.");
                if (wz == 0f) Assert.AreEqual(0f, s.z, 0f, "Z was zeroed but scatter still has a Z component.");
                if (s.sqrMagnitude > 1e-8f) sawNonZeroOnALiveAxis = true;
            }

            Assert.IsTrue(sawNonZeroOnALiveAxis, "Zeroing one axis flattened every axis.");
        }

        // ---- count -----------------------------------------------------------------------------

        [Test]
        public void ResolveItemCount_FixedRange_IsExact()
        {
            var e = new A2BBurstEmission { MinCount = 7, MaxCount = 7 };
            for (uint seed = 1u; seed <= 32u; seed++)
                Assert.AreEqual(7, e.ResolveItemCount(seed));
        }

        [Test]
        public void ResolveItemCount_VariableRange_StaysInRangeAndIsDeterministic()
        {
            var e = new A2BBurstEmission { MinCount = 5, MaxCount = 9 };
            var seen = new HashSet<int>();
            for (uint seed = 1u; seed <= 256u; seed++)
            {
                int count = e.ResolveItemCount(seed * 2246822519u);
                Assert.GreaterOrEqual(count, 5);
                Assert.LessOrEqual(count, 9);
                Assert.AreEqual(count, e.ResolveItemCount(seed * 2246822519u), "Item count is not deterministic.");
                seen.Add(count);
            }
            Assert.Greater(seen.Count, 1, "A variable count range never varied.");
        }

        [Test]
        public void ResolveItemCount_InvertedRange_DegradesRatherThanThrowing()
        {
            // AD-8: a designer typing Max < Min gets a sane burst, not an exception.
            var e = new A2BBurstEmission { MinCount = 10, MaxCount = 2 };
            Assert.DoesNotThrow(() => e.ResolveItemCount(SeedA));
            Assert.AreEqual(10, e.ResolveItemCount(SeedA));
        }

        // ---- release modes ---------------------------------------------------------------------

        [Test]
        public void AllAtOnce_GivesEveryItemZeroDelay()
        {
            var e = new A2BBurstEmission { ReleaseMode = A2BReleaseMode.AllAtOnce, DelayJitter = 0f };
            for (int i = 0; i < 16; i++)
                Assert.AreEqual(0f, e.ResolveDelay(SeedA, i, 16), 0f);
        }

        [Test]
        public void FixedStagger_ScalesDelayWithIndex()
        {
            var e = new A2BBurstEmission
            {
                ReleaseMode = A2BReleaseMode.FixedStagger, StaggerInterval = 0.05f, DelayJitter = 0f
            };
            for (int i = 0; i < 16; i++)
                Assert.AreEqual(i * 0.05f, e.ResolveDelay(SeedA, i, 16), 1e-5f);
        }

        [Test]
        public void SpreadOverDuration_SpansExactlyTheDuration_RegardlessOfCount()
        {
            var e = new A2BBurstEmission
            {
                ReleaseMode = A2BReleaseMode.SpreadOverDuration, SpreadDuration = 0.8f, DelayJitter = 0f
            };

            foreach (int count in new[] { 2, 16, 200 })
            {
                Assert.AreEqual(0f, e.ResolveDelay(SeedA, 0, count), 1e-5f, "First item was not released at t=0.");
                Assert.AreEqual(0.8f, e.ResolveDelay(SeedA, count - 1, count), 1e-5f,
                    "Last item of a " + count + "-item burst did not land on SpreadDuration.");
            }
        }

        [Test]
        public void SpreadOverDuration_SingleItem_HasNoDelay()
        {
            var e = new A2BBurstEmission { ReleaseMode = A2BReleaseMode.SpreadOverDuration, SpreadDuration = 0.8f, DelayJitter = 0f };
            Assert.AreEqual(0f, e.ResolveDelay(SeedA, 0, 1), 0f, "A one-item spread divided by zero.");
        }

        [Test]
        public void ResolveDelay_IsNeverNegative()
        {
            A2BBurstEmission e = Jittered();
            for (uint seed = 1u; seed <= 64u; seed++)
                for (int i = 0; i < 16; i++)
                    Assert.GreaterOrEqual(e.ResolveDelay(seed * 22695477u, i, 16), 0f);
        }

        [Test]
        public void DelayJitter_AndScatter_AreIndependentStreams()
        {
            // The implementation offsets the seed so the two do not share a stream. If they did,
            // tuning one knob in the inspector would silently re-roll the other.
            A2BBurstEmission baseline = Jittered();

            A2BBurstEmission moreDelayJitter = Jittered();
            moreDelayJitter.DelayJitter = 3f;

            A2BBurstEmission flatterScatter = Jittered();
            flatterScatter.ScatterAxisWeights = new Vector3(1f, 1f, 0f);

            for (int i = 0; i < 16; i++)
            {
                Assert.AreEqual(baseline.ResolveScatter(SeedA, i, 16), moreDelayJitter.ResolveScatter(SeedA, i, 16),
                    "Changing DelayJitter re-rolled the scatter layout.");
                Assert.AreEqual(baseline.ResolveDelay(SeedA, i, 16), flatterScatter.ResolveDelay(SeedA, i, 16), 0f,
                    "Changing ScatterAxisWeights re-rolled the release delays.");
            }
        }
    }
}
