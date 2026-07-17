using A2BKit.Core;
using NUnit.Framework;
using UnityEngine;

namespace A2BKit.Tests.EditMode
{
    /// <summary>
    /// AD-7 / FR-27 — the generation stamp.
    ///
    /// Slots are pooled and reused. Without the stamp, a stale copy of a handle would silently
    /// address a DIFFERENT effect that had since taken the slot — cancelling someone else's coins.
    /// That defect is silent, intermittent and near-impossible to diagnose in the field, so the test
    /// that matters is the aliasing one below: it deliberately forces slot reuse and then proves the
    /// stale handle is inert.
    /// </summary>
    [TestFixture]
    internal sealed class A2BEffectHandleTests
    {
        private A2BScheduler _scheduler;
        private A2BManualTimeSource _time;
        private TickClock _clock;
        private A2BStaticEndpoint _origin;
        private A2BStaticEndpoint _destination;

        [SetUp]
        public void SetUp()
        {
            _scheduler = new A2BScheduler();
            _time = new A2BManualTimeSource();
            _clock = new TickClock();
            _origin = new A2BStaticEndpoint(Vector3.zero);
            _destination = new A2BStaticEndpoint(new Vector3(5f, 0f, 0f));
        }

        private A2BEffectHandle Play(IA2BPresenter presenter, IA2BEffectListener listener, int count = 4)
        {
            var args = new A2BPlayArgs(_origin, _destination, presenter, seed: 1234u);
            A2BEffectHandle handle = _scheduler.Play(A2BTestHarness.Deterministic(count), in args);
            _scheduler.SetTimeSource(handle, _time);
            if (listener != null) handle.AddListener(listener);
            return handle;
        }

        // ---- THE aliasing test -------------------------------------------------------------------

        [Test]
        public void StaleHandle_AfterItsSlotIsReused_CannotCancelTheEffectThatTookTheSlot()
        {
            var presenterA = new RecordingPresenter();
            var listenerA = new RecordingListener { Clock = _clock };
            A2BEffectHandle staleHandle = Play(presenterA, listenerA);

            A2BTestHarness.RunToCompletion(_scheduler, _time, _clock);
            Assert.AreEqual(1, listenerA.CompletedCount, "Effect A did not complete; the setup is wrong.");

            // Effect B must land in A's freed slot, or this test proves nothing.
            var presenterB = new RecordingPresenter();
            var listenerB = new RecordingListener { Clock = _clock };
            A2BEffectHandle liveHandle = Play(presenterB, listenerB);
            Assert.AreEqual(1, _scheduler.SlotCapacity,
                "Effect B did not reuse effect A's slot; the aliasing scenario was never reproduced.");

            // The stamp is what makes the stale handle inert.
            Assert.IsFalse(staleHandle.IsValid, "A handle to a finished effect still reports itself valid (AD-7).");
            Assert.IsTrue(liveHandle.IsValid);

            staleHandle.Cancel();

            Assert.AreEqual(0, listenerB.CancelledCount,
                "A stale handle cancelled the effect that reused its slot — the exact defect AD-7 exists to prevent.");
            Assert.IsTrue(liveHandle.IsValid, "Effect B was killed by a stale handle.");

            A2BTestHarness.RunToCompletion(_scheduler, _time, _clock);

            Assert.AreEqual(1, listenerB.CompletedCount, "Effect B did not complete normally.");
            Assert.AreEqual(0, listenerB.CancelledCount);
            Assert.AreEqual(1, listenerA.CompletedCount, "Effect A received a second terminal event.");
            Assert.AreEqual(0, presenterB.LiveCount);
        }

        [Test]
        public void StaleHandle_AfterItsSlotIsReused_ReadsAsEmptyRatherThanReportingEffectBsState()
        {
            var staleHandle = Play(new RecordingPresenter(), null, count: 4);
            A2BTestHarness.RunToCompletion(_scheduler, _time, _clock);

            Play(new RecordingPresenter(), null, count: 9);
            Assert.AreEqual(1, _scheduler.SlotCapacity, "The slot was not reused; the test is vacuous.");

            Assert.AreEqual(0, staleHandle.ItemCount, "A stale handle reported the NEW effect's item count.");
            Assert.AreEqual(0, staleHandle.ArrivedCount, "A stale handle reported the NEW effect's arrival count.");
        }

