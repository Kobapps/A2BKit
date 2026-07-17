using System;
using A2BKit.Core;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools.Constraints;

// =====================================================================================================
// THE ALIAS BELOW IS MANDATORY AND LOAD-BEARING (AD-3). DO NOT REMOVE IT.
//
// NUnit.Framework also defines a type called `Is`. Without this alias, NUnit's `Is` shadows Unity's,
// `Is.Not.AllocatingGCMemory()` fails to bind to Unity's constraint, and — in the shapes where it
// still compiles — EVERY TEST IN THIS FILE PASSES WHILE MEASURING NOTHING.
//
// A green allocation suite that asserts nothing is worse than no suite at all: it retires the guard
// that the package's headline requirement (NFR-1 / FR-18) rests on, and does it silently.
//
// A C# alias directive outwits a using-namespace import, so `Is` below is unambiguously Unity's.
// The plain `using UnityEngine.TestTools.Constraints;` is ALSO required: it brings the
// `AllocatingGCMemory()` extension on ConstraintExpression into scope, which is what makes the
// `Is.Not.…` (negated) form resolve at all.
//
// TheAllocationConstraintCanActuallyFail() at the bottom of this file exists to catch a future
// regression here: it asserts that a deliberate allocation IS detected.
// =====================================================================================================
using Is = UnityEngine.TestTools.Constraints.Is;

namespace A2BKit.Tests.PlayMode
{
    /// <summary>
    /// FR-18 / AD-3 — the allocation gate. This is the package's headline claim, so this file's
    /// correctness matters more than its coverage.
    ///
    /// The budget under test, from the architecture spine:
    ///   Per-Frame : ZERO bytes, always.
    ///   Per-Play  : bounded, and CONSTANT with respect to item count (AD-10 — per-item variation is
    ///               computed from (seed, index) on demand, never collected into a list).
    ///
    /// Everything is warmed up before it is measured. JIT, the slot pool, and each EffectSlot's reused
    /// ItemState[] are one-time costs BY DESIGN (AD-3: "collections are pre-sized and reused, never
    /// grown per frame"), so they are paid up front rather than inside the measured region.
    /// </summary>
    [TestFixture]
    internal sealed class A2BAllocationTests
    {
        internal enum PathKind { Linear, Bezier, Procedural }

        private const float Dt = 1f / 60f;
        private const float FlightDuration = 2f;
        private const int SteadyStateItemCount = 32;

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
            _origin = new A2BStaticEndpoint(new Vector3(0f, 0f, 0f));
            _destination = new A2BStaticEndpoint(new Vector3(6f, 3f, -2f));
        }

        // ---- fixture helpers -----------------------------------------------------------------------

        private static A2BEffectDefinition Definition(PathKind path, A2BReleaseMode mode, int count, float scatter = 0.5f)
        {
            A2BEffectBuilder builder = A2BEffectBuilder.Create()
                .Ease(A2BEaseKind.InOutCubic)
                .Duration(FlightDuration)
                .DurationJitter(0.2f)
                .Count(count)
                .Scatter(scatter)
                .AlignToVelocity(true);

            switch (path)
            {
                case PathKind.Linear: builder.Linear(); break;
                case PathKind.Bezier: builder.Arc(2f, 0.5f, 0.25f); break;
                default: builder.Spiral(1.25f, 3f); break;
            }

            switch (mode)
            {
                case A2BReleaseMode.AllAtOnce: builder.AllAtOnce(); break;
                case A2BReleaseMode.SpreadOverDuration: builder.SpreadOver(0.25f); break;
                default: builder.Stagger(0.01f); break;
            }

            return builder.Build();
        }

        private A2BEffectHandle Play(A2BEffectDefinition def, uint seed)
        {
            var args = new A2BPlayArgs(_origin, _destination, _presenter, seed: seed);
            A2BEffectHandle handle = _scheduler.Play(def, in args);
            _scheduler.SetTimeSource(handle, _time);
            return handle;
        }

