using A2BKit.Core;
using NUnit.Framework;
using UnityEngine;

namespace A2BKit.Tests.EditMode
{
    /// <summary>
    /// FR-10 / SM-4 — open/closed, proved rather than asserted.
    ///
    /// Every extension type below is declared HERE, inside the test assembly, and reaches the tick
    /// path with zero edits to any shipped file. If adding a path or an easing ever required touching
    /// a switch, an enum or a registry in A2BKit.Core, these tests could not be written at all.
    /// </summary>
    [TestFixture]
    internal sealed class A2BExtensibilityTests
    {
        private A2BScheduler _scheduler;
        private A2BManualTimeSource _time;
        private TickClock _clock;
        private RecordingPresenter _presenter;
        private RecordingListener _listener;
        private A2BStaticEndpoint _origin;
        private A2BStaticEndpoint _destination;

        private static readonly Vector3 OriginPos = new Vector3(1f, -2f, 3f);
        private static readonly Vector3 DestinationPos = new Vector3(9f, 6f, -1f);

        private const uint Seed = 0xFEEDu;
        private const float Duration = 0.4f;
        private const float Dt = 0.1f;

        [SetUp]
        public void SetUp()
        {
            _scheduler = new A2BScheduler();
            _time = new A2BManualTimeSource();
            _clock = new TickClock();
            _presenter = new RecordingPresenter();
            _listener = new RecordingListener { Clock = _clock };
            _origin = new A2BStaticEndpoint(OriginPos);
            _destination = new A2BStaticEndpoint(DestinationPos);
        }

        // ---- the user-authored extensions --------------------------------------------------------

        /// <summary>
        /// A custom trajectory a package consumer might write: a lateral bulge that decays to zero at
        /// both ends, so AD-13's endpoint invariant holds. Stateless, and a class, per AD-2.
        /// </summary>
        private sealed class BulgePath : IA2BPath
        {
            public float Bulge = 1.5f;

            public Vector3 Evaluate(in A2BPathContext ctx, float t)
            {
                Vector3 straight = Vector3.LerpUnclamped(ctx.Origin, ctx.Destination, t);
                float envelope = Mathf.Sin(t * Mathf.PI); // exactly 0 at both ends
                return straight + Vector3.right * (envelope * Bulge);
            }
        }

        /// <summary>A custom easing that is NOT in A2BEaseKind: quintic ease-in.</summary>
        private sealed class QuinticInEasing : IA2BEasing
        {
            public float Evaluate(float t)
            {
                t = Mathf.Clamp01(t);
                return t * t * t * t * t;
            }
        }

        /// <summary>A custom emission: a fixed count, no delay, no scatter, entirely user-defined.</summary>
        private sealed class PairEmission : IA2BEmission
        {
            public int ResolveItemCount(uint effectSeed) => 2;
            public float ResolveDelay(uint effectSeed, int itemIndex, int itemCount) => itemIndex * 0.05f;
            public Vector3 ResolveScatter(uint effectSeed, int itemIndex, int itemCount) => Vector3.zero;

            // ScatterRadius is on the port precisely so a custom emission like this one gets its
            // scatter honoured. The scheduler used to downcast to A2BBurstEmission to read it, which
            // silently forced every custom emission to zero — an open/closed break (FR-10).
            public float ScatterRadius => 0f;
        }

        // ---- conformance -------------------------------------------------------------------------

        [Test]
        public void CustomPath_SatisfiesTheSharedEndpointInvariant()
        {
            // A2BPathConformance lives in Core precisely so consumers can run it against their own
            // paths without depending on this test assembly.
            var path = new BulgePath { Bulge = 40f };
            var ctx = new A2BPathContext(OriginPos, DestinationPos, 0, 1, Seed);
            Assert.IsTrue(A2BPathConformance.SatisfiesEndpointInvariant(path, in ctx));
        }

        [Test]
        public void CustomEasing_IsNormalized()
        {
            IA2BEasing easing = new QuinticInEasing();
            Assert.AreEqual(0f, easing.Evaluate(0f), 1e-5f);
            Assert.AreEqual(1f, easing.Evaluate(1f), 1e-5f);
        }

        // ---- reaching the tick path --------------------------------------------------------------

