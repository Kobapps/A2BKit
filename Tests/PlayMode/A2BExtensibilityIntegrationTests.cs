using System.Collections.Generic;
using A2BKit.Core;
using A2BKit.Unity;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace A2BKit.Tests.PlayMode
{
    /// <summary>
    /// FR-6 through the PUBLIC entry points, which is the only place it counts.
    ///
    /// The interface always existed, but `A2B` and `A2BEffectPlayer` each hard-coded
    /// `switch (space) { Canvas: … World2D: … default: … }` — so a custom space adapter could be
    /// written and never reached: you had to hand-build an A2BPresenter or edit a shipped file. The
    /// old EditMode extensibility tests passed anyway, because they constructed the presenter
    /// directly and so never touched the switch. These go through the front door on purpose.
    /// </summary>
    [TestFixture]
    public sealed class A2BExtensibilityIntegrationTests
    {
        private readonly List<GameObject> _spawned = new List<GameObject>();

        [TearDown]
        public void TearDown()
        {
            LogAssert.ignoreFailingMessages = false;
            A2BAdapters.ResetFactories();   // global state must not leak into the next test
            A2B.CancelAll();
            foreach (GameObject go in _spawned) if (go != null) Object.DestroyImmediate(go);
            _spawned.Clear();
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

        private Transform NewTransform(string name, Vector3 pos)
        {
            var go = new GameObject(name);
            go.transform.position = pos;
            _spawned.Add(go);
            return go.transform;
        }

        private A2BEffectSpec Spec(A2BSpaceKind space, IA2BSpaceAdapterFactory over = null)
        {
            var root = new GameObject("payload-root");
            _spawned.Add(root);

            return new A2BEffectSpec
            {
                Space = space,
                Payload = new A2BSpritePayloadRenderer { Sprite = MakeSprite() },
                Definition = A2BEffectBuilder.Create().Count(2).Duration(0.3f).Build(),
                SpaceOverride = over,
            };
        }

        [Test]
        public void A_custom_space_adapter_reaches_the_A2B_facade_via_the_asset_override()
        {
            var factory = new SpyFactory();
            A2BEffectSpec spec = Spec(A2BSpaceKind.World3D, factory);

            A2B.Play(spec, NewTransform("from", Vector3.zero), NewTransform("to", Vector3.up * 3f));

            Assert.AreEqual(1, factory.CreateCalls,
                "The per-spec SpaceOverride was ignored — a custom adapter cannot be reached through A2B.Play.");

            // Conversion happens in the tick, not in Play, so the adapter is only exercised once the
            // runner advances the effect.
            A2BRunner.Scheduler.Tick();

            Assert.IsTrue(factory.Created.Used,
                "The custom adapter was built but never asked to convert anything — it is not really wired in.");
        }

        [Test]
        public void A_globally_registered_factory_overrides_the_built_in_adapter()
        {
            var factory = new SpyFactory();
            A2BAdapters.SetFactory(A2BSpaceKind.World3D, factory);

            A2B.Play(Spec(A2BSpaceKind.World3D), NewTransform("from", Vector3.zero), NewTransform("to", Vector3.up));

            Assert.AreEqual(1, factory.CreateCalls,
                "A2BAdapters.SetFactory did not take effect through the facade.");
        }

        [Test]
        public void Clearing_a_global_factory_restores_the_built_in()
        {
            var factory = new SpyFactory();
            A2BAdapters.SetFactory(A2BSpaceKind.World3D, factory);
            Assert.IsTrue(A2BAdapters.HasFactory(A2BSpaceKind.World3D));

            A2BAdapters.SetFactory(A2BSpaceKind.World3D, null);

            Assert.IsFalse(A2BAdapters.HasFactory(A2BSpaceKind.World3D));
            A2B.Play(Spec(A2BSpaceKind.World3D), NewTransform("from", Vector3.zero), NewTransform("to", Vector3.up));
            Assert.AreEqual(0, factory.CreateCalls, "The cleared factory was still used.");
        }

        [Test]
        public void A_factory_that_throws_falls_back_to_the_built_in_rather_than_killing_the_effect()
        {
            // AD-8: a broken extension degrades the effect. It must not take the frame down with it.
            // The exception below is the POINT of the test, so it is expected rather than a failure.
            LogAssert.ignoreFailingMessages = true;
            A2BAdapters.SetFactory(A2BSpaceKind.World3D, new ThrowingFactory());

            A2BEffectHandle handle = default;
            Assert.DoesNotThrow(() =>
                handle = A2B.Play(Spec(A2BSpaceKind.World3D), NewTransform("from", Vector3.zero), NewTransform("to", Vector3.up)));

            Assert.IsTrue(handle.IsValid,
                "A throwing adapter factory must fall back to the built-in adapter, not invalidate the effect.");
        }

        [Test]
        public void A_factory_returning_null_falls_back_to_the_built_in()
        {
            A2BAdapters.SetFactory(A2BSpaceKind.World3D, new NullFactory());

            A2BEffectHandle handle = A2B.Play(
                Spec(A2BSpaceKind.World3D), NewTransform("from", Vector3.zero), NewTransform("to", Vector3.up));

            Assert.IsTrue(handle.IsValid, "A null-returning factory must fall back to the built-in adapter.");
        }

        [Test]
        public void A_code_built_spec_plays_through_the_same_one_liner_as_an_asset()
        {
            // FR-2: the fluent builder is a PEER of the asset, not a second-class path.
            A2BEffectSpec spec = A2BEffectBuilder.Create()
                .Arc(1.5f).Count(4).Duration(0.3f)
                .AsSpec(new A2BSpritePayloadRenderer { Sprite = MakeSprite() }, A2BSpaceKind.World3D);

            A2BEffectHandle handle = A2B.Play(spec, NewTransform("from", Vector3.zero), NewTransform("to", Vector3.up * 2f));

            Assert.IsTrue(handle.IsValid, "A code-built spec must play through A2B.Play like an asset does.");
            Assert.AreEqual(4, handle.ItemCount);
        }

        // ---- doubles --------------------------------------------------------------------------

        private sealed class SpyAdapter : IA2BSpaceAdapter
        {
            private readonly Transform _root;
            public bool Used;

            public SpyAdapter(Transform root) => _root = root;

            public A2BSpaceKind Space => A2BSpaceKind.World3D;
            public Transform Root => _root;

            public Vector3 ToWorkingSpace(in A2BEndpointSample sample) { Used = true; return sample.Position; }
            public Vector3 ScaleScatter(in Vector3 unitOffset, float radius) => unitOffset * radius;
            public void ApplyToTransform(Transform target, in A2BVisualState state) => target.localPosition = state.Position;
        }

        private sealed class SpyFactory : IA2BSpaceAdapterFactory
        {
            public int CreateCalls;
            public SpyAdapter Created;

            public IA2BSpaceAdapter Create(in A2BSpaceContext context)
            {
                CreateCalls++;
                var go = new GameObject("spy-root");
                Created = new SpyAdapter(go.transform);
                return Created;
            }
        }

        private sealed class ThrowingFactory : IA2BSpaceAdapterFactory
        {
            public IA2BSpaceAdapter Create(in A2BSpaceContext context)
                => throw new System.InvalidOperationException("deliberate failure from a user factory");
        }

        private sealed class NullFactory : IA2BSpaceAdapterFactory
        {
            public IA2BSpaceAdapter Create(in A2BSpaceContext context) => null;
        }
    }
}
