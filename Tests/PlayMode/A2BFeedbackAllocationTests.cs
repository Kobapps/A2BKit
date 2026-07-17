using A2BKit.Core;
using A2BKit.Unity;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools.Constraints;

// BOTH lines below are load-bearing (AD-3) — see the header of A2BAllocationTests.cs for the full story.
//
// The alias makes `Is` unambiguously Unity's rather than NUnit's, without which the constraint
// silently measures nothing. The plain namespace import above is ALSO required: it brings the
// `AllocatingGCMemory()` extension on ConstraintExpression into scope, which is what lets the
// negated `Is.Not.…` form resolve at all. Dropping it does not fail quietly — it fails to compile,
// which is how this file was caught missing it.
using Is = UnityEngine.TestTools.Constraints.Is;

namespace A2BKit.Tests.PlayMode
{
    /// <summary>
    /// Feedbacks are the newest code in the per-frame path, so they are the newest way to break the
    /// package's headline promise.
    ///
    /// <c>IA2BFeedback.OnItemUpdated</c> runs for every item every frame, right next to the payload
    /// and the adapter. A feedback that allocates there costs exactly as much as a scheduler that
    /// allocates there — and it is easier to write by accident, because "it's just a trail" does not
    /// feel like hot code.
    /// </summary>
    [TestFixture]
    public sealed class A2BFeedbackAllocationTests
    {
        private A2BScheduler _scheduler;
        private A2BManualTimeSource _time;
        private GameObject _root;

        [SetUp]
        public void SetUp()
        {
            _scheduler = new A2BScheduler();
            _time = new A2BManualTimeSource();
            _root = new GameObject("feedback-alloc-root");
        }

        [TearDown]
        public void TearDown()
        {
            _scheduler.CancelAll();
            if (_root != null) Object.DestroyImmediate(_root);
        }


        /// <summary>
        /// A real 1x1 sprite. Not cosmetic: a null Sprite makes the payload log an actionable error
        /// (correctly — it would spawn invisible items), and NUnit fails a test on any unhandled
        /// error log. Passing a real sprite tests the happy path instead of the diagnostic path.
        /// </summary>
        private static Sprite MakeSprite()
        {
            var tex = new Texture2D(1, 1);
            tex.SetPixel(0, 0, Color.white);
            tex.Apply();
            return Sprite.Create(tex, new Rect(0, 0, 1, 1), new Vector2(0.5f, 0.5f), 100f);
        }

        private A2BPresenter BuildPresenter(params IA2BFeedback[] feedbacks)
        {
            var adapter = new A2BWorld3DAdapter(_root.transform);
            var payload = new A2BSpritePayloadRenderer { Sprite = MakeSprite() };
            return new A2BPresenter(adapter, payload, feedbacks, prewarmCount: 32);
        }

        private A2BEffectHandle Play(A2BPresenter presenter, int count = 12)
        {
            A2BEffectDefinition def = A2BEffectBuilder.Create()
                .Count(count).Duration(5f).DurationJitter(0f)
                .AllAtOnce().Scatter(0f).Linear().Ease(A2BEaseKind.Linear)
                .Build();

            var args = new A2BPlayArgs(
                new A2BStaticEndpoint(Vector3.zero),
                new A2BStaticEndpoint(Vector3.up * 10f),
                presenter, seed: 4242u);

            A2BEffectHandle handle = _scheduler.Play(def, in args);
            _scheduler.SetTimeSource(handle, _time);
            return handle;
        }

        /// <summary>Warm every pool and lazy path, so the measured frames are steady state.</summary>
        private void Warmup(int frames = 8)
        {
            for (int i = 0; i < frames; i++) { _time.Advance(1f / 60f); _scheduler.Tick(); }
        }

        [Test]
        public void Tick_WithNoFeedbacks_DoesNotAllocate()
        {
            // The control. If this fails, the failure is not about feedback.
            A2BPresenter presenter = BuildPresenter();
            Play(presenter);
            Warmup();

            Assert.That(() =>
            {
                _time.Advance(1f / 60f);
                _scheduler.Tick();
            }, Is.Not.AllocatingGCMemory());
        }

        [Test]
        public void Tick_WithATrailFeedback_DoesNotAllocate()
        {
            A2BPresenter presenter = BuildPresenter(new A2BTrailFeedback());
            Play(presenter);
            Warmup();

            Assert.That(() =>
            {
                _time.Advance(1f / 60f);
                _scheduler.Tick();
            }, Is.Not.AllocatingGCMemory());
        }

        [Test]
        public void Tick_WithASpawnPopFeedback_DoesNotAllocate()
        {
            // SpawnPop drives a colour flash from state.Progress on EVERY frame — the busiest of the
            // built-in feedbacks, and the one most likely to smuggle in a per-frame allocation.
            A2BPresenter presenter = BuildPresenter(new A2BSpawnPopFeedback());
            Play(presenter);
            Warmup();

            Assert.That(() =>
            {
                _time.Advance(1f / 60f);
                _scheduler.Tick();
            }, Is.Not.AllocatingGCMemory());
        }

        [Test]
        public void Tick_WithStackedFeedbacks_DoesNotAllocate()
        {
            // The impact gets a real sprite: bare, it correctly logs "neither a Prefab nor a Sprite",
            // and NUnit fails on any unhandled error log. Configuring it properly also means this
            // measures the real spawn path rather than an early-out.
            A2BPresenter presenter = BuildPresenter(
                new A2BTrailFeedback(),
                new A2BSpawnPopFeedback(),
                new A2BImpactFeedback { Sprite = MakeSprite() });

            Play(presenter);
            Warmup();

            // Stacking is the documented usage, so stacking is what must hold the line — the loop over
            // feedbacks is an indexed array walk precisely so this does not box an enumerator.
            Assert.That(() =>
            {
                _time.Advance(1f / 60f);
                _scheduler.Tick();
            }, Is.Not.AllocatingGCMemory());
        }

        [Test]
        public void A_disabled_feedback_costs_nothing_per_frame()
        {
            A2BPresenter presenter = BuildPresenter(
                new A2BTrailFeedback { Enabled = false },
                new A2BSpawnPopFeedback { Enabled = false });

            Play(presenter);
            Warmup();

            Assert.That(() =>
            {
                _time.Advance(1f / 60f);
                _scheduler.Tick();
            }, Is.Not.AllocatingGCMemory());
        }
    }
}
