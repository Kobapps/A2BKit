using A2BKit.Core;
using NUnit.Framework;
using UnityEngine;

namespace A2BKit.Tests.EditMode
{
    /// <summary>
    /// FR-14 / AD-9 — the event contract, driven through the AD-12 seam (a manual time source and
    /// hand-pumped ticks), so nothing here depends on a frame, a scene, or Time.deltaTime.
    ///
    /// The rules under test are the ones the architecture claims are true "by construction":
    /// Started precedes every spawn; FirstItemArrived fires exactly once and always before Completed;
    /// exactly one of Completed/Cancelled fires; and no terminal path leaks a pooled item.
    /// </summary>
    [TestFixture]
    internal sealed class A2BSchedulerEventTests
    {
        private A2BScheduler _scheduler;
        private A2BManualTimeSource _time;
        private TickClock _clock;
        private RecordingPresenter _presenter;
        private RecordingListener _listener;
        private A2BStaticEndpoint _origin;
        private A2BStaticEndpoint _destination;

        [SetUp]
        public void SetUp()
        {
            _scheduler = new A2BScheduler();
            _time = new A2BManualTimeSource();
            _clock = new TickClock();
            _presenter = new RecordingPresenter();
            _listener = new RecordingListener { Clock = _clock };
            _origin = new A2BStaticEndpoint(new Vector3(0f, 0f, 0f));
            _destination = new A2BStaticEndpoint(new Vector3(10f, 4f, -2f));
        }

        private A2BEffectHandle Play(A2BEffectDefinition def, uint seed = 0xA2Bu)
        {
            var args = new A2BPlayArgs(_origin, _destination, _presenter, seed: seed);
            A2BEffectHandle handle = _scheduler.Play(def, in args);
            _scheduler.SetTimeSource(handle, _time);
            handle.AddListener(_listener);
            return handle;
        }

        // ---- ordering ---------------------------------------------------------------------------

        [Test]
        public void Started_FiresBeforeAnyItemSpawned()
        {
            Play(A2BTestHarness.Deterministic(6));
            A2BTestHarness.RunToCompletion(_scheduler, _time, _clock);

            Assert.AreEqual(1, _listener.StartedCount, "Started did not fire exactly once.");
            Assert.AreEqual(RecordingListener.Started, _listener.Events[0],
                "Started was not the first event of the effect.");
            Assert.Less(_listener.Events.IndexOf(RecordingListener.Started),
                _listener.Events.IndexOf(RecordingListener.ItemSpawned),
                "An item spawned before Started fired.");
        }

        [Test]
        public void Started_IsAnEffectLevelFact_NotAnItemLevelOne()
        {
            // Started fires on the effect's first tick, whether or not the burst has released.
            // With a 2-second stagger only item 0 is due, so the first tick must read exactly
            // Started, ItemSpawned — and nothing else.
            A2BEffectDefinition def = A2BEffectBuilder.From(A2BTestHarness.Deterministic(4))
                .Stagger(2f)
                .Build();
            Play(def);

            A2BTestHarness.Step(_scheduler, _time, _clock);

            Assert.AreEqual(1, _listener.StartedCount);
            Assert.AreEqual(1, _listener.StartedTick, "Started did not fire on the effect's very first tick.");
            Assert.AreEqual(1, _listener.SpawnedCount, "The staggered items released early.");
            Assert.AreEqual(RecordingListener.Started, _listener.Events[0]);
            Assert.AreEqual(RecordingListener.ItemSpawned, _listener.Events[1]);
        }

        [Test]
        public void FirstItemArrived_FiresExactlyOnce_AndBeforeCompleted()
        {
            Play(A2BTestHarness.Deterministic(8));
            A2BTestHarness.RunToCompletion(_scheduler, _time, _clock);

            Assert.AreEqual(1, _listener.FirstArrivedCount,
                "FirstItemArrived fired " + _listener.FirstArrivedCount + " times; the contract says exactly once.");
            Assert.AreEqual(1, _listener.CompletedCount);
            Assert.Less(_listener.Events.IndexOf(RecordingListener.FirstItemArrived),
                _listener.Events.IndexOf(RecordingListener.Completed),
                "FirstItemArrived fired after Completed.");
        }

