using A2BKit.Core;
using NUnit.Framework;
using UnityEngine;

namespace A2BKit.Tests.EditMode
{
    /// <summary>
    /// The burst-then-gather shape, asserted as a SHAPE.
    ///
    /// The endpoint invariant (AD-13) is covered generically for every shipped path by
    /// A2BArchitectureTests, and it is necessary but nowhere near sufficient here: a plain straight
    /// line satisfies it too. What makes this path worth existing is that items first travel *away*
    /// from the destination, then pause, then come back — and every one of those three properties can
    /// be lost by a tuning change while the path still lands correctly and every other test stays
    /// green. So they are pinned here.
    /// </summary>
    [TestFixture]
    internal sealed class A2BBurstGatherPathTests
    {
        private static readonly Vector3 Origin = new Vector3(-4f, -3f, 0f);
        private static readonly Vector3 Destination = new Vector3(6f, 4f, 0f);

        private static A2BPathContext Ctx(uint seed = 0xC0FFEEu, int index = 0, int count = 12)
            => new A2BPathContext(Origin, Destination, index, count, A2BRandom.DeriveSeed(seed, index));

        private static A2BBurstGatherPath Path() => new A2BBurstGatherPath
        {
            BurstRadius = 3f,
            BurstFraction = 0.35f,
            HoldFraction = 0.15f,
            RadiusJitter = 0f,          // deterministic geometry for assertions
            BurstBias = Vector3.zero,
            BurstAxisWeights = new Vector3(1f, 1f, 0f),
        };

        [Test]
        public void A_meaningful_share_of_items_first_travel_AWAY_from_the_destination()
        {
            // The property that separates a burst from an arc: an arc closes on the target from frame
            // one, for every item. A burst sprays, so a good share of items must move the WRONG way first.
            //
            // Asserted across the spray, not per item — the first version of this test checked item 0
            // alone and failed, correctly: with a uniform spray, whether any ONE item happens to burst
            // toward the wallet is a coin flip. Roughly half legitimately do, and that is the point;
            // the population is the shape, not the individual.
            A2BBurstGatherPath path = Path();
            float startDistance = Vector3.Distance(Origin, Destination);

            const int items = 40;
            int away = 0;
            for (int i = 0; i < items; i++)
            {
                A2BPathContext ctx = Ctx(index: i);
                if (Vector3.Distance(path.Evaluate(in ctx, 0.35f), Destination) > startDistance) away++;
            }

            Assert.Greater(away, items / 5,
                $"Only {away}/{items} items ended the burst farther from the destination than they " +
                "started. A burst sprays outward; if nearly every item is already closing on the " +
                "target, this is an arc wearing a burst's name.");
            Assert.Less(away, items * 4 / 5,
                $"{away}/{items} items moved away — that is a spray pointed away from the target, not " +
                "a burst around the origin. Check BurstBias.");
        }

        [Test]
        public void Every_item_leaves_the_origin_regardless_of_direction()
        {
            // The per-item half of the same idea, and the part that IS deterministic: whichever way an
            // item sprays, it must actually travel outward from where it spawned.
            A2BBurstGatherPath path = Path();

            for (int i = 0; i < 24; i++)
            {
                A2BPathContext ctx = Ctx(index: i);
                float atPeak = Vector3.Distance(path.Evaluate(in ctx, 0.35f), Origin);
                Assert.AreEqual(3f, atPeak, 0.01f,
                    $"Item {i} did not reach the burst radius — it never really burst.");
            }
        }

        [Test]
        public void The_item_reaches_roughly_the_configured_radius()
        {
            A2BBurstGatherPath path = Path();
            A2BPathContext ctx = Ctx();

            float peakDistance = Vector3.Distance(path.Evaluate(in ctx, 0.35f), Origin);

            Assert.AreEqual(3f, peakDistance, 0.01f,
                "With jitter off, the burst apex should sit exactly BurstRadius from the origin.");
        }

        [Test]
        public void The_hold_is_a_real_pause_not_a_slow_move()
        {
            // The pause is what makes it read as two beats. If HoldFraction quietly became a slow lerp,
            // the effect would still work and still land — it would just stop looking like a burst.
            A2BBurstGatherPath path = Path();
            A2BPathContext ctx = Ctx();

            Vector3 a = path.Evaluate(in ctx, 0.36f);
            Vector3 b = path.Evaluate(in ctx, 0.44f);   // both inside burst(0.35) + hold(0.15) = 0.50

            Assert.AreEqual(0f, Vector3.Distance(a, b), 1e-4f, "The hold must be motionless.");
        }

        [Test]
        public void With_no_hold_the_item_turns_immediately()
        {
            A2BBurstGatherPath path = Path();
            path.HoldFraction = 0f;
            A2BPathContext ctx = Ctx();

            Vector3 a = path.Evaluate(in ctx, 0.36f);
            Vector3 b = path.Evaluate(in ctx, 0.44f);

            Assert.Greater(Vector3.Distance(a, b), 1e-3f,
                "HoldFraction = 0 must mean no dead time — the gather starts the moment the burst ends.");
        }

