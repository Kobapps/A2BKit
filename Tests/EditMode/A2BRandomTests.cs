using System.Collections.Generic;
using A2BKit.Core;
using NUnit.Framework;
using UnityEngine;

namespace A2BKit.Tests.EditMode
{
    /// <summary>
    /// A2BRandom is the substrate NFR-4 rests on: UnityEngine.Random is banned package-wide because
    /// it is global mutable state, so every bit of per-item variation comes from here. Two properties
    /// matter — it is a pure function of its seed, and it can never fall into xorshift's absorbing
    /// zero state (where every subsequent draw would be zero and every item would look identical).
    /// </summary>
    [TestFixture]
    internal sealed class A2BRandomTests
    {
        [Test]
        public void SameSeed_ProducesTheSameSequence()
        {
            var a = new A2BRandom(1234u);
            var b = new A2BRandom(1234u);
            for (int i = 0; i < 1000; i++)
                Assert.AreEqual(a.NextUInt(), b.NextUInt(), "Sequence diverged at draw " + i + ".");
        }

        [Test]
        public void DifferentSeeds_ProduceDifferentSequences()
        {
            var a = new A2BRandom(1234u);
            var b = new A2BRandom(5678u);
            bool diverged = false;
            for (int i = 0; i < 32; i++)
                if (a.NextUInt() != b.NextUInt())
                    diverged = true;

            Assert.IsTrue(diverged, "Two different seeds produced 32 identical draws.");
        }

        [Test]
        public void IsAStruct_SoCopiesDoNotShareState()
        {
            // A2BRandom is a struct on purpose (AD-10): copying it must fork the stream, not alias it.
            var a = new A2BRandom(99u);
            a.NextUInt();
            A2BRandom copy = a;

            Assert.AreEqual(a.NextUInt(), copy.NextUInt(), "A copied RNG did not continue the same stream.");
            a.NextUInt();
            Assert.AreNotEqual(a.NextUInt(), copy.NextUInt(),
                "Advancing one copy advanced the other: A2BRandom is behaving like a reference type.");
        }

        [Test]
        public void ZeroSeed_DoesNotCollapseToTheAbsorbingState()
        {
            // Zero is absorbing for xorshift: without the guard every draw would be 0 forever and
            // every item in every burst would land on top of the next.
            var rng = new A2BRandom(0u);
            for (int i = 0; i < 1000; i++)
                Assert.AreNotEqual(0u, rng.NextUInt(), "Zero-seeded RNG returned 0 at draw " + i + ".");
        }

        [Test]
        public void ZeroSeed_IsRemappedToTheDocumentedFallback()
        {
            var fromZero = new A2BRandom(0u);
            var fromFallback = new A2BRandom(0x9E3779B9u);
            for (int i = 0; i < 64; i++)
                Assert.AreEqual(fromFallback.NextUInt(), fromZero.NextUInt());
        }

        [Test]
        public void NextUInt_NeverReturnsZero_ForAnyOfManySeeds()
        {
            for (uint seed = 0u; seed < 512u; seed++)
            {
                var rng = new A2BRandom(seed);
                for (int i = 0; i < 64; i++)
                    Assert.AreNotEqual(0u, rng.NextUInt(), "Seed " + seed + " reached the zero state at draw " + i + ".");
            }
        }

        [Test]
        public void NextFloat_StaysInUnitRange()
        {
            var rng = new A2BRandom(0xDEADBEEFu);
            for (int i = 0; i < 20000; i++)
            {
                float v = rng.NextFloat();
                Assert.GreaterOrEqual(v, 0f);
                Assert.Less(v, 1f, "NextFloat returned 1.0; the range is documented as [0,1).");
            }
        }

        [Test]
        public void NextFloat_Ranged_StaysInRange()
        {
            var rng = new A2BRandom(4242u);
            for (int i = 0; i < 20000; i++)
            {
                float v = rng.NextFloat(-3f, 7f);
                Assert.GreaterOrEqual(v, -3f);
                Assert.Less(v, 7f);
            }
        }