        [Test]
        public void FirstItemArrived_FiresOnce_EvenWhenItemsArriveOnDifferentTicks()
        {
            // A stagger means arrivals are spread across many ticks. FirstItemArrived must still be
            // a once-per-effect fact rather than a once-per-tick one.
            A2BEffectDefinition def = A2BEffectBuilder.From(A2BTestHarness.Deterministic(6))
                .Stagger(0.15f)
                .Build();
            Play(def);
            A2BTestHarness.RunToCompletion(_scheduler, _time, _clock);

            Assert.AreEqual(1, _listener.FirstArrivedCount);
            Assert.AreEqual(6, _listener.ArrivedCount);
            Assert.Greater(_listener.Events.LastIndexOf(RecordingListener.ItemArrived),
                _listener.Events.IndexOf(RecordingListener.FirstItemArrived),
                "Every arrival landed on the same tick; this test is not exercising a spread burst.");
        }

        [Test]
        public void FirstItemArrived_IsAccompaniedByAnItemArrivedForTheSameItem()
        {
            // FirstItemArrived is a marker on top of a real arrival, not a replacement for it.
            Play(A2BTestHarness.Deterministic(5));
            A2BTestHarness.RunToCompletion(_scheduler, _time, _clock);

            int firstIndex = _listener.Events.IndexOf(RecordingListener.FirstItemArrived);
            Assert.AreEqual(RecordingListener.ItemArrived, _listener.Events[firstIndex + 1],
                "FirstItemArrived was not immediately followed by the ItemArrived it marks.");
            Assert.AreEqual(_listener.ArrivedIndices[0], _listener.FirstArrivedIndex);
        }

        [Test]
        public void ItemArrived_FiresOncePerItem_OnCleanCompletion()
        {
            Play(A2BTestHarness.Deterministic(12));
            A2BTestHarness.RunToCompletion(_scheduler, _time, _clock);

            Assert.AreEqual(12, _listener.ArrivedCount, "ItemArrived count does not match the item count.");
            Assert.AreEqual(12, _listener.SpawnedCount, "ItemSpawned count does not match the item count.");
            CollectionAssert.AllItemsAreUnique(_listener.ArrivedIndices, "An item arrived more than once.");
        }

        [Test]
        public void ItemArrived_FiresInIndexAscendingOrderWithinATick()
        {
            // AD-13: "ItemArrived is raised in index-ascending order within the frame."
            Play(A2BTestHarness.Deterministic(10));
            A2BTestHarness.RunToCompletion(_scheduler, _time, _clock);

            for (int i = 1; i < _listener.ArrivedIndices.Count; i++)
                Assert.Greater(_listener.ArrivedIndices[i], _listener.ArrivedIndices[i - 1],
                    "Arrivals were not in index-ascending order.");
        }

        [Test]
        public void EveryItem_SpawnsBeforeItArrives()
        {
            Play(A2BTestHarness.Deterministic(6));
            A2BTestHarness.RunToCompletion(_scheduler, _time, _clock);

            Assert.Less(_listener.Events.LastIndexOf(RecordingListener.ItemSpawned),
                _listener.Events.IndexOf(RecordingListener.Completed));
            Assert.AreEqual(0, _presenter.UnknownApplyCount, "Apply was called with an id the presenter never issued.");
        }

        // ---- exactly one terminal event ---------------------------------------------------------

        [Test]
        public void CleanCompletion_RaisesCompletedExactlyOnce_AndNeverCancelled()
        {
            Play(A2BTestHarness.Deterministic(4));
            A2BTestHarness.RunToCompletion(_scheduler, _time, _clock);

            Assert.AreEqual(1, _listener.CompletedCount);
            Assert.AreEqual(0, _listener.CancelledCount);
            Assert.AreEqual(1, _listener.TerminalCount, "Not exactly one terminal event (AD-9).");
        }