        private void RunToCompletion(int maxTicks = 2000)
        {
            for (int i = 0; i < maxTicks && _scheduler.ActiveEffectCount > 0; i++)
            {
                _time.Advance(Dt);
                _scheduler.Tick();
            }
        }

        /// <summary>Plays a definition through to completion so JIT and every pool are primed.</summary>
        private void WarmUp(A2BEffectDefinition def, uint seed = 1u)
        {
            Play(def, seed);
            RunToCompletion();
            Assert.AreEqual(0, _scheduler.ActiveEffectCount, "Warm-up effect never completed.");
            Assert.AreEqual(0, _presenter.LiveCount, "Warm-up leaked items.");
            Assert.AreEqual(0, _presenter.ExhaustedCount, "The presenter stub's pool is undersized for this fixture.");
        }

        private void WarmUpPlayCancel(A2BEffectDefinition def)
        {
            Play(def, 1u).Cancel();
            Assert.AreEqual(0, _scheduler.ActiveEffectCount);
        }

        /// <summary>Ticks until every item is airborne but none has arrived — true steady state.</summary>
        private void AdvanceToSteadyState(int ticks = 30)
        {
            for (int i = 0; i < ticks; i++)
            {
                _time.Advance(Dt);
                _scheduler.Tick();
            }
            Assert.Greater(_presenter.LiveCount, 0, "Nothing is in flight; the measured tick would be a no-op.");
            Assert.AreEqual(1, _scheduler.ActiveEffectCount, "The effect is not still running.");
        }

        // ---- Per-Frame: zero bytes -------------------------------------------------------------------

        [Test]
        public void Tick_SteadyState_DoesNotAllocate([Values] PathKind path, [Values] A2BReleaseMode mode)
        {
            A2BEffectDefinition def = Definition(path, mode, SteadyStateItemCount);

            WarmUp(def);
            WarmUp(def, 2u);

            Play(def, 3u);
            AdvanceToSteadyState();

            _time.Advance(Dt);
            Assert.That(() => { _scheduler.Tick(); }, Is.Not.AllocatingGCMemory());
        }

        [Test]
        public void Tick_AcrossTheWholeFlight_DoesNotAllocate([Values] PathKind path)
        {
            // A single-tick measurement cannot see a per-arrival or per-teardown allocation. This one
            // measures an entire flight: play, spawn, travel, arrive, complete, release, slot reuse.
            A2BEffectDefinition def = Definition(path, A2BReleaseMode.FixedStagger, SteadyStateItemCount);
            WarmUp(def);
            WarmUp(def, 2u);

            Assert.That(() =>
            {
                Play(def, 3u);
                RunToCompletion(600);
            }, Is.Not.AllocatingGCMemory());

            Assert.AreEqual(0, _scheduler.ActiveEffectCount, "The measured flight did not complete.");
            Assert.AreEqual(0, _presenter.LiveCount, "The measured flight leaked items.");
            Assert.AreEqual(0, _presenter.DoubleReleaseCount);
        }

        [Test]
        public void Tick_WithManyConcurrentEffects_DoesNotAllocate()
        {
            // FR-3 / NFR-2: concurrency must not reintroduce per-frame cost.
            A2BEffectDefinition def = Definition(PathKind.Bezier, A2BReleaseMode.FixedStagger, 16);

            for (uint seed = 1u; seed <= 8u; seed++) WarmUp(def, seed);

            for (uint seed = 10u; seed < 18u; seed++) Play(def, seed);
            for (int i = 0; i < 30; i++)
            {
                _time.Advance(Dt);
                _scheduler.Tick();
            }
            Assert.AreEqual(8, _scheduler.ActiveEffectCount);
            Assert.Greater(_presenter.LiveCount, 0);

            _time.Advance(Dt);
            Assert.That(() => { _scheduler.Tick(); }, Is.Not.AllocatingGCMemory());
        }