        [Test]
        public void NextInt_StaysInRange_AndIsExclusiveOfMax()
        {
            var rng = new A2BRandom(31337u);
            var seen = new HashSet<int>();
            for (int i = 0; i < 20000; i++)
            {
                int v = rng.NextInt(5, 9);
                Assert.GreaterOrEqual(v, 5);
                Assert.Less(v, 9, "NextInt is documented as [min,max).");
                seen.Add(v);
            }
            Assert.AreEqual(4, seen.Count, "NextInt(5,9) did not cover every value in [5,9).");
        }

        [Test]
        public void NextInt_DegenerateRange_ReturnsMin_RatherThanDividingByZero()
        {
            var rng = new A2BRandom(1u);
            Assert.AreEqual(5, rng.NextInt(5, 5));
            Assert.AreEqual(5, rng.NextInt(5, 3));
        }

        [Test]
        public void NextUnitSphere_LandsOnTheUnitSphere()
        {
            var rng = new A2BRandom(0xABCDEFu);
            for (int i = 0; i < 10000; i++)
            {
                Vector3 v = rng.NextUnitSphere();
                Assert.AreEqual(1f, v.magnitude, 1e-3f, "NextUnitSphere returned a non-unit vector.");
            }
        }

        [Test]
        public void NextUnitSphere_CoversAllOctants()
        {
            var rng = new A2BRandom(555u);
            var octants = new HashSet<int>();
            for (int i = 0; i < 5000; i++)
            {
                Vector3 v = rng.NextUnitSphere();
                octants.Add((v.x >= 0f ? 1 : 0) | (v.y >= 0f ? 2 : 0) | (v.z >= 0f ? 4 : 0));
            }
            Assert.AreEqual(8, octants.Count, "Scatter directions are biased: not all 8 octants were reached.");
        }

        // ---- DeriveSeed ------------------------------------------------------------------------

        [Test]
        public void DeriveSeed_IsStable()
        {
            for (uint seed = 0u; seed < 64u; seed++)
                for (int i = 0; i < 64; i++)
                    Assert.AreEqual(A2BRandom.DeriveSeed(seed, i), A2BRandom.DeriveSeed(seed, i),
                        "DeriveSeed is not a pure function of (seed, index).");
        }

        [Test]
        public void DeriveSeed_NeverReturnsZero()
        {
            for (uint seed = 0u; seed < 256u; seed++)
                for (int i = -8; i < 256; i++)
                    Assert.AreNotEqual(0u, A2BRandom.DeriveSeed(seed, i),
                        "DeriveSeed(" + seed + ", " + i + ") returned the absorbing zero state.");
        }

        [Test]
        public void DeriveSeed_SpreadsAdjacentIndices()
        {
            // AD-10 hands every item a seed derived from (effectSeed, itemIndex). If adjacent indices
            // collided, neighbouring items in a burst would share scatter and delay exactly.
            foreach (uint seed in new[] { 0u, 1u, 0xC0FFEEu, uint.MaxValue })
            {
                var derived = new HashSet<uint>();
                for (int i = 0; i < 256; i++) derived.Add(A2BRandom.DeriveSeed(seed, i));

                Assert.GreaterOrEqual(derived.Count, 255,
                    "DeriveSeed collided for effect seed " + seed + ": only " + derived.Count + "/256 distinct.");
            }
        }

        [Test]
        public void DeriveSeed_SpreadsAdjacentEffectSeeds()
        {
            var derived = new HashSet<uint>();
            for (uint seed = 0u; seed < 256u; seed++) derived.Add(A2BRandom.DeriveSeed(seed, 0));

            Assert.GreaterOrEqual(derived.Count, 255,
                "DeriveSeed collided across adjacent effect seeds: only " + derived.Count + "/256 distinct.");
        }

        [Test]
        public void DeriveSeed_ProducesUsableStreams()
        {
            // The point of DeriveSeed is that the derived seed feeds an RNG. Two items must not
            // produce the same first draw.
            var firstDraws = new HashSet<uint>();
            for (int i = 0; i < 200; i++)
            {
                var rng = new A2BRandom(A2BRandom.DeriveSeed(0xC0FFEEu, i));
                firstDraws.Add(rng.NextUInt());
            }
            Assert.GreaterOrEqual(firstDraws.Count, 199, "Per-item RNG streams collided.");
        }
    }
}