        [Test]
        public void CancelMidFlight_RaisesCancelledExactlyOnce_AndNeverCompleted()
        {
            A2BEffectHandle handle = Play(A2BTestHarness.Deterministic(4));
            A2BTestHarness.Step(_scheduler, _time, _clock);
            Assert.AreEqual(4, _presenter.LiveCount, "Items were not in flight; the cancel is not mid-flight.");

            handle.Cancel();

            Assert.AreEqual(1, _listener.CancelledCount);
            Assert.AreEqual(A2BCompletionReason.Cancelled, _listener.LastCancelReason);
            Assert.AreEqual(0, _listener.CompletedCount);
            Assert.AreEqual(1, _listener.TerminalCount, "Not exactly one terminal event (AD-9).");
        }

        [Test]
        public void DoubleCancel_StillRaisesExactlyOneTerminalEvent()
        {
            A2BEffectHandle handle = Play(A2BTestHarness.Deterministic(4));
            A2BTestHarness.Step(_scheduler, _time, _clock);

            handle.Cancel();
            handle.Cancel();
            handle.Cancel();
            A2BTestHarness.Step(_scheduler, _time, _clock);

            Assert.AreEqual(1, _listener.TerminalCount, "Cancelling twice raised two terminal events.");
        }

        [Test]
        public void CancelAfterCompletion_DoesNotRaiseASecondTerminalEvent()
        {
            A2BEffectHandle handle = Play(A2BTestHarness.Deterministic(4));
            A2BTestHarness.RunToCompletion(_scheduler, _time, _clock);
            Assert.AreEqual(1, _listener.CompletedCount);

            handle.Cancel();

            Assert.AreEqual(1, _listener.TerminalCount);
            Assert.AreEqual(0, _listener.CancelledCount);
        }

        [Test]
        public void CancelAll_CancelsEveryRunningEffect()
        {
            var secondListener = new RecordingListener { Clock = _clock };
            var secondPresenter = new RecordingPresenter();

            Play(A2BTestHarness.Deterministic(4));

            var args = new A2BPlayArgs(_origin, _destination, secondPresenter, seed: 7u);
            A2BEffectHandle second = _scheduler.Play(A2BTestHarness.Deterministic(4), in args);
            _scheduler.SetTimeSource(second, _time);
            second.AddListener(secondListener);

            A2BTestHarness.Step(_scheduler, _time, _clock);
            Assert.AreEqual(2, _scheduler.ActiveEffectCount);

            _scheduler.CancelAll();

            Assert.AreEqual(0, _scheduler.ActiveEffectCount);
            Assert.AreEqual(1, _listener.CancelledCount);
            Assert.AreEqual(1, secondListener.CancelledCount);
            Assert.AreEqual(0, _presenter.LiveCount);
            Assert.AreEqual(0, secondPresenter.LiveCount);
        }

        // ---- AD-9: no leaks on ANY terminal path -------------------------------------------------

        [Test]
        public void Completion_ReturnsEveryAcquiredItem()
        {
            Play(A2BTestHarness.Deterministic(16));
            A2BTestHarness.RunToCompletion(_scheduler, _time, _clock);

            Assert.AreEqual(16, _presenter.AcquireCount);
            Assert.AreEqual(16, _presenter.ReleaseCount, "Acquire/Release are not balanced on completion (AD-9).");
            Assert.AreEqual(0, _presenter.LiveCount, "Items leaked on the completion path.");
            Assert.AreEqual(0, _presenter.DoubleReleaseCount, "An item was released twice.");
            CollectionAssert.AreEquivalent(_presenter.AcquiredIds, _presenter.ReleasedIds);
        }

