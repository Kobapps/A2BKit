using A2BKit.Core;
using NUnit.Framework;
using UnityEngine;

namespace A2BKit.Tests.EditMode
{
    /// <summary>
    /// FR-13 — an endpoint destroyed mid-flight.
    ///
    /// The scene case is a target GameObject being destroyed while coins are still flying at it. The
    /// requirement is that this NEVER surfaces as a MissingReferenceException in the gameplay stack
    /// (AD-8): it degrades to a clean, policy-driven outcome with the pool back at baseline (AD-9).
    /// ToggleEndpoint reproduces that without a scene — the point of the port.
    /// </summary>
    [TestFixture]
    internal sealed class A2BEndpointLostTests
    {
        private A2BScheduler _scheduler;
        private A2BManualTimeSource _time;
        private TickClock _clock;
        private RecordingPresenter _presenter;
        private RecordingListener _listener;
        private ToggleEndpoint _origin;
        private ToggleEndpoint _destination;

        [SetUp]
        public void SetUp()
        {
            _scheduler = new A2BScheduler();
            _time = new A2BManualTimeSource();
            _clock = new TickClock();
            _presenter = new RecordingPresenter();
            _listener = new RecordingListener { Clock = _clock };
            _origin = new ToggleEndpoint(new Vector3(0f, 0f, 0f));
            _destination = new ToggleEndpoint(new Vector3(8f, 0f, 0f));
        }

        private A2BEffectHandle Play(A2BEndpointLostPolicy policy, int count = 6)
        {
            A2BEffectDefinition def = A2BEffectBuilder.From(A2BTestHarness.Deterministic(count))
                .OnEndpointLost(policy)
                .Build();

            var args = new A2BPlayArgs(_origin, _destination, _presenter, seed: 4242u);
            A2BEffectHandle handle = _scheduler.Play(def, in args);
            _scheduler.SetTimeSource(handle, _time);
            handle.AddListener(_listener);
            return handle;
        }

        // ---- Cancel policy (the default) ----------------------------------------------------------

        [Test]
        public void DestinationLostMidFlight_Cancel_RaisesEndpointLost_AndReleasesEveryItem()
        {
            Play(A2BEndpointLostPolicy.Cancel);
            A2BTestHarness.Step(_scheduler, _time, _clock);
            Assert.AreEqual(6, _presenter.LiveCount, "Nothing was in flight when the endpoint was lost.");

            _destination.IsValid = false; // the target GameObject just got destroyed
            A2BTestHarness.Step(_scheduler, _time, _clock);

            Assert.AreEqual(1, _listener.CancelledCount);
            Assert.AreEqual(A2BCompletionReason.EndpointLost, _listener.LastCancelReason,
                "A lost endpoint was reported as a plain cancellation; the caller cannot distinguish it.");
            Assert.AreEqual(0, _listener.CompletedCount);
            Assert.AreEqual(1, _listener.TerminalCount, "Not exactly one terminal event (AD-9).");

            Assert.AreEqual(6, _presenter.ReleaseCount, "A lost endpoint leaked pooled items (AD-9).");
            Assert.AreEqual(0, _presenter.LiveCount);
            Assert.AreEqual(0, _presenter.DoubleReleaseCount);
            Assert.AreEqual(0, _scheduler.ActiveEffectCount);
        }

        [Test]
        public void OriginLostMidFlight_Cancel_RaisesEndpointLost()
        {
            Play(A2BEndpointLostPolicy.Cancel);
            A2BTestHarness.Step(_scheduler, _time, _clock);

            _origin.IsValid = false;
            A2BTestHarness.Step(_scheduler, _time, _clock);

            Assert.AreEqual(1, _listener.CancelledCount);
            Assert.AreEqual(A2BCompletionReason.EndpointLost, _listener.LastCancelReason);
            Assert.AreEqual(0, _presenter.LiveCount);
        }

        [Test]
        public void DestinationInvalidFromTheFirstTick_Cancel_NeverSpawnsAnItem()
        {
            _destination.IsValid = false;
            Play(A2BEndpointLostPolicy.Cancel);

            A2BTestHarness.Step(_scheduler, _time, _clock);

            Assert.AreEqual(1, _listener.StartedCount, "Started must still fire; the effect did exist.");
            Assert.AreEqual(0, _presenter.AcquireCount, "Items were acquired for an effect with no destination.");
            Assert.AreEqual(1, _listener.CancelledCount);
            Assert.AreEqual(A2BCompletionReason.EndpointLost, _listener.LastCancelReason);
            Assert.AreEqual(0, _presenter.LiveCount);
        }