        [Test]
        public void CustomPath_AndCustomEasing_DriveTheRealTickPath_WithNoEditsToShippedCode()
        {
            var path = new BulgePath { Bulge = 2.5f };
            var easing = new QuinticInEasing();

            A2BEffectDefinition def = A2BEffectBuilder.Create()
                .Path(path)
                .Easing(easing)
                .Duration(Duration)
                .DurationJitter(0f)
                .Count(3)
                .AllAtOnce()
                .Scatter(0f)
                .Build();

            var args = new A2BPlayArgs(_origin, _destination, _presenter, seed: Seed);
            A2BEffectHandle handle = _scheduler.Play(def, in args);
            _scheduler.SetTimeSource(handle, _time);
            handle.AddListener(_listener);

            A2BTestHarness.Step(_scheduler, _time, _clock, Dt);

            // Reproduce the scheduler's arithmetic independently: elapsed is exactly one dt, delay is
            // zero, scatter is zero, and the presenter's working space is the identity.
            float rawT = Dt / Duration;
            float easedT = easing.Evaluate(rawT);
            var ctx = new A2BPathContext(OriginPos, DestinationPos, 0, 3, A2BRandom.DeriveSeed(Seed, 0));
            Vector3 expected = path.Evaluate(in ctx, easedT);

            A2BVisualState state = _presenter.StateForItem(0);
            Assert.AreEqual(expected.x, state.Position.x, 1e-4f, "The custom path did not drive item 0's X.");
            Assert.AreEqual(expected.y, state.Position.y, 1e-4f, "The custom path did not drive item 0's Y.");
            Assert.AreEqual(expected.z, state.Position.z, 1e-4f, "The custom path did not drive item 0's Z.");
            Assert.AreEqual(rawT, state.Progress, 1e-5f, "Progress reports the EASED t; it must report the raw t.");

            A2BTestHarness.RunToCompletion(_scheduler, _time, _clock, Dt);

            Assert.AreEqual(1, _listener.CompletedCount, "An effect built from user-authored strategies did not complete.");
            Assert.AreEqual(3, _listener.ArrivedCount);
            Assert.AreEqual(0, _presenter.LiveCount);
        }

        [Test]
        public void Easing_IsAppliedBeforeThePath_NotAfter()
        {
            // FR-11 / AD-13: easing reparameterizes t BEFORE Evaluate. If it were applied to the
            // output instead, a path that is not a straight line would visibly diverge. Quintic-in at
            // t=0.25 gives ~0.00098 — nowhere near linear — so the two orderings are far apart.
            var path = new BulgePath { Bulge = 6f };
            var easing = new QuinticInEasing();

            A2BEffectDefinition def = A2BEffectBuilder.Create()
                .Path(path).Easing(easing).Duration(Duration).DurationJitter(0f)
                .Count(1).AllAtOnce().Scatter(0f)
                .Build();

            var args = new A2BPlayArgs(_origin, _destination, _presenter, seed: Seed);
            A2BEffectHandle handle = _scheduler.Play(def, in args);
            _scheduler.SetTimeSource(handle, _time);
            A2BTestHarness.Step(_scheduler, _time, _clock, Dt);

            float rawT = Dt / Duration;                 // 0.25
            float easedT = easing.Evaluate(rawT);       // 0.25^5 ~= 0.00098
            var ctx = new A2BPathContext(OriginPos, DestinationPos, 0, 1, A2BRandom.DeriveSeed(Seed, 0));

            Vector3 easedIntoThePath = path.Evaluate(in ctx, easedT);
            Vector3 pathWithoutEasing = path.Evaluate(in ctx, rawT);

            Assert.Greater(Vector3.Distance(easedIntoThePath, pathWithoutEasing), 0.1f,
                "The eased and un-eased positions coincide here; this test cannot detect the defect.");

            Vector3 actual = _presenter.StateForItem(0).Position;
            Assert.Less(Vector3.Distance(actual, easedIntoThePath), 1e-3f,
                "The path was not evaluated at the EASED t (FR-11 / AD-13: easing reparameterizes t before the path).");
            Assert.Greater(Vector3.Distance(actual, pathWithoutEasing), 0.1f,
                "The path was evaluated at the raw t: the easing never reached it.");
        }

        /// <summary>A custom emission that DOES scatter — the case the port's ScatterRadius exists for.</summary>
        private sealed class FixedScatterEmission : IA2BEmission
        {
            public const float Radius = 3f;
            public static readonly Vector3 UnitOffset = new Vector3(1f, 0f, 0f);

