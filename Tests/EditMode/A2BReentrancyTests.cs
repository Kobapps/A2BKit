using A2BKit.Core;
using NUnit.Framework;
using UnityEngine;

namespace A2BKit.Tests.EditMode
{
    /// <summary>
    /// AD-17 — playing an effect from inside an ItemArrived callback is the DOCUMENTED use case
    /// (start the counter roll-up, spawn a follow-up burst), not an edge case. Growing the slot list
    /// mid-loop would either allocate or corrupt the index loop, so effects created during a tick
    /// wait in a pending queue and take their first advance on the NEXT tick with a full delta.
    /// </summary>
    [TestFixture]
    internal sealed class A2BReentrancyTests
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
            _destination = new A2BStaticEndpoint(new Vector3(3f, 1f, 0f));
        }

        /// <summary>Plays a follow-up effect from OnItemArrived — the AD-17 scenario, verbatim.</summary>
        private sealed class PlayOnArrivalListener : A2BEffectListenerBase
        {
            public A2BScheduler Scheduler;
            public A2BEffectDefinition Definition;
            public A2BPlayArgs Args;
            public IA2BTimeSource TimeSource;
            public TickClock Clock;
            public RecordingListener FollowUpListener;

            public A2BEffectHandle FollowUp;
            public bool HasPlayed;
            public int PlayedOnTick = -1;

            /// <summary>Read from INSIDE the tick, while the new effect is still on the pending queue.</summary>
            public int ActiveCountAtPlayTime = -1;

            public override void OnItemArrived(in A2BEffectHandle handle, int itemIndex)
            {
                if (HasPlayed) return;
                HasPlayed = true;
                PlayedOnTick = Clock.Tick;

                FollowUp = Scheduler.Play(Definition, in Args);
                Scheduler.SetTimeSource(FollowUp, TimeSource);
                FollowUp.AddListener(FollowUpListener);
                ActiveCountAtPlayTime = Scheduler.ActiveEffectCount;
            }
        }

        /// <summary>Cancels its own effect from OnItemArrived — release must defer to the tick boundary.</summary>
        private sealed class CancelSelfOnArrivalListener : A2BEffectListenerBase
        {
            public A2BEffectHandle Handle;
            public int ArrivalsSeen;

            public override void OnItemArrived(in A2BEffectHandle handle, int itemIndex)
            {
                ArrivalsSeen++;
                Handle.Cancel();
            }
        }

        /// <summary>Calls Tick() from inside a callback. AD-17 says the re-entrant Tick is ignored.</summary>
        private sealed class TickFromCallbackListener : A2BEffectListenerBase
        {
            public A2BScheduler Scheduler;
            public int ReentrantTickAttempts;

            public override void OnStarted(in A2BEffectHandle handle)
            {
                ReentrantTickAttempts++;
                Scheduler.Tick();
            }
        }

        [Test]
        public void PlayingFromOnItemArrived_StartsTheNewEffectOnTheNextTick_AndBothComplete()
        {
            var presenterA = new RecordingPresenter();
            var listenerA = new RecordingListener { Clock = _clock };
            var presenterB = new RecordingPresenter();
            var listenerB = new RecordingListener { Clock = _clock };

            var reentrant = new PlayOnArrivalListener
            {
                Scheduler = _scheduler,
                Definition = A2BTestHarness.Deterministic(3),
                Args = new A2BPlayArgs(_origin, _destination, presenterB, seed: 55u),
                TimeSource = _time,
                Clock = _clock,
                FollowUpListener = listenerB
            };

            var argsA = new A2BPlayArgs(_origin, _destination, presenterA, seed: 11u);
            A2BEffectHandle a = _scheduler.Play(A2BTestHarness.Deterministic(3), in argsA);
            _scheduler.SetTimeSource(a, _time);
            a.AddListener(listenerA);
            a.AddListener(reentrant);

            // Tick until effect A's first arrival triggers the re-entrant Play.
            for (int i = 0; i < 20 && !reentrant.HasPlayed; i++)
                A2BTestHarness.Step(_scheduler, _time, _clock);

            Assert.IsTrue(reentrant.HasPlayed, "The re-entrant Play never happened; the scenario is not set up.");
            Assert.AreEqual(0, listenerB.StartedCount,
                "The effect created during the tick was advanced inside the same tick (AD-17).");
            Assert.AreEqual(0, presenterB.AcquireCount, "The pending effect spawned items in the tick it was created.");
            Assert.IsTrue(reentrant.FollowUp.IsValid, "The pending effect's handle is not usable before its first tick.");

            A2BTestHarness.Step(_scheduler, _time, _clock);

            Assert.AreEqual(1, listenerB.StartedCount, "The pending effect did not start on the following tick.");
            Assert.AreEqual(reentrant.PlayedOnTick + 1, listenerB.StartedTick,
                "The pending effect did not take its first advance on exactly the next tick (AD-17).");

            A2BTestHarness.RunToCompletion(_scheduler, _time, _clock);

            Assert.AreEqual(1, listenerA.CompletedCount, "The originating effect did not complete.");
            Assert.AreEqual(1, listenerB.CompletedCount, "The re-entrantly created effect did not complete.");
            Assert.AreEqual(3, listenerA.ArrivedCount);
            Assert.AreEqual(3, listenerB.ArrivedCount);
            Assert.AreEqual(0, presenterA.LiveCount, "The originating effect leaked items.");
            Assert.AreEqual(0, presenterB.LiveCount, "The re-entrant effect leaked items.");
            Assert.AreEqual(0, presenterA.DoubleReleaseCount);
            Assert.AreEqual(0, presenterB.DoubleReleaseCount);
        }

        [Test]
        public void PendingEffect_IsCountedAsActiveImmediately()
        {
            // FR-22's overlay reads ActiveEffectCount. An effect that exists but is invisible to the
            // count for a frame would make the overlay lie.
            var reentrant = new PlayOnArrivalListener
            {
                Scheduler = _scheduler,
                Definition = A2BTestHarness.Deterministic(2),
                Args = new A2BPlayArgs(_origin, _destination, new RecordingPresenter(), seed: 55u),
                TimeSource = _time,
                Clock = _clock,
                FollowUpListener = new RecordingListener { Clock = _clock }
            };

            var args = new A2BPlayArgs(_origin, _destination, new RecordingPresenter(), seed: 11u);
            A2BEffectHandle a = _scheduler.Play(A2BTestHarness.Deterministic(2), in args);
            _scheduler.SetTimeSource(a, _time);
            a.AddListener(reentrant);

            for (int i = 0; i < 20 && !reentrant.HasPlayed; i++)
                A2BTestHarness.Step(_scheduler, _time, _clock);

            // Read at the instant of the re-entrant Play — the new effect is on the pending queue and
            // the originating effect has not yet been retired, so both must be counted.
            Assert.AreEqual(2, reentrant.ActiveCountAtPlayTime,
                "An effect on the pending queue is invisible to ActiveEffectCount, so FR-22's overlay would under-report.");

            // After the tick boundary only the (now admitted) follow-up remains.
            Assert.AreEqual(1, _scheduler.ActiveEffectCount);
        }

        [Test]
        public void ManyReentrantPlays_DoNotCorruptTheTick()
        {
            // Ten listeners each spawning a follow-up from the same arrival: the pending queue must
            // absorb all of them and every effect must reach a terminal state exactly once.
            var followUps = new RecordingListener[10];
            var presenters = new RecordingPresenter[10];

            var argsA = new A2BPlayArgs(_origin, _destination, new RecordingPresenter(), seed: 11u);
            A2BEffectHandle a = _scheduler.Play(A2BTestHarness.Deterministic(2), in argsA);
            _scheduler.SetTimeSource(a, _time);

            for (int i = 0; i < 10; i++)
            {
                presenters[i] = new RecordingPresenter();
                followUps[i] = new RecordingListener { Clock = _clock };
                a.AddListener(new PlayOnArrivalListener
                {
                    Scheduler = _scheduler,
                    Definition = A2BTestHarness.Deterministic(4),
                    Args = new A2BPlayArgs(_origin, _destination, presenters[i], seed: (uint)(100 + i)),
                    TimeSource = _time,
                    Clock = _clock,
                    FollowUpListener = followUps[i]
                });
            }

            A2BTestHarness.RunToCompletion(_scheduler, _time, _clock);

            for (int i = 0; i < 10; i++)
            {
                Assert.AreEqual(1, followUps[i].CompletedCount, "Follow-up effect " + i + " did not complete.");
                Assert.AreEqual(1, followUps[i].TerminalCount);
                Assert.AreEqual(4, followUps[i].ArrivedCount);
                Assert.AreEqual(0, presenters[i].LiveCount, "Follow-up effect " + i + " leaked items.");
            }
            Assert.AreEqual(0, _scheduler.ActiveEffectCount);
        }

        [Test]
        public void CancellingFromOnItemArrived_DefersReleaseToTheTickBoundary_AndLeaksNothing()
        {
            var presenter = new RecordingPresenter();
            var listener = new RecordingListener { Clock = _clock };
            var canceller = new CancelSelfOnArrivalListener();

            A2BEffectDefinition def = A2BEffectBuilder.From(A2BTestHarness.Deterministic(8))
                .Stagger(0.2f)
                .Build();

            var args = new A2BPlayArgs(_origin, _destination, presenter, seed: 3u);
            A2BEffectHandle handle = _scheduler.Play(def, in args);
            _scheduler.SetTimeSource(handle, _time);
            canceller.Handle = handle;
            handle.AddListener(listener);
            handle.AddListener(canceller);

            A2BTestHarness.RunToCompletion(_scheduler, _time, _clock);

            Assert.AreEqual(1, canceller.ArrivalsSeen, "Cancelling from a callback did not stop the burst.");
            Assert.AreEqual(1, listener.TerminalCount, "Cancelling from a callback raised more than one terminal event.");
            Assert.AreEqual(1, listener.CancelledCount);
            Assert.AreEqual(0, listener.CompletedCount);
            Assert.AreEqual(0, presenter.LiveCount, "Cancelling from a callback leaked pooled items (AD-9).");
            Assert.AreEqual(0, presenter.DoubleReleaseCount);
            Assert.AreEqual(0, _scheduler.ActiveEffectCount);
        }

        [Test]
        public void TickFromInsideACallback_IsIgnoredRatherThanCorruptingTheLoop()
        {
            var presenter = new RecordingPresenter();
            var listener = new RecordingListener { Clock = _clock };
            var reentrantTicker = new TickFromCallbackListener { Scheduler = _scheduler };

            var args = new A2BPlayArgs(_origin, _destination, presenter, seed: 9u);
            A2BEffectHandle handle = _scheduler.Play(A2BTestHarness.Deterministic(4), in args);
            _scheduler.SetTimeSource(handle, _time);
            handle.AddListener(listener);
            handle.AddListener(reentrantTicker);

            Assert.DoesNotThrow(() => A2BTestHarness.RunToCompletion(_scheduler, _time, _clock));

            Assert.AreEqual(1, reentrantTicker.ReentrantTickAttempts, "The re-entrant Tick scenario never ran.");
            Assert.AreEqual(4, listener.ArrivedCount, "The re-entrant Tick double-advanced the effect.");
            Assert.AreEqual(1, listener.TerminalCount);
            Assert.AreEqual(0, presenter.LiveCount);
        }
    }
}