        [Test]
        public void Tick_WithListenersAttached_DoesNotAllocate()
        {
            // AD-11: IA2BEffectListener is THE allocation-free event path — one reusable implementer,
            // dispatched by a plain interface call. No delegate combination, no params array, no boxing.
            A2BEffectDefinition def = Definition(PathKind.Linear, A2BReleaseMode.FixedStagger, SteadyStateItemCount);
            var listener = new CountingListener();

            Play(def, 1u).AddListener(listener);
            RunToCompletion();
            Assert.Greater(listener.TotalCallbacks, 0, "The listener never fired during warm-up.");

            Play(def, 2u).AddListener(listener);
            AdvanceToSteadyState();

            _time.Advance(Dt);
            Assert.That(() => { _scheduler.Tick(); }, Is.Not.AllocatingGCMemory());
        }

        [Test]
        public void Tick_ThatRaisesArrivalsAndCompletion_DoesNotAllocate()
        {
            // The terminal tick does the most work of any tick: N releases, FirstItemArrived,
            // ItemArrived x N, Completed, and ReleaseEffect. If anything allocates per teardown,
            // it is here — and a single steady-state tick would never see it.
            A2BEffectDefinition def = A2BEffectBuilder
                .From(Definition(PathKind.Linear, A2BReleaseMode.AllAtOnce, 24, scatter: 0f))
                .DurationJitter(0f)
                .Build();
            var listener = new CountingListener();

            WarmUp(def);
            WarmUp(def, 2u);

            Play(def, 3u).AddListener(listener);
            // 2 seconds of flight at 1/60 is ~120 ticks; stop short so the terminal tick lands inside
            // the measured region below rather than before it.
            for (int i = 0; i < 110; i++)
            {
                _time.Advance(Dt);
                _scheduler.Tick();
            }
            Assert.AreEqual(1, _scheduler.ActiveEffectCount, "The effect finished early; the terminal tick was missed.");

            listener.Reset();
            Assert.That(() => { RunToCompletion(30); }, Is.Not.AllocatingGCMemory());

            Assert.AreEqual(0, _scheduler.ActiveEffectCount, "The terminal tick fell outside the measured region.");
            Assert.Greater(listener.TotalCallbacks, 24,
                "The measured region did not contain the arrivals and the completion.");
            Assert.AreEqual(0, _presenter.LiveCount);
        }

        [Test]
        public void CancelMidFlight_DoesNotAllocate()
        {
            // AD-9's teardown path, measured. Cancel releases every live item and raises Cancelled.
            A2BEffectDefinition def = Definition(PathKind.Bezier, A2BReleaseMode.FixedStagger, SteadyStateItemCount);
            WarmUp(def);

            A2BEffectHandle warm = Play(def, 2u);
            AdvanceToSteadyState();
            warm.Cancel();

            A2BEffectHandle handle = Play(def, 3u);
            AdvanceToSteadyState();
            int liveBefore = _presenter.LiveCount;
            Assert.Greater(liveBefore, 0);

            Assert.That(() => { handle.Cancel(); }, Is.Not.AllocatingGCMemory());

            Assert.AreEqual(0, _presenter.LiveCount, "The measured cancel leaked items.");
            Assert.AreEqual(0, _presenter.DoubleReleaseCount);
        }

        // ---- Per-Play: bounded, and constant w.r.t. item count -----------------------------------------

        [Test]
        public void Play_CostsTheSame_ForTenItemsAsForTwoHundred()
        {
            // AD-10 is what makes this true: per-item delay and scatter are pure functions of
            // (seed, index), evaluated on demand. The obvious implementation — building a List<float>
            // of delays and a List<Vector3> of offsets at play time — would allocate per play AND
            // scale with item count, so 200 items would cost 20x what 10 items cost.
            //
            // After warm-up the shared answer is ZERO for both, which is strictly stronger than
            // "equal": constant, and constant at nothing.
            A2BEffectDefinition small = Definition(PathKind.Bezier, A2BReleaseMode.FixedStagger, 10);
            A2BEffectDefinition large = Definition(PathKind.Bezier, A2BReleaseMode.FixedStagger, 200);

            Assert.AreEqual(10, small.Emission.ResolveItemCount(7u), "The small definition does not resolve 10 items.");
            Assert.AreEqual(200, large.Emission.ResolveItemCount(7u), "The large definition does not resolve 200 items.");

            // Warm up the LARGE case first: growing the slot's ItemState[] to its high-water mark is
            // amortised setup by design (AD-3), not a per-play cost.
            WarmUpPlayCancel(large);
            WarmUpPlayCancel(small);
            WarmUpPlayCancel(large);

            Assert.That(() => { Play(small, 7u).Cancel(); }, Is.Not.AllocatingGCMemory());
            Assert.That(() => { Play(large, 7u).Cancel(); }, Is.Not.AllocatingGCMemory());
        }