            public int ResolveItemCount(uint effectSeed) => 1;
            public float ResolveDelay(uint effectSeed, int itemIndex, int itemCount) => 0f;
            public Vector3 ResolveScatter(uint effectSeed, int itemIndex, int itemCount) => UnitOffset;
            public float ScatterRadius => Radius;
        }

        [Test]
        public void CustomEmission_GetsItsScatterHonoured_JustLikeTheBuiltInOne()
        {
            // The regression this locks in: the scheduler used to reach ScatterRadius by downcasting
            // to A2BBurstEmission, which silently gave EVERY custom emission a radius of zero. That
            // reads as "scatter just doesn't work for my emission" and is near-impossible to trace,
            // and it is an open/closed break (FR-10) — so the radius is read through the port.
            A2BEffectDefinition def = A2BEffectBuilder.Create()
                .Linear().Ease(A2BEaseKind.Linear).Duration(Duration).DurationJitter(0f)
                .Emission(new FixedScatterEmission())
                .Build();

            var args = new A2BPlayArgs(_origin, _destination, _presenter, seed: Seed);
            A2BEffectHandle handle = _scheduler.Play(def, in args);
            _scheduler.SetTimeSource(handle, _time);

            A2BTestHarness.Step(_scheduler, _time, _clock, Dt);

            Assert.Greater(_presenter.ScaleScatterCallCount, 0,
                "The presenter's ScaleScatter was never called: a custom emission's scatter radius was dropped (FR-10).");
            Assert.AreEqual(FixedScatterEmission.Radius, _presenter.LastScatterRadius, 1e-5f,
                "The custom emission's ScatterRadius did not reach the presenter (AD-16).");
            Assert.AreEqual(FixedScatterEmission.UnitOffset, _presenter.LastScatterUnit,
                "The custom emission's unitless offset did not reach the presenter.");

            // And it actually moved the item: the path starts from origin + scaled scatter (AD-16).
            float rawT = Dt / Duration;
            Vector3 scatteredOrigin = OriginPos + FixedScatterEmission.UnitOffset * FixedScatterEmission.Radius;
            Vector3 expected = Vector3.LerpUnclamped(scatteredOrigin, DestinationPos, rawT);

            Assert.Less(Vector3.Distance(_presenter.StateForItem(0).Position, expected), 1e-3f,
                "Scatter was reported to the presenter but never applied to the item's origin.");
        }

        [Test]
        public void CustomEmission_DrivesItemCountAndRelease()
        {
            A2BEffectDefinition def = A2BEffectBuilder.Create()
                .Linear().Ease(A2BEaseKind.Linear).Duration(Duration).DurationJitter(0f)
                .Emission(new PairEmission())
                .Build();

            var args = new A2BPlayArgs(_origin, _destination, _presenter, seed: Seed);
            A2BEffectHandle handle = _scheduler.Play(def, in args);
            _scheduler.SetTimeSource(handle, _time);
            handle.AddListener(_listener);

            Assert.AreEqual(2, handle.ItemCount, "The custom emission's item count did not reach the scheduler.");

            A2BTestHarness.RunToCompletion(_scheduler, _time, _clock, Dt);

            Assert.AreEqual(2, _listener.ArrivedCount);
            Assert.AreEqual(2, _presenter.AcquireCount);
            Assert.AreEqual(1, _listener.CompletedCount);
        }

        [Test]
        public void CustomTimeSource_DrivesTheSimulation()
        {
            // AD-12: IA2BTimeSource is the only seam that reads a clock, which is why a test can own
            // time completely without a scene or a frame.
            var doubleSpeed = new ScaledManualTimeSource { Multiplier = 2f, Source = _time };

            var args = new A2BPlayArgs(_origin, _destination, _presenter, seed: Seed);
            A2BEffectHandle handle = _scheduler.Play(A2BTestHarness.Deterministic(1, Duration), in args);
            _scheduler.SetTimeSource(handle, doubleSpeed);

            A2BTestHarness.Step(_scheduler, _time, _clock, Dt);

            // One dt of 0.1 at 2x is 0.2 of a 0.4-second flight: exactly halfway.
            Assert.AreEqual(0.5f, _presenter.StateForItem(0).Progress, 1e-4f,
                "A custom time source did not drive the item's progress.");
        }