        [Test]
        public void CancelMidFlight_ReturnsEveryAcquiredItem()
        {
            // The path nobody tests, per AD-9. Completion is exercised constantly; this one is
            // exercised only by the bug report.
            A2BEffectHandle handle = Play(A2BTestHarness.Deterministic(16));
            A2BTestHarness.Step(_scheduler, _time, _clock);
            A2BTestHarness.Step(_scheduler, _time, _clock);

            Assert.AreEqual(16, _presenter.LiveCount, "Nothing was in flight to leak.");
            Assert.AreEqual(0, _presenter.ReleaseCount, "Items already arrived; this is not a mid-flight cancel.");

            handle.Cancel();

            Assert.AreEqual(16, _presenter.ReleaseCount, "Cancelling mid-flight leaked pooled items (AD-9).");
            Assert.AreEqual(0, _presenter.LiveCount);
            Assert.AreEqual(0, _presenter.DoubleReleaseCount);
            CollectionAssert.AreEquivalent(_presenter.AcquiredIds, _presenter.ReleasedIds);
        }

        [Test]
        public void CancelPartWayThroughAStaggeredBurst_ReleasesOnlyWhatIsLive_AndLeaksNothing()
        {
            // The nastiest shape: some items arrived (already released), some in flight, some never
            // spawned. All three must end at pool baseline with no double release.
            A2BEffectDefinition def = A2BEffectBuilder.From(A2BTestHarness.Deterministic(10))
                .Stagger(0.2f)
                .Build();
            A2BEffectHandle handle = Play(def);

            for (int i = 0; i < 6; i++) A2BTestHarness.Step(_scheduler, _time, _clock);

            Assert.Greater(_listener.ArrivedCount, 0, "No item had arrived yet; the mixed-state case is not set up.");
            Assert.Less(_listener.ArrivedCount, 10, "Everything had already arrived.");
            Assert.Greater(_presenter.LiveCount, 0, "Nothing was in flight.");
            Assert.Less(_listener.SpawnedCount, 10, "Every item had already spawned.");

            handle.Cancel();

            Assert.AreEqual(0, _presenter.LiveCount, "A mid-burst cancel leaked pooled items (AD-9).");
            Assert.AreEqual(0, _presenter.DoubleReleaseCount, "An already-arrived item was released a second time.");
            Assert.AreEqual(_presenter.AcquireCount, _presenter.ReleaseCount);
            Assert.AreEqual(1, _listener.TerminalCount);
        }

        // ---- scheduler bookkeeping ---------------------------------------------------------------

        [Test]
        public void ActiveEffectCount_TracksTheLifetime()
        {
            Assert.AreEqual(0, _scheduler.ActiveEffectCount);
            Play(A2BTestHarness.Deterministic(4));
            Assert.AreEqual(1, _scheduler.ActiveEffectCount, "Play did not register an active effect.");

            A2BTestHarness.RunToCompletion(_scheduler, _time, _clock);
            Assert.AreEqual(0, _scheduler.ActiveEffectCount, "A completed effect is still counted as active.");
        }

        [Test]
        public void ActiveItemCount_DrainsAsItemsArrive()
        {
            A2BEffectDefinition def = A2BEffectBuilder.From(A2BTestHarness.Deterministic(8))
                .Stagger(0.2f)
                .Build();
            Play(def);
            A2BTestHarness.Step(_scheduler, _time, _clock);
            Assert.AreEqual(8, _scheduler.ActiveItemCount);

            int previous = _scheduler.ActiveItemCount;
            for (int i = 0; i < 60 && _scheduler.ActiveEffectCount > 0; i++)
            {
                A2BTestHarness.Step(_scheduler, _time, _clock);
                Assert.LessOrEqual(_scheduler.ActiveItemCount, previous, "In-flight item count went up mid-burst.");
                previous = _scheduler.ActiveItemCount;
            }
            Assert.AreEqual(0, _scheduler.ActiveItemCount);
        }