        [Test]
        public void Play_OfAnAlreadySeenShape_DoesNotAllocate([Values] PathKind path, [Values] A2BReleaseMode mode)
        {
            A2BEffectDefinition def = Definition(path, mode, 200);
            WarmUpPlayCancel(def);
            WarmUpPlayCancel(def);

            Assert.That(() => { Play(def, 9u).Cancel(); }, Is.Not.AllocatingGCMemory());
        }

        [Test]
        public void ReplayingIntoAPooledSlot_DoesNotAllocate()
        {
            // FR-17 / AD-9: slot reuse is what keeps a burst-heavy scene at zero steady-state cost.
            // Five full play/complete cycles, measured end to end.
            A2BEffectDefinition def = Definition(PathKind.Linear, A2BReleaseMode.AllAtOnce, 64, scatter: 0f);
            WarmUp(def);
            WarmUp(def, 2u);

            int capacityAfterWarmUp = _scheduler.SlotCapacity;

            Assert.That(() =>
            {
                for (int play = 0; play < 5; play++)
                {
                    Play(def, (uint)(50 + play));
                    RunToCompletion(600);
                }
            }, Is.Not.AllocatingGCMemory());

            Assert.AreEqual(capacityAfterWarmUp, _scheduler.SlotCapacity,
                "Sequential plays grew the slot pool; slots are not being reused.");
            Assert.AreEqual(0, _presenter.LiveCount);
        }

        // ---- the pure-math hot spots ---------------------------------------------------------------------

        [Test]
        public void PathEvaluation_DoesNotAllocate([Values] PathKind path)
        {
            // AD-13: Evaluate is pure — no frame state, no side effects, no allocation.
            IA2BPath evaluator;
            switch (path)
            {
                case PathKind.Linear: evaluator = new A2BLinearPath(); break;
                case PathKind.Bezier: evaluator = new A2BBezierPath(); break;
                default: evaluator = new A2BProceduralPath(); break;
            }

            var ctx = new A2BPathContext(Vector3.zero, new Vector3(5f, 2f, -1f), 3, 8, 12345u);
            for (int i = 0; i < 100; i++) evaluator.Evaluate(in ctx, i / 100f);

            Assert.That(() =>
            {
                for (int i = 0; i <= 200; i++) evaluator.Evaluate(in ctx, i / 200f);
            }, Is.Not.AllocatingGCMemory());
        }

        [Test]
        public void EmissionResolution_DoesNotAllocate()
        {
            // AD-10: nothing is collected per item, so nothing can be allocated per item.
            var emission = new A2BBurstEmission
            {
                MinCount = 200,
                MaxCount = 200,
                ReleaseMode = A2BReleaseMode.FixedStagger,
                StaggerInterval = 0.01f,
                DelayJitter = 0.2f,
                ScatterRadius = 1f
            };
            for (int i = 0; i < 200; i++) { emission.ResolveDelay(1u, i, 200); emission.ResolveScatter(1u, i, 200); }

            Assert.That(() =>
            {
                emission.ResolveItemCount(7u);
                for (int i = 0; i < 200; i++)
                {
                    emission.ResolveDelay(7u, i, 200);
                    emission.ResolveScatter(7u, i, 200);
                }
            }, Is.Not.AllocatingGCMemory());
        }