        /// <summary>A user-authored time source wrapping another. Stateless per AD-2.</summary>
        private sealed class ScaledManualTimeSource : IA2BTimeSource
        {
            public IA2BTimeSource Source;
            public float Multiplier = 1f;
            public float DeltaTime => Source.DeltaTime * Multiplier;
        }

        /// <summary>An endpoint that reports an already-projected screen coordinate.</summary>
        private sealed class ScreenEndpoint : IA2BEndpointProvider
        {
            public Vector3 ScreenPoint;
            public ScreenEndpoint(Vector3 screenPoint) => ScreenPoint = screenPoint;
            public A2BEndpointSample Resolve() => A2BEndpointSample.AtScreen(ScreenPoint);
        }

        [Test]
        public void AnEndpointsSpace_SurvivesTheTripToThePresenter()
        {
            // ToWorkingSpace takes the whole A2BEndpointSample rather than a bare Vector3 precisely
            // so the adapter can tell these apart (AD-4): a screen-space endpoint is ALREADY
            // projected, and projecting it a second time puts the item somewhere plausible but wrong.
            // If Core flattened the sample to a Vector3, the adapter could not recover the space.
            var screenDestination = new ScreenEndpoint(new Vector3(640f, 360f, 0f));
            var args = new A2BPlayArgs(_origin, screenDestination, _presenter, seed: Seed);
            A2BEffectHandle handle = _scheduler.Play(A2BTestHarness.Deterministic(1, Duration), in args);
            _scheduler.SetTimeSource(handle, _time);

            A2BTestHarness.Step(_scheduler, _time, _clock, Dt);

            Assert.Greater(_presenter.ToWorkingSpaceCallCount, 0, "The presenter was never asked to convert an endpoint.");
            Assert.AreEqual(A2BEndpointSpace.Screen, _presenter.LastEndpointSpace,
                "A screen-space endpoint reached the presenter marked as World: the adapter would project " +
                "an already-projected coordinate a second time (AD-4).");
        }

        [Test]
        public void AWorldEndpoint_ReachesThePresenterMarkedAsWorld()
        {
            var args = new A2BPlayArgs(_origin, _destination, _presenter, seed: Seed);
            A2BEffectHandle handle = _scheduler.Play(A2BTestHarness.Deterministic(1, Duration), in args);
            _scheduler.SetTimeSource(handle, _time);

            A2BTestHarness.Step(_scheduler, _time, _clock, Dt);

            Assert.AreEqual(A2BEndpointSpace.World, _presenter.LastEndpointSpace);
        }

        [Test]
        public void EndpointsAreConvertedOncePerEffectPerTick_NotOncePerItem()
        {
            // AD-4 again, on the conversion side: 2 conversions per tick, not 2 per item.
            var args = new A2BPlayArgs(_origin, _destination, _presenter, seed: Seed);
            A2BEffectHandle handle = _scheduler.Play(A2BTestHarness.Deterministic(64, Duration), in args);
            _scheduler.SetTimeSource(handle, _time);

            A2BTestHarness.Step(_scheduler, _time, _clock, Dt);

            Assert.AreEqual(2, _presenter.ToWorkingSpaceCallCount,
                "ToWorkingSpace ran " + _presenter.ToWorkingSpaceCallCount +
                " times in one tick for a 64-item burst; it must run once per endpoint (AD-4).");
        }

        [Test]
        public void CustomEndpointProvider_IsResolvedEveryFrame_NeverCachedAtPlay()
        {
            // FR-12: a moving destination must be tracked, so the provider is polled every tick.
            var moving = new ToggleEndpoint(new Vector3(9f, 6f, -1f));
            var args = new A2BPlayArgs(_origin, moving, _presenter, seed: Seed);
            A2BEffectHandle handle = _scheduler.Play(A2BTestHarness.Deterministic(1, Duration), in args);
            _scheduler.SetTimeSource(handle, _time);

            A2BTestHarness.Step(_scheduler, _time, _clock, Dt);
            int afterFirstTick = moving.ResolveCount;
            A2BTestHarness.Step(_scheduler, _time, _clock, Dt);

            Assert.Greater(moving.ResolveCount, afterFirstTick,
                "The endpoint was resolved once and cached; a moving target would be missed (FR-12).");
        }