        [Test]
        public void The_gather_closes_monotonically_on_the_destination()
        {
            // Beat two: once turned, an item should not wander back out. A player reads any second
            // reversal as the effect glitching.
            A2BBurstGatherPath path = Path();
            A2BPathContext ctx = Ctx();

            float previous = float.MaxValue;
            for (float t = 0.5f; t <= 1.0001f; t += 0.05f)
            {
                float d = Vector3.Distance(path.Evaluate(in ctx, t), Destination);
                Assert.LessOrEqual(d, previous + 1e-3f,
                    $"Distance to the destination grew during the gather at t={t:0.00} — the item turned back.");
                previous = d;
            }

            Assert.AreEqual(0f, previous, 1e-3f, "The gather must finish exactly on the destination (AD-13).");
        }

        [Test]
        public void Different_items_burst_in_different_directions()
        {
            // A spray, not a ring: every item sharing one direction would look like a single clump
            // being thrown. Direction comes from the per-item seed (AD-10).
            A2BBurstGatherPath path = Path();

            Vector3 first = path.Evaluate(Ctx(index: 0), 0.35f);
            Vector3 second = path.Evaluate(Ctx(index: 1), 0.35f);
            Vector3 third = path.Evaluate(Ctx(index: 2), 0.35f);

            Assert.Greater(Vector3.Distance(first, second), 0.1f, "Items 0 and 1 burst to the same point.");
            Assert.Greater(Vector3.Distance(second, third), 0.1f, "Items 1 and 2 burst to the same point.");
        }

        [Test]
        public void The_same_seed_bursts_the_same_way_every_time()
        {
            A2BBurstGatherPath path = Path();
            A2BPathContext ctx = Ctx(index: 3);

            for (float t = 0f; t <= 1f; t += 0.1f)
            {
                Vector3 a = path.Evaluate(in ctx, t);
                Vector3 b = path.Evaluate(in ctx, t);
                Assert.AreEqual(0f, Vector3.Distance(a, b), 1e-6f, "Evaluation must be pure (AD-13/NFR-4).");
            }
        }

        [Test]
        public void Planar_weights_keep_a_canvas_burst_in_plane()
        {
            // Canvas and World2D live on XY. A burst that sprayed in Z would push items off the canvas
            // plane, which the adapter then flattens — reading as a burst that mysteriously loses range.
            A2BBurstGatherPath path = Path();
            path.BurstAxisWeights = new Vector3(1f, 1f, 0f);

            for (int i = 0; i < 24; i++)
            {
                A2BPathContext ctx = Ctx(index: i);
                Assert.AreEqual(0f, path.Evaluate(in ctx, 0.35f).z, 1e-4f,
                    "A planar burst must not leave the XY plane.");
            }
        }

        [Test]
        public void A_degenerate_configuration_does_not_produce_NaN()
        {
            // Zeroed weights and no bias make the spray direction a zero vector; normalizing that
            // yields NaN and teleports the item somewhere undefined. Falls back to "up" instead (AD-8).
            var path = new A2BBurstGatherPath
            {
                BurstAxisWeights = Vector3.zero,
                BurstBias = Vector3.zero,
                RadiusJitter = 0f,
            };
            A2BPathContext ctx = Ctx();

            for (float t = 0f; t <= 1f; t += 0.1f)
            {
                Vector3 p = path.Evaluate(in ctx, t);
                Assert.IsFalse(float.IsNaN(p.x) || float.IsNaN(p.y) || float.IsNaN(p.z),
                    $"NaN at t={t:0.0} from a degenerate burst configuration.");
            }
        }

        [Test]
        public void Zero_radius_degrades_to_a_straight_line_rather_than_breaking()
        {
            var path = new A2BBurstGatherPath { BurstRadius = 0f, RadiusJitter = 0f, HoldFraction = 0f };
            A2BPathContext ctx = Ctx();

            // No burst distance means the apex is the origin: the item simply waits, then flies.
            Assert.AreEqual(0f, Vector3.Distance(path.Evaluate(in ctx, 0.35f), Origin), 1e-3f);
            Assert.AreEqual(0f, Vector3.Distance(path.Evaluate(in ctx, 1f), Destination), 1e-3f);
        }

        [Test]
        public void The_builder_shortcut_produces_the_same_path()
        {
            // BurstThenGather() is the discoverable surface; if it drifted from the type it configures,
            // every caller using the fluent API would get something else.
            A2BEffectDefinition def = A2BEffectBuilder.Create()
                .BurstThenGather(radius: 5f, burstFraction: 0.4f, hold: 0.2f, planar: true)
                .Build();

            var path = def.Path as A2BBurstGatherPath;
            Assert.IsNotNull(path, "BurstThenGather did not install an A2BBurstGatherPath.");
            Assert.AreEqual(5f, path.BurstRadius, 1e-4f);
            Assert.AreEqual(0.4f, path.BurstFraction, 1e-4f);
            Assert.AreEqual(0.2f, path.HoldFraction, 1e-4f);
            Assert.AreEqual(0f, path.BurstAxisWeights.z, 1e-4f, "planar: true must zero the Z spray.");
        }

        [Test]
        public void Non_planar_bursts_spray_in_three_dimensions()
        {
            A2BEffectDefinition def = A2BEffectBuilder.Create()
                .BurstThenGather(radius: 5f, planar: false)
                .Build();

            var path = def.Path as A2BBurstGatherPath;
            Assert.IsNotNull(path);
            Assert.AreNotEqual(0f, path.BurstAxisWeights.z, "planar: false must allow a Z spray for World3D.");
        }
    }
}