        [Test]
        public void EasingEvaluation_DoesNotAllocate()
        {
            var kinds = (A2BEaseKind[])Enum.GetValues(typeof(A2BEaseKind));
            for (int k = 0; k < kinds.Length; k++)
                for (int i = 0; i < 20; i++)
                    A2BEase.Evaluate(kinds[k], i / 20f);

            Assert.That(() =>
            {
                for (int k = 0; k < kinds.Length; k++)
                    for (int i = 0; i <= 100; i++)
                        A2BEase.Evaluate(kinds[k], i / 100f);
            }, Is.Not.AllocatingGCMemory());
        }

        [Test]
        public void RandomDraws_DoNotAllocate()
        {
            var warm = new A2BRandom(1u);
            for (int i = 0; i < 100; i++) { warm.NextFloat(); warm.NextUnitSphere(); A2BRandom.DeriveSeed(1u, i); }

            Assert.That(() =>
            {
                // A struct RNG on the stack: constructing one per call must cost nothing.
                var local = new A2BRandom(9u);
                for (int i = 0; i < 500; i++)
                {
                    local.NextUInt();
                    local.NextFloat();
                    local.NextFloat(-1f, 1f);
                    local.NextInt(0, 10);
                    local.NextUnitSphere();
                    A2BRandom.DeriveSeed(9u, i);
                }
            }, Is.Not.AllocatingGCMemory());
        }

        [Test]
        public void HandleOperations_DoNotAllocate()
        {
            // AD-7: the stamp check happens on EVERY handle access, including from the tick path's
            // event dispatch. It must cost nothing.
            A2BEffectDefinition def = Definition(PathKind.Linear, A2BReleaseMode.AllAtOnce, 8, scatter: 0f);
            WarmUp(def);
            A2BEffectHandle handle = Play(def, 2u);
            AdvanceToSteadyState(5);

            bool valid = handle.IsValid;
            int items = handle.ItemCount;
            Assert.IsTrue(valid);
            Assert.AreEqual(8, items);

            Assert.That(() =>
            {
                for (int i = 0; i < 200; i++)
                {
                    bool v = handle.IsValid;
                    int c = handle.ItemCount + handle.ArrivedCount;
                    if (v && c < 0) throw new InvalidOperationException();
                }
            }, Is.Not.AllocatingGCMemory());
        }

        // ---- the guard on the guard -----------------------------------------------------------------------

        [Test]
        public void TheAllocationConstraintCanActuallyFail()
        {
            // If the `using Is = UnityEngine.TestTools.Constraints.Is;` alias at the top of this file
            // were ever removed or shadowed, every test above would pass while measuring nothing.
            // This asserts the constraint is genuinely live: a deliberate allocation MUST be detected.
            Assert.That(() =>
            {
                object boxed = new object();
                Assert.NotNull(boxed);
            }, Is.AllocatingGCMemory(),
                "Is.AllocatingGCMemory() did not detect a deliberate heap allocation. The Unity `Is` " +
                "alias at the top of this file is missing or shadowed by NUnit's `Is`, which means " +
                "every allocation test in this file is measuring nothing (AD-3).");
        }

        /// <summary>A reusable listener — AD-11's allocation-free event path, used as intended.</summary>
        private sealed class CountingListener : A2BEffectListenerBase
        {
            public int TotalCallbacks;

            public override void OnStarted(in A2BEffectHandle handle) => TotalCallbacks++;
            public override void OnItemSpawned(in A2BEffectHandle handle, int itemIndex) => TotalCallbacks++;
            public override void OnFirstItemArrived(in A2BEffectHandle handle, int itemIndex) => TotalCallbacks++;
            public override void OnItemArrived(in A2BEffectHandle handle, int itemIndex) => TotalCallbacks++;
            public override void OnCompleted(in A2BEffectHandle handle) => TotalCallbacks++;
            public override void OnCancelled(in A2BEffectHandle handle, A2BCompletionReason reason) => TotalCallbacks++;

            public void Reset() => TotalCallbacks = 0;
        }
    }
}
