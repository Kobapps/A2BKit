using System.Text.RegularExpressions;
using A2BKit.Core;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace A2BKit.Tests.EditMode
{
    /// <summary>
    /// AD-8 — failure is logged, never thrown.
    ///
    /// A2BKit is cosmetic. A coin burst that throws into a reward-granting call stack can cost a
    /// player their purchase, so every runtime and configuration fault must degrade to one actionable
    /// log line plus an invalid handle — and an invalid handle must itself be safe to hold, cancel,
    /// subscribe to and await.
    /// </summary>
    [TestFixture]
    internal sealed class A2BFailureContainmentTests
    {
        private A2BScheduler _scheduler;
        private A2BManualTimeSource _time;
        private TickClock _clock;
        private RecordingPresenter _presenter;
        private A2BStaticEndpoint _origin;
        private A2BStaticEndpoint _destination;

        [SetUp]
        public void SetUp()
        {
            _scheduler = new A2BScheduler();
            _time = new A2BManualTimeSource();
            _clock = new TickClock();
            _presenter = new RecordingPresenter();
            _origin = new A2BStaticEndpoint(Vector3.zero);
            _destination = new A2BStaticEndpoint(new Vector3(4f, 0f, 0f));
        }

        [TearDown]
        public void TearDown()
        {
            LogAssert.ignoreFailingMessages = false;
        }

        // ---- Play never throws -------------------------------------------------------------------

        [Test]
        public void Play_WithNullDefinition_LogsAndReturnsInvalid()
        {
            LogAssert.Expect(LogType.Error, new Regex(".*definition is null.*"));
            var args = new A2BPlayArgs(_origin, _destination, _presenter);

            A2BEffectHandle handle = A2BEffectHandle.Invalid;
            Assert.DoesNotThrow(() => handle = _scheduler.Play(null, in args));

            Assert.AreEqual(A2BEffectHandle.Invalid, handle);
            Assert.IsFalse(handle.IsValid);
            Assert.AreEqual(0, _scheduler.ActiveEffectCount, "A failed Play still registered an effect.");
        }

        [Test]
        public void Play_WithNullPresenter_LogsAndReturnsInvalid()
        {
            LogAssert.Expect(LogType.Error, new Regex(".*presenter.*"));
            var args = new A2BPlayArgs(_origin, _destination, null);

            A2BEffectHandle handle = A2BEffectHandle.Invalid;
            Assert.DoesNotThrow(() => handle = _scheduler.Play(A2BTestHarness.Deterministic(), in args));

            Assert.IsFalse(handle.IsValid);
            Assert.AreEqual(0, _scheduler.ActiveEffectCount);
        }

        [Test]
        public void Play_WithNullOrigin_LogsAndReturnsInvalid()
        {
            LogAssert.Expect(LogType.Error, new Regex(".*origin.*"));
            var args = new A2BPlayArgs(null, _destination, _presenter);

            A2BEffectHandle handle = A2BEffectHandle.Invalid;
            Assert.DoesNotThrow(() => handle = _scheduler.Play(A2BTestHarness.Deterministic(), in args));

            Assert.IsFalse(handle.IsValid);
            Assert.AreEqual(0, _scheduler.ActiveEffectCount);
        }

        [Test]
        public void Play_WithNullDestination_LogsAndReturnsInvalid()
        {
            LogAssert.Expect(LogType.Error, new Regex(".*destination.*"));
            var args = new A2BPlayArgs(_origin, null, _presenter);

            A2BEffectHandle handle = A2BEffectHandle.Invalid;
            Assert.DoesNotThrow(() => handle = _scheduler.Play(A2BTestHarness.Deterministic(), in args));

            Assert.IsFalse(handle.IsValid);
            Assert.AreEqual(0, _scheduler.ActiveEffectCount);
        }

        [Test]
        public void Play_WithEverythingNull_LogsAndReturnsInvalid()
        {
            LogAssert.Expect(LogType.Error, new Regex(".*definition is null.*"));

            var args = new A2BPlayArgs(null, null, null);
            A2BEffectHandle handle = A2BEffectHandle.Invalid;
            Assert.DoesNotThrow(() => handle = _scheduler.Play(null, in args));

            Assert.IsFalse(handle.IsValid);
        }

        [TestCase("Path")]
        [TestCase("Easing")]
        [TestCase("Emission")]
        [TestCase("Duration")]
        public void Play_WithAHalfConfiguredDefinition_LogsAndReturnsInvalid(string missing)
        {
            A2BEffectDefinition def = A2BTestHarness.Deterministic();
            switch (missing)
            {
                case "Path": def.Path = null; break;
                case "Easing": def.Easing = null; break;
                case "Emission": def.Emission = null; break;
                case "Duration": def.Duration = 0f; break;
            }

            LogAssert.Expect(LogType.Error, new Regex(".*Play failed.*"));
            var args = new A2BPlayArgs(_origin, _destination, _presenter);

            A2BEffectHandle handle = A2BEffectHandle.Invalid;
            Assert.DoesNotThrow(() => handle = _scheduler.Play(def, in args));

            Assert.IsFalse(handle.IsValid);
            Assert.AreEqual(0, _scheduler.ActiveEffectCount);
        }

        [Test]
        public void Validate_NamesTheOffendingField()
        {
            // FR-23: the message has to be actionable, not "something went wrong".
            var def = new A2BEffectDefinition { Path = null };
            Assert.IsFalse(def.Validate(out string error));
            StringAssert.Contains("Path", error);

            def = new A2BEffectDefinition { Easing = null };
            Assert.IsFalse(def.Validate(out error));
            StringAssert.Contains("Easing", error);

            def = new A2BEffectDefinition { Emission = null };
            Assert.IsFalse(def.Validate(out error));
            StringAssert.Contains("Emission", error);

            def = new A2BEffectDefinition { Duration = 0f };
            Assert.IsFalse(def.Validate(out error));
            StringAssert.Contains("Duration", error);

            Assert.IsTrue(new A2BEffectDefinition().Validate(out error), "A default definition does not validate.");
            Assert.IsNull(error);
        }

        // ---- an invalid handle is inert, not a landmine -------------------------------------------

        [Test]
        public void InvalidHandle_IsSafeToUse()
        {
            A2BEffectHandle handle = A2BEffectHandle.Invalid;

            Assert.DoesNotThrow(() => handle.Cancel());
            Assert.DoesNotThrow(() => handle.Cancel());
            Assert.DoesNotThrow(() => handle.AddListener(new RecordingListener()));
            Assert.DoesNotThrow(() => handle.RemoveListener(new RecordingListener()));
            Assert.DoesNotThrow(() => handle.AddListener(null));
            Assert.DoesNotThrow(() => handle.RemoveListener(null));

            Assert.IsFalse(handle.IsValid);
            Assert.AreEqual(0, handle.ItemCount);
            Assert.AreEqual(0, handle.ArrivedCount);
            Assert.DoesNotThrow(() => handle.GetHashCode());
        }

        [Test]
        public void LiveHandle_TolerantOfNullListeners()
        {
            var args = new A2BPlayArgs(_origin, _destination, _presenter);
            A2BEffectHandle handle = _scheduler.Play(A2BTestHarness.Deterministic(2), in args);
            _scheduler.SetTimeSource(handle, _time);

            Assert.DoesNotThrow(() => handle.AddListener(null));
            Assert.DoesNotThrow(() => handle.RemoveListener(null));
            Assert.DoesNotThrow(() => _scheduler.SetTimeSource(handle, null));

            A2BTestHarness.RunToCompletion(_scheduler, _time, _clock);
            Assert.AreEqual(0, _presenter.LiveCount);
        }

        [Test]
        public void SetTimeSource_OnAnInvalidHandle_IsANoOp()
        {
            Assert.DoesNotThrow(() => _scheduler.SetTimeSource(A2BEffectHandle.Invalid, _time));
        }

        // ---- a listener that throws is contained --------------------------------------------------

        [Test]
        public void AListenerThatThrows_LeaksNoItems_AndDoesNotSuppressTheRestOfTheEventSet()
        {
            // AD-8: "one bad listener cannot leak pooled items or suppress the rest of the event set."
            // A2BLog.Exception logs each throw, so the failing messages are expected here.
            LogAssert.ignoreFailingMessages = true;

            var thrower = new ThrowingListener();
            var survivor = new RecordingListener { Clock = _clock };

            var args = new A2BPlayArgs(_origin, _destination, _presenter, seed: 8u);
            A2BEffectHandle handle = _scheduler.Play(A2BTestHarness.Deterministic(6), in args);
            _scheduler.SetTimeSource(handle, _time);
            handle.AddListener(thrower);
            handle.AddListener(survivor);

            Assert.DoesNotThrow(() => A2BTestHarness.RunToCompletion(_scheduler, _time, _clock),
                "A listener exception escaped the scheduler and reached the caller (AD-8).");

            Assert.Greater(thrower.CallCount, 0, "The throwing listener was never invoked; the test is vacuous.");

            // The whole event set still reached the well-behaved listener.
            Assert.AreEqual(1, survivor.StartedCount, "Started was suppressed by a throwing listener.");
            Assert.AreEqual(6, survivor.SpawnedCount, "ItemSpawned was suppressed by a throwing listener.");
            Assert.AreEqual(1, survivor.FirstArrivedCount, "FirstItemArrived was suppressed by a throwing listener.");
            Assert.AreEqual(6, survivor.ArrivedCount, "ItemArrived was suppressed by a throwing listener.");
            Assert.AreEqual(1, survivor.CompletedCount, "Completed was suppressed by a throwing listener.");
            Assert.AreEqual(1, survivor.TerminalCount);

            // And nothing leaked.
            Assert.AreEqual(6, _presenter.AcquireCount);
            Assert.AreEqual(6, _presenter.ReleaseCount, "A throwing listener leaked pooled items (AD-8/AD-9).");
            Assert.AreEqual(0, _presenter.LiveCount);
            Assert.AreEqual(0, _presenter.DoubleReleaseCount);
            Assert.AreEqual(0, _scheduler.ActiveEffectCount);
        }

        [Test]
        public void AListenerThatThrowsIsIsolatedFromTheOnesRegisteredBeforeIt()
        {
            LogAssert.ignoreFailingMessages = true;

            var before = new RecordingListener { Clock = _clock };
            var thrower = new ThrowingListener();
            var after = new RecordingListener { Clock = _clock };

            var args = new A2BPlayArgs(_origin, _destination, _presenter, seed: 8u);
            A2BEffectHandle handle = _scheduler.Play(A2BTestHarness.Deterministic(3), in args);
            _scheduler.SetTimeSource(handle, _time);
            handle.AddListener(before);
            handle.AddListener(thrower);
            handle.AddListener(after);

            A2BTestHarness.RunToCompletion(_scheduler, _time, _clock);

            CollectionAssert.AreEqual(before.Events, after.Events,
                "A listener registered after a throwing one saw a different event sequence.");
            Assert.AreEqual(1, before.CompletedCount);
            Assert.AreEqual(1, after.CompletedCount);
        }

        [Test]
        public void AListenerThatThrowsOnCancel_StillLeavesThePoolAtBaseline()
        {
            LogAssert.ignoreFailingMessages = true;

            var thrower = new ThrowingListener();
            var survivor = new RecordingListener { Clock = _clock };

            var args = new A2BPlayArgs(_origin, _destination, _presenter, seed: 8u);
            A2BEffectHandle handle = _scheduler.Play(A2BTestHarness.Deterministic(6), in args);
            _scheduler.SetTimeSource(handle, _time);
            handle.AddListener(thrower);
            handle.AddListener(survivor);

            A2BTestHarness.Step(_scheduler, _time, _clock);
            Assert.AreEqual(6, _presenter.LiveCount);

            Assert.DoesNotThrow(() => handle.Cancel());

            Assert.AreEqual(0, _presenter.LiveCount, "Items leaked when a listener threw during cancellation.");
            Assert.AreEqual(1, survivor.CancelledCount);
        }

        // ---- an endpoint provider that throws is contained -----------------------------------------

        [Test]
        public void AnEndpointProviderThatThrows_DegradesToEndpointLost_RatherThanEscaping()
        {
            LogAssert.ignoreFailingMessages = true;

            var listener = new RecordingListener { Clock = _clock };
            var args = new A2BPlayArgs(_origin, new ThrowingEndpoint(), _presenter, seed: 8u);
            A2BEffectHandle handle = _scheduler.Play(A2BTestHarness.Deterministic(4), in args);
            _scheduler.SetTimeSource(handle, _time);
            handle.AddListener(listener);

            Assert.DoesNotThrow(() => A2BTestHarness.Step(_scheduler, _time, _clock),
                "A throwing endpoint provider escaped into the caller's stack (AD-8).");

            Assert.AreEqual(1, listener.CancelledCount);
            Assert.AreEqual(A2BCompletionReason.EndpointLost, listener.LastCancelReason);
            Assert.AreEqual(0, listener.CompletedCount);
            Assert.AreEqual(0, _presenter.LiveCount);
            Assert.AreEqual(0, _scheduler.ActiveEffectCount);
        }

        // ---- degenerate configuration --------------------------------------------------------------

        [Test]
        public void ZeroLengthEffect_OriginEqualsDestination_StillCompletesAndReleases()
        {
            var listener = new RecordingListener { Clock = _clock };
            var same = new A2BStaticEndpoint(new Vector3(2f, 2f, 2f));
            var args = new A2BPlayArgs(same, same, _presenter, seed: 8u);

            A2BEffectHandle handle = _scheduler.Play(
                A2BEffectBuilder.Create().Spiral(3f, 5f).Ease(A2BEaseKind.OutBounce)
                    .Duration(0.4f).DurationJitter(0f).Count(4).AllAtOnce().Scatter(0f).Build(),
                in args);
            _scheduler.SetTimeSource(handle, _time);
            handle.AddListener(listener);

            A2BTestHarness.RunToCompletion(_scheduler, _time, _clock);

            Assert.AreEqual(1, listener.CompletedCount, "A zero-length effect never arrived.");
            Assert.AreEqual(4, listener.ArrivedCount);
            Assert.AreEqual(0, _presenter.LiveCount);
        }

        [Test]
        public void SingleItemEffect_CompletesAndRaisesFirstItemArrivedOnce()
        {
            var listener = new RecordingListener { Clock = _clock };
            var args = new A2BPlayArgs(_origin, _destination, _presenter, seed: 8u);
            A2BEffectHandle handle = _scheduler.Play(A2BTestHarness.Deterministic(1), in args);
            _scheduler.SetTimeSource(handle, _time);
            handle.AddListener(listener);

            A2BTestHarness.RunToCompletion(_scheduler, _time, _clock);

            Assert.AreEqual(1, listener.FirstArrivedCount);
            Assert.AreEqual(1, listener.ArrivedCount);
            Assert.AreEqual(1, listener.CompletedCount);
        }

        [Test]
        public void ItemSpawnInfo_CarriesThePerPlayTextAndValue()
        {
            // FR-7's payload data arrives through A2BItemSpawnInfo, not through the definition:
            // the definition is a reusable asset and must not bake per-play data.
            var args = new A2BPlayArgs(_origin, _destination, _presenter, text: "+250", value: 250f, seed: 8u);
            A2BEffectHandle handle = _scheduler.Play(A2BTestHarness.Deterministic(3), in args);
            _scheduler.SetTimeSource(handle, _time);

            A2BTestHarness.Step(_scheduler, _time, _clock);

            Assert.AreEqual(3, _presenter.SpawnInfos.Count);
            for (int i = 0; i < 3; i++)
            {
                Assert.AreEqual("+250", _presenter.SpawnInfos[i].Text);
                Assert.AreEqual(250f, _presenter.SpawnInfos[i].Value, 0f);
                Assert.AreEqual(i, _presenter.SpawnInfos[i].ItemIndex, "Items did not spawn in index order.");
                Assert.AreEqual(3, _presenter.SpawnInfos[i].ItemCount);
                Assert.AreEqual(A2BRandom.DeriveSeed(8u, i), _presenter.SpawnInfos[i].Seed,
                    "The per-item seed handed to the presenter is not the AD-10 derived seed.");
            }
        }
    }
}
