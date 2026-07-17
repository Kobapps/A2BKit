using A2BKit.Core;
using NUnit.Framework;
using UnityEngine;

namespace A2BKit.Tests.EditMode
{
    /// <summary>
    /// The presenter must be able to tell "this item landed" from "this item was recalled".
    ///
    /// It is the whole basis of on-hit feedback: an arrival should spark, punch and play a sound; a
    /// cancelled item should vanish silently. Before <see cref="A2BReleaseReason"/> existed, Release
    /// carried no reason at all — so cancelling a 50-coin burst mid-flight would have fired 50
    /// impact effects as it tore down, which is the opposite of what cancelling means.
    /// </summary>
    [TestFixture]
    internal sealed class A2BReleaseReasonTests
    {
        private A2BScheduler _scheduler;
        private A2BManualTimeSource _time;
        private RecordingPresenter _presenter;

        private const uint Seed = 0x515Eu;

        [SetUp]
        public void SetUp()
        {
            _scheduler = new A2BScheduler();
            _time = new A2BManualTimeSource();
            _presenter = new RecordingPresenter();
        }

        private A2BEffectDefinition Definition(int count, float duration = 0.4f) =>
            A2BEffectBuilder.Create()
                .Duration(duration).DurationJitter(0f)
                .Linear().Ease(A2BEaseKind.Linear)
                .Count(count).AllAtOnce().Scatter(0f)
                .Build();

        private A2BEffectHandle Play(A2BEffectDefinition def)
        {
            var args = new A2BPlayArgs(
                new A2BStaticEndpoint(Vector3.zero),
                new A2BStaticEndpoint(new Vector3(0f, 5f, 0f)),
                _presenter, seed: Seed);
            A2BEffectHandle handle = _scheduler.Play(def, in args);
            _scheduler.SetTimeSource(handle, _time);
            return handle;
        }

        private void Step(float dt, int frames)
        {
            for (int i = 0; i < frames; i++) { _time.Advance(dt); _scheduler.Tick(); }
        }

        [Test]
        public void EveryItem_ThatLands_IsReleasedAsArrived()
        {
            Play(Definition(count: 6));
            Step(0.1f, 8);

            Assert.AreEqual(6, _presenter.ArrivedReleaseCount, "Every landed item must release as Arrived.");
            Assert.AreEqual(0, _presenter.CancelledReleaseCount, "A clean completion must cancel nothing.");
            Assert.AreEqual(0, _presenter.DoubleReleaseCount);
        }

        [Test]
        public void Items_CancelledMidFlight_AreNotReportedAsArrivals()
        {
            A2BEffectHandle handle = Play(Definition(count: 10, duration: 5f));
            Step(0.1f, 2);   // in flight, none landed

            Assert.AreEqual(0, _presenter.ArrivedReleaseCount, "Nothing has reached the destination yet.");

            handle.Cancel();
            _scheduler.Tick();

            Assert.AreEqual(10, _presenter.CancelledReleaseCount,
                "Cancelling mid-flight must release every in-flight item as Cancelled — firing arrival " +
                "feedback here would spark 10 impacts for coins that never landed.");
            Assert.AreEqual(0, _presenter.ArrivedReleaseCount);
            Assert.AreEqual(0, _presenter.LiveIds.Count, "No item may be left holding a pooled visual (AD-9).");
        }

        [Test]
        public void A_LostEndpoint_ReleasesAsCancelled_NotArrived()
        {
            var flaky = new FlakyEndpoint { Valid = true };
            var args = new A2BPlayArgs(
                new A2BStaticEndpoint(Vector3.zero), flaky, _presenter, seed: Seed);
            A2BEffectHandle handle = _scheduler.Play(Definition(count: 4, duration: 5f), in args);
            _scheduler.SetTimeSource(handle, _time);

            Step(0.1f, 2);
            flaky.Valid = false;      // the wallet was destroyed mid-flight
            Step(0.1f, 1);

            Assert.AreEqual(4, _presenter.CancelledReleaseCount,
                "A destroyed target means the coins never arrived; they must not spark (FR-13).");
            Assert.AreEqual(0, _presenter.ArrivedReleaseCount);
        }

        [Test]
        public void PartialFlight_ReportsArrivalsAndCancellationsSeparately()
        {
            // Staggered, so early items land while later ones are still travelling when we cancel.
            A2BEffectDefinition def = A2BEffectBuilder.Create()
                .Duration(0.2f).DurationJitter(0f)
                .Linear().Ease(A2BEaseKind.Linear)
                .Count(6).Stagger(0.2f).Scatter(0f)
                .Build();

            A2BEffectHandle handle = Play(def);
            Step(0.1f, 7);   // some have landed by now

            int landed = _presenter.ArrivedReleaseCount;
            Assert.Greater(landed, 0, "Test is meaningless unless something landed first.");
            Assert.Less(landed, 6, "Test is meaningless unless something is still in flight.");

            handle.Cancel();
            _scheduler.Tick();

            Assert.AreEqual(landed, _presenter.ArrivedReleaseCount,
                "Cancelling must not retroactively turn landed items into cancellations.");
            Assert.Greater(_presenter.CancelledReleaseCount, 0, "The in-flight remainder must report Cancelled.");
            Assert.AreEqual(0, _presenter.LiveIds.Count, "Every item is back in the pool either way (AD-9).");
        }

        private sealed class FlakyEndpoint : IA2BEndpointProvider
        {
            public bool Valid = true;
            public A2BEndpointSample Resolve()
                => Valid ? A2BEndpointSample.At(new Vector3(0f, 5f, 0f)) : A2BEndpointSample.Invalid;
        }
    }
}