        [Test]
        public void SlotsAreReused_SoConcurrencyDrivesCapacityRatherThanPlayCount()
        {
            for (int i = 0; i < 25; i++)
            {
                Play(A2BTestHarness.Deterministic(2));
                A2BTestHarness.RunToCompletion(_scheduler, _time, _clock);
            }
            Assert.AreEqual(1, _scheduler.SlotCapacity,
                "25 sequential plays created " + _scheduler.SlotCapacity + " slots; slots are not being pooled.");
        }

        [Test]
        public void Tick_WithNoActiveEffects_IsANoOp()
        {
            Assert.DoesNotThrow(() => _scheduler.Tick());
            Assert.AreEqual(0, _scheduler.ActiveEffectCount);
        }

        [Test]
        public void ConcurrentEffects_ShareNoMutableState()
        {
            // FR-3: one definition drives many concurrent effects. Different seeds, same definition.
            A2BEffectDefinition shared = A2BEffectBuilder.Create()
                .Linear().Ease(A2BEaseKind.Linear).Duration(0.4f).DurationJitter(0f)
                .Count(5).Stagger(0.05f).Scatter(1f)
                .Build();

            var presenterB = new RecordingPresenter();
            var listenerB = new RecordingListener { Clock = _clock };

            Play(shared, seed: 111u);

            var argsB = new A2BPlayArgs(_origin, _destination, presenterB, seed: 222u);
            A2BEffectHandle b = _scheduler.Play(shared, in argsB);
            _scheduler.SetTimeSource(b, _time);
            b.AddListener(listenerB);

            A2BTestHarness.RunToCompletion(_scheduler, _time, _clock);

            Assert.AreEqual(1, _listener.CompletedCount);
            Assert.AreEqual(1, listenerB.CompletedCount);
            Assert.AreEqual(5, _listener.ArrivedCount);
            Assert.AreEqual(5, listenerB.ArrivedCount);
            Assert.AreEqual(0, _presenter.LiveCount);
            Assert.AreEqual(0, presenterB.LiveCount);
        }

        [Test]
        public void SameSeed_ProducesIdenticalMotion_AcrossSeparatePlays()
        {
            // NFR-4 end to end: the whole pipeline (emission -> easing -> path -> presenter) is a
            // pure function of the seed.
            A2BEffectDefinition def = A2BEffectBuilder.Create()
                .Spiral(1.5f, 3f).Ease(A2BEaseKind.OutCubic).Duration(0.4f).DurationJitter(0.5f)
                .Count(6).Stagger(0.02f).Scatter(0.75f)
                .Build();

            Vector3[] first = CapturePositionsAfterOneTick(def, 424242u);
            Vector3[] second = CapturePositionsAfterOneTick(def, 424242u);
            Vector3[] other = CapturePositionsAfterOneTick(def, 999999u);

            Assert.AreEqual(6, first.Length, "Not every item spawned; the comparison would be vacuous.");
            Assert.AreEqual(first.Length, second.Length);
            Assert.AreEqual(first.Length, other.Length);

            for (int i = 0; i < first.Length; i++)
                Assert.AreEqual(first[i], second[i], "Replaying the same seed produced different motion (NFR-4).");

            bool anyDifferent = false;
            for (int i = 0; i < first.Length; i++)
                if (Vector3.Distance(first[i], other[i]) > 1e-5f) anyDifferent = true;
            Assert.IsTrue(anyDifferent, "A different seed produced identical motion.");
        }

        private Vector3[] CapturePositionsAfterOneTick(A2BEffectDefinition def, uint seed)
        {
            var scheduler = new A2BScheduler();
            var time = new A2BManualTimeSource();
            var presenter = new RecordingPresenter();
            var args = new A2BPlayArgs(_origin, _destination, presenter, seed: seed);

            A2BEffectHandle handle = scheduler.Play(def, in args);
            scheduler.SetTimeSource(handle, time);
            time.Advance(0.1f);
            scheduler.Tick();

            var positions = new Vector3[presenter.AcquireCount];
            for (int i = 0; i < positions.Length; i++)
                positions[i] = presenter.StateForItem(i).Position;
            return positions;
        }
    }
}
