using System.Collections.Generic;
using A2BKit.Core;
using A2BKit.Unity;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace A2BKit.Tests.PlayMode
{
    /// <summary>
    /// Playing a new effect while one is already running must not touch the running one.
    ///
    /// This is the exact worry behind "spamming a popup moves or stops the one already playing": an
    /// A2BEffectPlayer keeps ONE presenter and reuses it, so every Play on it shares a single item
    /// pool and a single id table. If a second effect could be handed an item id (and therefore a
    /// pooled Transform) that the first effect is still flying, the second play would visibly yank or
    /// blank the first. These tests hold that line at the observable level: distinct Transforms,
    /// undisturbed positions, correct independent completion.
    /// </summary>
    [TestFixture]
    public sealed class A2BConcurrentEffectsTests
    {
        private A2BScheduler _scheduler;
        private A2BManualTimeSource _time;
        private GameObject _root;
        private TrackingRenderer _renderer;
        private A2BPresenter _presenter;

        [SetUp]
        public void SetUp()
        {
            _scheduler = new A2BScheduler();
            _time = new A2BManualTimeSource();
            _root = new GameObject("concurrent-root");
            _renderer = new TrackingRenderer();
            // One presenter, shared by every Play — exactly what A2BEffectPlayer does.
            _presenter = new A2BPresenter(new A2BWorld3DAdapter(_root.transform), _renderer);
        }

        [TearDown]
        public void TearDown()
        {
            _scheduler.CancelAll();
            _presenter?.Dispose();
            if (_root != null) Object.DestroyImmediate(_root);
        }

        private A2BEffectDefinition Def(int count = 1, float duration = 10f) =>
            A2BEffectBuilder.Create()
                .Count(count).AllAtOnce().Scatter(0f)
                .Linear().Ease(A2BEaseKind.Linear)
                .Duration(duration).DurationJitter(0f)
                .Build();

        private A2BEffectHandle Play(Vector3 origin, Vector3 destination, uint seed, int count = 1, float duration = 10f)
        {
            var args = new A2BPlayArgs(
                new A2BStaticEndpoint(origin), new A2BStaticEndpoint(destination), _presenter, seed: seed);
            A2BEffectHandle h = _scheduler.Play(Def(count, duration), in args);
            _scheduler.SetTimeSource(h, _time);
            return h;
        }

        private void Step(float dt, int frames)
        {
            for (int i = 0; i < frames; i++) { _time.Advance(dt); _scheduler.Tick(); }
        }

        [Test]
        public void A_second_effect_does_not_reuse_the_first_effects_live_item()
        {
            // A flies straight up from the origin; B flies up from 1000 units to the side. Wildly
            // different paths, so if B ever grabbed A's Transform the position jump would be obvious.
            Play(Vector3.zero, new Vector3(0f, 100f, 0f), seed: 1u);
            Step(0.1f, 5);

            Assert.AreEqual(1, _renderer.Live.Count, "Exactly one item should be flying so far.");
            Transform aItem = First(_renderer.Live);
            Vector3 aBefore = aItem.localPosition;
            Assert.Less(Mathf.Abs(aBefore.x), 0.01f, "A flies straight up; its x should be ~0.");

            // Start B while A is mid-flight.
            Play(new Vector3(1000f, 0f, 0f), new Vector3(1000f, 100f, 0f), seed: 2u);
            Step(0.1f, 1);

            Assert.AreEqual(2, _renderer.Live.Count, "Both effects should now have a live item.");
            Assert.IsTrue(_renderer.Live.Contains(aItem), "A's item must still be alive — B must not have recycled it.");

            // A must not have jumped onto B's path.
            Assert.Less(Mathf.Abs(aItem.localPosition.x), 0.01f,
                "A's item jumped sideways when B played — it was aliased onto B's path.");
            Assert.Greater(aItem.localPosition.y, aBefore.y,
                "A's item stopped advancing when B played.");

            Transform bItem = Other(_renderer.Live, aItem);
            Assert.AreNotSame(aItem, bItem, "A and B are flying the SAME Transform — a pool aliasing bug.");
            Assert.Greater(bItem.localPosition.x, 900f, "B's item is not on B's own path.");
        }

        [Test]
        public void Concurrent_effects_each_complete_with_their_own_item_count()
        {
            var la = new Counting();
            var lb = new Counting();

            A2BEffectHandle a = Play(Vector3.zero, new Vector3(0f, 10f, 0f), seed: 1u, count: 4, duration: 0.4f);
            a.AddListener(la);
            Step(0.1f, 1);   // A partway

            A2BEffectHandle b = Play(new Vector3(50f, 0f, 0f), new Vector3(50f, 10f, 0f), seed: 2u, count: 7, duration: 0.4f);
            b.AddListener(lb);

            Step(0.1f, 8);   // both finish

            Assert.AreEqual(4, la.Arrived, "A should land exactly its 4 items.");
            Assert.AreEqual(7, lb.Arrived, "B should land exactly its 7 items.");
            Assert.AreEqual(1, la.Completed);
            Assert.AreEqual(1, lb.Completed);
            Assert.AreEqual(0, _renderer.Live.Count, "Every item from both effects is back in the pool (AD-9).");
            Assert.AreEqual(0, _renderer.DoubleReleased, "An item was released twice — the free-list would hand it out to two effects.");
        }

        [Test]
        public void Rapid_replays_never_leave_two_items_sharing_a_transform()
        {
            // The literal "spamming" case: fire many short effects, some overlapping, and assert that
            // at no observed frame do two live items share a Transform.
            for (int click = 0; click < 30; click++)
            {
                Play(new Vector3(click, 0f, 0f), new Vector3(click, 20f, 0f), seed: (uint)(click + 1), count: 1, duration: 0.25f);

                _time.Advance(0.05f);
                _scheduler.Tick();

                // The live set is a HashSet of Transforms; if two live items ever aliased, the count
                // would drop below the scheduler's in-flight count.
                Assert.AreEqual(_scheduler.ActiveItemCount, _renderer.Live.Count,
                    "Live Transform count diverged from in-flight item count at click " + click +
                    " — two items are sharing a Transform.");
            }

            Assert.AreEqual(0, _renderer.DoubleReleased);
        }

        // ---- helpers ---------------------------------------------------------------------------

        private static Transform First(HashSet<Transform> set)
        {
            foreach (Transform t in set) return t;
            return null;
        }

        private static Transform Other(HashSet<Transform> set, Transform not)
        {
            foreach (Transform t in set) if (t != not) return t;
            return null;
        }

        private sealed class Counting : A2BEffectListenerBase
        {
            public int Arrived;
            public int Completed;
            public override void OnItemArrived(in A2BEffectHandle h, int i) => Arrived++;
            public override void OnCompleted(in A2BEffectHandle h) => Completed++;
        }

        /// <summary>
        /// A payload renderer that hands out a distinct plain Transform per item and tracks the live
        /// set, so a test can catch two live items ever pointing at the same Transform. Returns itself
        /// from CreateRuntimeInstance so the test keeps a handle on the tracked state.
        /// </summary>
        private sealed class TrackingRenderer : IA2BPayloadRenderer
        {
            public readonly HashSet<Transform> Live = new HashSet<Transform>();
            public int DoubleReleased;

            private Transform _root;
            private int _n;

            public string PayloadKey => "tracking";
            public IA2BPayloadRenderer CreateRuntimeInstance() => this;
            public void Initialize(Transform root, int prewarmCount) => _root = root;

            public Transform Acquire(in A2BItemSpawnInfo info)
            {
                var go = new GameObject("item-" + _n++);
                go.transform.SetParent(_root, false);
                Live.Add(go.transform);
                return go.transform;
            }

            public void UpdateItem(Transform item, in A2BVisualState state) { }

            public void Release(Transform item)
            {
                if (!Live.Remove(item)) DoubleReleased++;
            }

            public void Dispose()
            {
                foreach (Transform t in Live) if (t != null) Object.DestroyImmediate(t.gameObject);
                Live.Clear();
            }

            public void GetPoolStats(out int active, out int available)
            {
                active = Live.Count;
                available = 0;
            }
        }
    }
}