        [Test]
        public void CancelIsTheDefaultPolicy()
        {
            var def = new A2BEffectDefinition();
            Assert.AreEqual(A2BEndpointLostPolicy.Cancel, def.EndpointLostPolicy,
                "The safe policy must be the default: silently flying to a stale position is the surprising one.");
        }

        // ---- UseLastKnownPosition policy -----------------------------------------------------------

        [Test]
        public void DestinationLostMidFlight_UseLastKnownPosition_CompletesInsteadOfCancelling()
        {
            Play(A2BEndpointLostPolicy.UseLastKnownPosition);
            A2BTestHarness.Step(_scheduler, _time, _clock); // banks the last known positions
            Assert.AreEqual(6, _presenter.LiveCount);

            _destination.IsValid = false;
            A2BTestHarness.RunToCompletion(_scheduler, _time, _clock);

            Assert.AreEqual(1, _listener.CompletedCount,
                "UseLastKnownPosition did not complete the effect after the endpoint went away.");
            Assert.AreEqual(0, _listener.CancelledCount);
            Assert.AreEqual(6, _listener.ArrivedCount, "Not every item arrived at the last known position.");
            Assert.AreEqual(1, _listener.TerminalCount);
            Assert.AreEqual(0, _presenter.LiveCount);
            Assert.AreEqual(0, _presenter.DoubleReleaseCount);
        }

        [Test]
        public void UseLastKnownPosition_FlightContinuesToWhereTheTargetWas_NotToTheOrigin()
        {
            var lastKnown = new Vector3(8f, 0f, 0f);
            Play(A2BEndpointLostPolicy.UseLastKnownPosition, count: 1);
            A2BTestHarness.Step(_scheduler, _time, _clock);

            _destination.IsValid = false;
            // Moving the (now invalid) provider must have no effect: the banked value is what counts.
            _destination.WorldPosition = new Vector3(-999f, -999f, -999f);

            A2BTestHarness.RunToCompletion(_scheduler, _time, _clock);

            Vector3 finalPosition = _presenter.StateForItem(0).Position;
            Assert.Less(Vector3.Distance(finalPosition, lastKnown), 1e-3f,
                "The item did not land on the endpoint's last known position (it landed at " + finalPosition + ").");
        }

        [Test]
        public void UseLastKnownPosition_ButLostOnTheVeryFirstTick_StillCancels()
        {
            // There is no "last known" position to fall back to yet, so the policy cannot apply and
            // the effect must degrade to EndpointLost rather than fly to (0,0,0).
            _destination.IsValid = false;
            Play(A2BEndpointLostPolicy.UseLastKnownPosition);

            A2BTestHarness.Step(_scheduler, _time, _clock);

            Assert.AreEqual(1, _listener.CancelledCount);
            Assert.AreEqual(A2BCompletionReason.EndpointLost, _listener.LastCancelReason);
            Assert.AreEqual(0, _listener.CompletedCount);
            Assert.AreEqual(0, _presenter.LiveCount);
        }

        [Test]
        public void EndpointRecoversAfterBeingLost_UnderUseLastKnownPosition_TracksTheLiveValueAgain()
        {
            Play(A2BEndpointLostPolicy.UseLastKnownPosition, count: 1);
            A2BTestHarness.Step(_scheduler, _time, _clock);

            _destination.IsValid = false;
            A2BTestHarness.Step(_scheduler, _time, _clock);

            _destination.IsValid = true;
            _destination.WorldPosition = new Vector3(0f, 20f, 0f);
            A2BTestHarness.RunToCompletion(_scheduler, _time, _clock);

            Assert.AreEqual(1, _listener.CompletedCount);
            Assert.Less(Vector3.Distance(_presenter.StateForItem(0).Position, new Vector3(0f, 20f, 0f)), 1e-3f,
                "The effect kept using the banked position after the endpoint came back (FR-12).");
        }

        // ---- FR-12: a moving destination ------------------------------------------------------------

        [Test]
        public void MovingDestination_IsTrackedEveryTick_AndTheItemStillLandsOnIt()
        {
            Play(A2BEndpointLostPolicy.Cancel, count: 1);

            for (int i = 0; i < 100 && _scheduler.ActiveEffectCount > 0; i++)
            {
                _destination.WorldPosition += new Vector3(0.5f, 0.25f, 0f);
                A2BTestHarness.Step(_scheduler, _time, _clock);
            }

            Assert.AreEqual(1, _listener.CompletedCount);
            Assert.Less(Vector3.Distance(_presenter.StateForItem(0).Position, _destination.WorldPosition), 1e-3f,
                "The item did not land on a destination that moved during the flight (FR-12 / AD-13).");
        }
    }
}