        [Test]
        public void StaleHandle_CannotAddAListenerToTheEffectThatTookItsSlot()
        {
            var staleHandle = Play(new RecordingPresenter(), null);
            A2BTestHarness.RunToCompletion(_scheduler, _time, _clock);

            var listenerB = new RecordingListener { Clock = _clock };
            Play(new RecordingPresenter(), listenerB);
            Assert.AreEqual(1, _scheduler.SlotCapacity);

            var eavesdropper = new RecordingListener { Clock = _clock };
            staleHandle.AddListener(eavesdropper);

            A2BTestHarness.RunToCompletion(_scheduler, _time, _clock);

            Assert.AreEqual(0, eavesdropper.StartedCount,
                "A stale handle subscribed a listener to somebody else's effect.");
            Assert.AreEqual(1, listenerB.CompletedCount);
        }

        [Test]
        public void Handle_BecomesInvalidTheInstantTheEffectEnds_NotAtTheTickBoundary()
        {
            // AD-7 + AD-17: the generation bumps immediately on release even though the slot itself
            // is not reusable until the tick boundary.
            A2BEffectHandle handle = Play(new RecordingPresenter(), null);
            A2BTestHarness.Step(_scheduler, _time, _clock);
            Assert.IsTrue(handle.IsValid);

            handle.Cancel();

            Assert.IsFalse(handle.IsValid, "The handle survived its own effect's cancellation.");
        }

        // ---- value semantics ---------------------------------------------------------------------

        [Test]
        public void Handle_IsAValueType_SoCopiesAreInterchangeableWhileTheEffectLives()
        {
            A2BEffectHandle handle = Play(new RecordingPresenter(), null);
            A2BEffectHandle copy = handle;

            Assert.IsTrue(copy.IsValid);
            Assert.AreEqual(handle, copy);
            Assert.IsTrue(handle == copy);
            Assert.AreEqual(handle.GetHashCode(), copy.GetHashCode());
            Assert.AreEqual(handle.ItemCount, copy.ItemCount);

            copy.Cancel();
            Assert.IsFalse(handle.IsValid, "Cancelling a copy did not cancel the effect both copies refer to.");
        }

        [Test]
        public void Handles_ToDifferentEffects_AreNotEqual()
        {
            A2BEffectHandle a = Play(new RecordingPresenter(), null);
            A2BEffectHandle b = Play(new RecordingPresenter(), null);

            Assert.AreNotEqual(a, b);
            Assert.IsTrue(a != b);
        }

        [Test]
        public void Handle_ToAReusedSlot_IsNotEqualToTheStaleHandle()
        {
            A2BEffectHandle stale = Play(new RecordingPresenter(), null);
            A2BTestHarness.RunToCompletion(_scheduler, _time, _clock);
            A2BEffectHandle reused = Play(new RecordingPresenter(), null);

            Assert.AreEqual(1, _scheduler.SlotCapacity, "The slot was not reused; the test is vacuous.");
            Assert.AreNotEqual(stale, reused,
                "Two handles to the same slot compare equal across a reuse — the stamp is not part of identity.");
        }

        [Test]
        public void InvalidHandle_IsNotEqualToALiveHandle()
        {
            A2BEffectHandle live = Play(new RecordingPresenter(), null);
            Assert.AreNotEqual(A2BEffectHandle.Invalid, live);
            Assert.AreEqual(A2BEffectHandle.Invalid, A2BEffectHandle.Invalid);
            Assert.IsFalse(A2BEffectHandle.Invalid.Equals("not a handle"));
        }

        [Test]
        public void LiveHandle_ReportsItemAndArrivalCounts()
        {
            A2BEffectHandle handle = Play(new RecordingPresenter(), null, count: 7);
            Assert.AreEqual(7, handle.ItemCount);
            Assert.AreEqual(0, handle.ArrivedCount);

            A2BTestHarness.Step(_scheduler, _time, _clock);
            Assert.AreEqual(0, handle.ArrivedCount, "Items arrived before their duration elapsed.");
        }
    }
}