        [Test]
        public void EndpointsAreResolvedOncePerEffectPerTick_NotOncePerItem()
        {
            // AD-4: the difference between 1 and N Transform reads for a 200-item burst.
            var origin = new ToggleEndpoint(OriginPos);
            var destination = new ToggleEndpoint(DestinationPos);
            var args = new A2BPlayArgs(origin, destination, _presenter, seed: Seed);
            A2BEffectHandle handle = _scheduler.Play(A2BTestHarness.Deterministic(64, Duration), in args);
            _scheduler.SetTimeSource(handle, _time);

            A2BTestHarness.Step(_scheduler, _time, _clock, Dt);

            Assert.AreEqual(1, origin.ResolveCount,
                "The origin was resolved " + origin.ResolveCount + " times in one tick for a 64-item burst (AD-4).");
            Assert.AreEqual(1, destination.ResolveCount,
                "The destination was resolved " + destination.ResolveCount + " times in one tick for a 64-item burst (AD-4).");
            Assert.AreEqual(64, _presenter.AcquireCount, "The burst did not actually spawn 64 items.");
        }

        [Test]
        public void TimeSource_IsReadOncePerTick_EvenWithManyEffectsSharingIt()
        {
            // AD-6: distinct source instances are evaluated at most once per tick and cached.
            var counting = new CountingTimeSource { Inner = _time };

            for (int i = 0; i < 5; i++)
            {
                var args = new A2BPlayArgs(_origin, _destination, new RecordingPresenter(), seed: (uint)(i + 1));
                A2BEffectHandle handle = _scheduler.Play(A2BTestHarness.Deterministic(2, Duration), in args);
                _scheduler.SetTimeSource(handle, counting);
            }

            A2BTestHarness.Step(_scheduler, _time, _clock, Dt);

            Assert.AreEqual(1, counting.Reads,
                "One shared time source was read " + counting.Reads + " times across 5 effects in a single tick (AD-6).");
        }

        private sealed class CountingTimeSource : IA2BTimeSource
        {
            public IA2BTimeSource Inner;
            public int Reads;
            public float DeltaTime { get { Reads++; return Inner.DeltaTime; } }
        }

        [Test]
        public void DistinctTimeSources_EachAdvanceTheirOwnEffect_InTheSameTick()
        {
            // FR-16's motivating case, and why Tick carries no delta (AD-6): a paused-menu effect on
            // an unscaled clock and a gameplay effect on a scaled one advance correctly together.
            var fastClock = new A2BManualTimeSource();
            var slowClock = new A2BManualTimeSource();

            var fastPresenter = new RecordingPresenter();
            var slowPresenter = new RecordingPresenter();

            var fastArgs = new A2BPlayArgs(_origin, _destination, fastPresenter, seed: 1u);
            A2BEffectHandle fast = _scheduler.Play(A2BTestHarness.Deterministic(1, Duration), in fastArgs);
            _scheduler.SetTimeSource(fast, fastClock);

            var slowArgs = new A2BPlayArgs(_origin, _destination, slowPresenter, seed: 2u);
            A2BEffectHandle slow = _scheduler.Play(A2BTestHarness.Deterministic(1, Duration), in slowArgs);
            _scheduler.SetTimeSource(slow, slowClock);

            fastClock.Advance(0.2f);
            slowClock.Advance(0.05f);
            _scheduler.Tick();

            Assert.AreEqual(0.5f, fastPresenter.StateForItem(0).Progress, 1e-4f);
            Assert.AreEqual(0.125f, slowPresenter.StateForItem(0).Progress, 1e-4f);
        }

        [Test]
        public void FrozenTimeSource_HoldsTheEffectInPlace()
        {
            // A paused clock (dt == 0) must park the effect, not advance or complete it.
            var args = new A2BPlayArgs(_origin, _destination, _presenter, seed: Seed);
            A2BEffectHandle handle = _scheduler.Play(A2BTestHarness.Deterministic(1, Duration), in args);
            _scheduler.SetTimeSource(handle, _time);

            A2BTestHarness.Step(_scheduler, _time, _clock, Dt);
            float progressBefore = _presenter.StateForItem(0).Progress;

            _time.Hold();
            for (int i = 0; i < 10; i++) _scheduler.Tick();

            Assert.AreEqual(progressBefore, _presenter.StateForItem(0).Progress, 0f,
                "A held clock still advanced the effect.");
            Assert.IsTrue(handle.IsValid, "A held effect completed while its clock was frozen.");
        }
    }
}
