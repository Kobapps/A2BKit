using System.Collections;
using System.Threading;
using System.Threading.Tasks;
using A2BKit.Core;
using Cysharp.Threading.Tasks;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace A2BKit.Tests.PlayMode
{
    /// <summary>
    /// FR-15 — the async surface.
    ///
    /// Awaiting an effect resolves with the terminal reason and DOES NOT THROW on cancellation: a
    /// cosmetic effect being cut short is not exceptional (AD-8). The async path also never suppresses
    /// events, nor vice versa (AD-11) — both are asserted here.
    ///
    /// These are `[Test] public async Task` per the spine: Test Framework 1.7 supports async tests
    /// natively. The UniTask.ToCoroutine bridge is deliberately not used — it is a 2021-era workaround
    /// that forfeits 1.7's fixes.
    ///
    /// The scheduler is hand-pumped with a manual time source, so the awaited completion is resolved
    /// synchronously inside Tick() and the await below is already-completed by the time it runs.
    /// </summary>
    [TestFixture]
    internal sealed class A2BAsyncTests
    {
        private const float Dt = 0.1f;
        private const float FlightDuration = 0.4f;

        private A2BScheduler _scheduler;
        private A2BManualTimeSource _time;
        private RecordingPresenter _presenter;
        private A2BStaticEndpoint _origin;
        private A2BStaticEndpoint _destination;

        [SetUp]
        public void SetUp()
        {
            _scheduler = new A2BScheduler();
            _time = new A2BManualTimeSource();
            _presenter = new RecordingPresenter();
            _origin = new A2BStaticEndpoint(Vector3.zero);
            _destination = new A2BStaticEndpoint(new Vector3(5f, 0f, 0f));
        }

        private static A2BEffectDefinition Definition(int count = 4)
            => A2BEffectBuilder.Create()
                .Linear()
                .Ease(A2BEaseKind.Linear)
                .Duration(FlightDuration)
                .DurationJitter(0f)
                .Count(count)
                .AllAtOnce()
                .Scatter(0f)
                .Build();

        private A2BEffectHandle Play(CancellationToken token = default, int count = 4)
        {
            var args = new A2BPlayArgs(_origin, _destination, _presenter, seed: 77u, cancellationToken: token);
            A2BEffectHandle handle = _scheduler.Play(Definition(count), in args);
            _scheduler.SetTimeSource(handle, _time);
            return handle;
        }

        private void Pump(int maxTicks = 200)
        {
            for (int i = 0; i < maxTicks && _scheduler.ActiveEffectCount > 0; i++)
            {
                _time.Advance(Dt);
                _scheduler.Tick();
            }
        }

        // ---- completion ------------------------------------------------------------------------------

        [Test]
        public async Task AwaitingACompletedEffect_ResolvesWithCompleted()
        {
            A2BEffectHandle handle = Play();
            UniTask<A2BCompletionReason> task = handle.ToUniTask();

            Pump();

            A2BCompletionReason reason = await task;

            Assert.AreEqual(A2BCompletionReason.Completed, reason);
            Assert.AreEqual(0, _presenter.LiveCount, "The awaited effect leaked items.");
            Assert.AreEqual(4, _presenter.ReleaseCount);
        }

        [Test]
        public async Task Awaiting_DoesNotSuppressTheEventPath()
        {
            // AD-11: "the async path never suppresses events, nor vice versa."
            var listener = new EventRecordingListener();
            A2BEffectHandle handle = Play();
            handle.AddListener(listener);
            UniTask<A2BCompletionReason> task = handle.ToUniTask();

            Pump();
            A2BCompletionReason reason = await task;

            Assert.AreEqual(A2BCompletionReason.Completed, reason);
            Assert.AreEqual(1, listener.StartedCount, "Awaiting suppressed Started.");
            Assert.AreEqual(4, listener.SpawnedCount, "Awaiting suppressed ItemSpawned.");
            Assert.AreEqual(1, listener.FirstArrivedCount, "Awaiting suppressed FirstItemArrived.");
            Assert.AreEqual(4, listener.ArrivedCount, "Awaiting suppressed ItemArrived.");
            Assert.AreEqual(1, listener.CompletedCount, "Awaiting suppressed Completed.");
            Assert.AreEqual(0, listener.CancelledCount);
        }

        [Test]
        public async Task AwaitingAnInvalidHandle_ResolvesImmediatelyAsInvalid()
        {
            // AD-8: awaiting a failed Play must resolve, not hang forever.
            A2BCompletionReason reason = await A2BEffectHandle.Invalid.ToUniTask();
            Assert.AreEqual(A2BCompletionReason.Invalid, reason);
        }

        [Test]
        public async Task AwaitingAnAlreadyFinishedEffect_ResolvesAsInvalid_RatherThanHanging()
        {
            A2BEffectHandle handle = Play();
            Pump();
            Assert.IsFalse(handle.IsValid);

            // The effect is gone, so there is nothing left to await — but it must still resolve.
            A2BCompletionReason reason = await handle.ToUniTask();
            Assert.AreEqual(A2BCompletionReason.Invalid, reason);
        }

        // ---- cancellation ------------------------------------------------------------------------------

        [Test]
        public async Task CancellingViaCancellationToken_ResolvesAsCancelled_AndDoesNotThrow()
        {
            using (var cts = new CancellationTokenSource())
            {
                A2BEffectHandle handle = Play(cts.Token);
                UniTask<A2BCompletionReason> task = handle.ToUniTask();

                _time.Advance(Dt);
                _scheduler.Tick();
                Assert.AreEqual(4, _presenter.LiveCount, "Nothing was in flight when the token was cancelled.");

                cts.Cancel();

                _time.Advance(Dt);
                _scheduler.Tick();

                A2BCompletionReason reason = await task;

                Assert.AreEqual(A2BCompletionReason.Cancelled, reason,
                    "A token cancellation did not resolve the await as Cancelled.");
                Assert.AreEqual(0, _presenter.LiveCount, "A token cancellation leaked items (AD-9).");
                Assert.AreEqual(0, _scheduler.ActiveEffectCount);
            }
        }

        [Test]
        public async Task CancellingViaTheHandle_ResolvesAsCancelled_AndDoesNotThrow()
        {
            A2BEffectHandle handle = Play();
            UniTask<A2BCompletionReason> task = handle.ToUniTask();

            _time.Advance(Dt);
            _scheduler.Tick();
            handle.Cancel();

            A2BCompletionReason reason = await task;

            Assert.AreEqual(A2BCompletionReason.Cancelled, reason);
            Assert.AreEqual(0, _presenter.LiveCount);
        }

        [Test]
        public async Task ATokenCancelledBeforeThePlay_ResolvesAsCancelled_WithoutEverSpawning()
        {
            using (var cts = new CancellationTokenSource())
            {
                cts.Cancel();
                A2BEffectHandle handle = Play(cts.Token);
                UniTask<A2BCompletionReason> task = handle.ToUniTask();

                _time.Advance(Dt);
                _scheduler.Tick();

                A2BCompletionReason reason = await task;

                Assert.AreEqual(A2BCompletionReason.Cancelled, reason);
                Assert.AreEqual(0, _presenter.AcquireCount, "An already-cancelled effect still acquired items.");
                Assert.AreEqual(0, _presenter.LiveCount);
            }
        }

        [Test]
        public async Task CancellationResolvesTheAwait_AndAlsoRaisesTheCancelledEvent()
        {
            using (var cts = new CancellationTokenSource())
            {
                var listener = new EventRecordingListener();
                A2BEffectHandle handle = Play(cts.Token);
                handle.AddListener(listener);
                UniTask<A2BCompletionReason> task = handle.ToUniTask();

                _time.Advance(Dt);
                _scheduler.Tick();
                cts.Cancel();
                _time.Advance(Dt);
                _scheduler.Tick();

                A2BCompletionReason reason = await task;

                Assert.AreEqual(A2BCompletionReason.Cancelled, reason);
                Assert.AreEqual(1, listener.CancelledCount, "The async path suppressed the Cancelled event (AD-11).");
                Assert.AreEqual(0, listener.CompletedCount);
                Assert.AreEqual(A2BCompletionReason.Cancelled, listener.LastCancelReason);
            }
        }

        [Test]
        public async Task ALostEndpoint_ResolvesTheAwaitWithEndpointLost()
        {
            var destination = new FlakyEndpoint(new Vector3(5f, 0f, 0f));
            var args = new A2BPlayArgs(_origin, destination, _presenter, seed: 77u);
            A2BEffectHandle handle = _scheduler.Play(Definition(), in args);
            _scheduler.SetTimeSource(handle, _time);
            UniTask<A2BCompletionReason> task = handle.ToUniTask();

            _time.Advance(Dt);
            _scheduler.Tick();

            destination.IsValid = false;
            _time.Advance(Dt);
            _scheduler.Tick();

            A2BCompletionReason reason = await task;

            Assert.AreEqual(A2BCompletionReason.EndpointLost, reason,
                "The await could not distinguish a lost endpoint from a plain cancellation.");
            Assert.AreEqual(0, _presenter.LiveCount);
        }

        [Test]
        public async Task AwaitingTheSameEffectIsSafe_AndTheSlotIsReusableAfterwards()
        {
            // The completion source is resolved LAST in ReleaseEffect precisely because a continuation
            // may Play() again and reuse this very slot.
            A2BEffectHandle first = Play();
            UniTask<A2BCompletionReason> task = first.ToUniTask();
            Pump();
            Assert.AreEqual(A2BCompletionReason.Completed, await task);

            A2BEffectHandle second = Play();
            Assert.AreEqual(1, _scheduler.SlotCapacity, "The awaited effect's slot was not returned to the pool.");

            UniTask<A2BCompletionReason> secondTask = second.ToUniTask();
            Pump();
            Assert.AreEqual(A2BCompletionReason.Completed, await secondTask);
        }

        // ---- frame stepping ---------------------------------------------------------------------------

        [UnityTest]
        public IEnumerator AnEffectDrivenByRealFrames_CompletesAndReleases()
        {
            // The one test that uses the engine's own clock end to end, via [UnityTest] frame stepping.
            // Everything else injects time, which is the point of AD-12 — but if the scaled time source
            // were broken, only this test would notice.
            var args = new A2BPlayArgs(_origin, _destination, _presenter, seed: 77u);
            A2BEffectDefinition def = A2BEffectBuilder.From(Definition()).Duration(0.15f).Build();

            A2BEffectHandle handle = _scheduler.Play(def, in args);
            Assert.IsTrue(handle.IsValid);

            // No SetTimeSource: this exercises A2BScaledTimeSource reading UnityEngine.Time.
            float deadline = Time.realtimeSinceStartup + 5f;
            while (_scheduler.ActiveEffectCount > 0 && Time.realtimeSinceStartup < deadline)
            {
                _scheduler.Tick();
                yield return null;
            }

            Assert.AreEqual(0, _scheduler.ActiveEffectCount, "The effect never completed on the engine clock.");
            Assert.AreEqual(4, _presenter.AcquireCount);
            Assert.AreEqual(4, _presenter.ReleaseCount, "Items leaked on the real-frame path.");
            Assert.AreEqual(0, _presenter.LiveCount);
        }

        [UnityTest]
        public IEnumerator UnscaledTimeEffect_KeepsMovingWhileTheGameIsPaused()
        {
            // FR-16's motivating case: the reward that must keep flying while a paused menu is open.
            float originalScale = Time.timeScale;
            try
            {
                Time.timeScale = 0f;

                A2BEffectDefinition def = A2BEffectBuilder.From(Definition(1)).Duration(0.15f).UseUnscaledTime(true).Build();
                var args = new A2BPlayArgs(_origin, _destination, _presenter, seed: 77u);
                _scheduler.Play(def, in args);

                float deadline = Time.realtimeSinceStartup + 5f;
                while (_scheduler.ActiveEffectCount > 0 && Time.realtimeSinceStartup < deadline)
                {
                    _scheduler.Tick();
                    yield return null;
                }

                Assert.AreEqual(0, _scheduler.ActiveEffectCount,
                    "An unscaled-time effect froze when the game paused (FR-16).");
                Assert.AreEqual(0, _presenter.LiveCount);
            }
            finally
            {
                Time.timeScale = originalScale;
            }
        }

        [UnityTest]
        public IEnumerator ScaledTimeEffect_FreezesWhileTheGameIsPaused()
        {
            // The other half of FR-16, and the reason Tick carries no delta (AD-6): the two effects
            // must be able to disagree about time in the same frame.
            float originalScale = Time.timeScale;
            try
            {
                Time.timeScale = 0f;

                // Time.deltaTime is fixed at the START of a frame, so the frame in which the pause
                // begins still carries the pre-pause delta. Let the engine clock settle to zero
                // before playing, or the assertion would be testing Unity's frame timing, not ours.
                yield return null;
                yield return null;
                Assert.AreEqual(0f, Time.deltaTime, 1e-6f, "timeScale 0 did not zero Time.deltaTime.");

                A2BEffectDefinition def = A2BEffectBuilder.From(Definition(1)).Duration(0.15f).UseUnscaledTime(false).Build();
                var args = new A2BPlayArgs(_origin, _destination, _presenter, seed: 77u);
                _scheduler.Play(def, in args);

                for (int i = 0; i < 30; i++)
                {
                    _scheduler.Tick();
                    yield return null;
                }

                Assert.AreEqual(1, _scheduler.ActiveEffectCount,
                    "A scaled-time effect kept advancing while the game was paused.");
                Assert.AreEqual(0f, _presenter.LastProgress, 1e-5f, "A scaled-time effect made progress at timeScale 0.");
            }
            finally
            {
                Time.timeScale = originalScale;
                _scheduler.CancelAll();
            }
        }

        /// <summary>An endpoint that can be invalidated mid-flight, without a scene.</summary>
        private sealed class FlakyEndpoint : IA2BEndpointProvider
        {
            public Vector3 WorldPosition;
            public bool IsValid = true;

            public FlakyEndpoint(Vector3 worldPosition) => WorldPosition = worldPosition;

            public A2BEndpointSample Resolve()
                => IsValid ? A2BEndpointSample.At(WorldPosition) : A2BEndpointSample.Invalid;
        }

        private sealed class EventRecordingListener : A2BEffectListenerBase
        {
            public int StartedCount;
            public int SpawnedCount;
            public int FirstArrivedCount;
            public int ArrivedCount;
            public int CompletedCount;
            public int CancelledCount;
            public A2BCompletionReason LastCancelReason = A2BCompletionReason.Invalid;

            public override void OnStarted(in A2BEffectHandle handle) => StartedCount++;
            public override void OnItemSpawned(in A2BEffectHandle handle, int itemIndex) => SpawnedCount++;
            public override void OnFirstItemArrived(in A2BEffectHandle handle, int itemIndex) => FirstArrivedCount++;
            public override void OnItemArrived(in A2BEffectHandle handle, int itemIndex) => ArrivedCount++;
            public override void OnCompleted(in A2BEffectHandle handle) => CompletedCount++;

            public override void OnCancelled(in A2BEffectHandle handle, A2BCompletionReason reason)
            {
                CancelledCount++;
                LastCancelReason = reason;
            }
        }
    }
}
