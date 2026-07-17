using A2BKit.Core;
using NUnit.Framework;
using UnityEngine;

namespace A2BKit.Tests.EditMode
{
    /// <summary>
    /// FR-2 — code-first authoring is a PEER of the asset surface, not a wrapper on it, and
    /// <c>From(def)</c> deep-copies so that overriding a shared asset in code never mutates the
    /// asset. A definition is read by many concurrent effects (FR-3); a builder that aliased its
    /// source would let one caller's tweak reach every other caller's effect.
    /// </summary>
    [TestFixture]
    internal sealed class A2BEffectBuilderTests
    {
        private static A2BEffectDefinition Source()
            => A2BEffectBuilder.Create()
                .Linear()
                .Ease(A2BEaseKind.InQuad)
                .Duration(1f)
                .DurationJitter(0.1f)
                .Count(4)
                .Scatter(0.25f)
                .AlignToVelocity(false)
                .UseUnscaledTime(false)
                .OnEndpointLost(A2BEndpointLostPolicy.Cancel)
                .Prewarm(8)
                .ScaleOverProgress(AnimationCurve.Linear(0f, 1f, 1f, 1f))
                .Build();

        // ---- the clone must not write back --------------------------------------------------------

        [Test]
        public void From_ClonesScalarSettings_SoMutatingTheCloneLeavesTheSourceAlone()
        {
            A2BEffectDefinition source = Source();

            A2BEffectDefinition clone = A2BEffectBuilder.From(source)
                .Duration(9f)
                .DurationJitter(0.9f)
                .AlignToVelocity(true)
                .UseUnscaledTime(true)
                .OnEndpointLost(A2BEndpointLostPolicy.UseLastKnownPosition)
                .Prewarm(64)
                .Build();

            Assert.AreNotSame(source, clone, "From returned the source definition itself.");

            Assert.AreEqual(9f, clone.Duration, 1e-5f);
            Assert.AreEqual(1f, source.Duration, 1e-5f, "The clone's Duration wrote back to the source (FR-2).");

            Assert.AreEqual(0.9f, clone.DurationJitter, 1e-5f);
            Assert.AreEqual(0.1f, source.DurationJitter, 1e-5f, "The clone's DurationJitter wrote back to the source.");

            Assert.IsTrue(clone.AlignToVelocity);
            Assert.IsFalse(source.AlignToVelocity, "The clone's AlignToVelocity wrote back to the source.");

            Assert.IsTrue(clone.UseUnscaledTime);
            Assert.IsFalse(source.UseUnscaledTime, "The clone's UseUnscaledTime wrote back to the source.");

            Assert.AreEqual(A2BEndpointLostPolicy.UseLastKnownPosition, clone.EndpointLostPolicy);
            Assert.AreEqual(A2BEndpointLostPolicy.Cancel, source.EndpointLostPolicy,
                "The clone's EndpointLostPolicy wrote back to the source.");

            Assert.AreEqual(64, clone.PrewarmCount);
            Assert.AreEqual(8, source.PrewarmCount, "The clone's PrewarmCount wrote back to the source.");
        }

        [Test]
        public void From_ReplacingAStrategyOnTheClone_LeavesTheSourcePointingAtItsOwn()
        {
            A2BEffectDefinition source = Source();
            IA2BPath sourcePath = source.Path;
            IA2BEasing sourceEasing = source.Easing;
            IA2BEmission sourceEmission = source.Emission;

            A2BEffectDefinition clone = A2BEffectBuilder.From(source)
                .Arc(5f)
                .Ease(A2BEaseKind.OutBounce)
                .Emission(new A2BBurstEmission { MinCount = 99, MaxCount = 99 })
                .Build();

            Assert.IsInstanceOf<A2BBezierPath>(clone.Path);
            Assert.AreSame(sourcePath, source.Path, "Replacing the clone's Path replaced the source's Path (FR-2).");
            Assert.IsInstanceOf<A2BLinearPath>(source.Path);

            Assert.AreSame(sourceEasing, source.Easing, "Replacing the clone's Easing replaced the source's Easing.");
            Assert.AreEqual(A2BEaseKind.InQuad, ((A2BStandardEasing)source.Easing).Kind);

            Assert.AreSame(sourceEmission, source.Emission,
                "Replacing the clone's Emission replaced the source's Emission.");
            Assert.AreEqual(4, source.Emission.ResolveItemCount(1u));
            Assert.AreEqual(99, clone.Emission.ResolveItemCount(1u));
        }

        [Test]
        public void Clone_DeepCopiesTheScaleCurve()
        {
            A2BEffectDefinition source = Source();
            A2BEffectDefinition clone = source.Clone();

            Assert.AreNotSame(source.ScaleOverProgress, clone.ScaleOverProgress,
                "The clone shares the source's AnimationCurve instance; editing one edits both.");

            clone.ScaleOverProgress.AddKey(0.5f, 5f);
            Assert.AreEqual(2, source.ScaleOverProgress.length,
                "Adding a key to the clone's curve mutated the source's curve.");
        }

        [Test]
        public void Clone_DeepCopiesTheColorGradient()
        {
            A2BEffectDefinition source = Source();
            A2BEffectDefinition clone = source.Clone();

            Assert.AreNotSame(source.ColorOverProgress, clone.ColorOverProgress,
                "The clone shares the source's Gradient instance; editing one edits both.");

            clone.ColorOverProgress.SetKeys(
                new[] { new GradientColorKey(Color.red, 0f), new GradientColorKey(Color.red, 1f) },
                new[] { new GradientAlphaKey(0f, 0f), new GradientAlphaKey(0f, 1f) });

            Color sampled = source.ColorOverProgress.Evaluate(0.5f);
            Assert.AreEqual(1f, sampled.a, 1e-3f, "Re-keying the clone's gradient zeroed the source's alpha.");
            Assert.AreEqual(1f, sampled.b, 1e-3f, "Re-keying the clone's gradient recoloured the source.");
        }

        [Test]
        public void Clone_WithNullCurveAndGradient_DoesNotThrow()
        {
            // AD-8: a definition mid-edit in the inspector must still clone.
            var source = new A2BEffectDefinition { ScaleOverProgress = null, ColorOverProgress = null };

            A2BEffectDefinition clone = null;
            Assert.DoesNotThrow(() => clone = source.Clone());

            // Null in, null out — and this assertion is the point of the test, not a detail.
            //
            // Clone() used to fabricate `new AnimationCurve(Array.Empty<Keyframe>())` here so the
            // field would be non-null. But an EMPTY AnimationCurve evaluates to 0 at every t, and the
            // scheduler multiplies item scale by it — so cloning a definition whose curve was null
            // produced an effect scaled to zero: every item invisible, no error, nothing to debug.
            // Null is the honest representation of "no curve"; the scheduler already reads it as
            // `?? 1f`, which is the sane default the fabricated curve was pretending to be.
            Assert.IsNull(clone.ScaleOverProgress, "A null scale curve must stay null — an empty curve evaluates to 0 and hides every item.");
            Assert.IsNull(clone.ColorOverProgress, "A null gradient must stay null rather than becoming a fabricated one.");
        }

        [Test]
        public void Clone_WithRealCurves_CopiesThemRatherThanSharing()
        {
            var source = new A2BEffectDefinition
            {
                ScaleOverProgress = AnimationCurve.Constant(0f, 1f, 0.5f),
            };

            A2BEffectDefinition clone = source.Clone();

            Assert.IsNotNull(clone.ScaleOverProgress);
            Assert.AreNotSame(source.ScaleOverProgress, clone.ScaleOverProgress,
                "Curves are mutable reference types; sharing one would let an override edit the source asset (FR-2).");
            Assert.AreEqual(0.5f, clone.ScaleOverProgress.Evaluate(0.5f), 1e-4f,
                "The copy must carry the source's keys, not an empty curve.");
        }

        [Test]
        public void Clone_SharesStrategyInstances_ByDesign()
        {
            // AD-2: strategies are stateless and shared across effects and items, so Clone
            // deliberately does NOT deep-copy them. This is documented behaviour, and it is only safe
            // because AD-2 also forbids strategies from holding mutable instance state.
            A2BEffectDefinition source = Source();
            A2BEffectDefinition clone = source.Clone();

            Assert.AreSame(source.Path, clone.Path);
            Assert.AreSame(source.Easing, clone.Easing);
            Assert.AreSame(source.Emission, clone.Emission);
        }

        [Test]
        public void From_Null_YieldsDefaultsRatherThanThrowing()
        {
            A2BEffectDefinition def = null;
            Assert.DoesNotThrow(() => def = A2BEffectBuilder.From(null).Build());
            Assert.IsNotNull(def);
            Assert.IsTrue(def.Validate(out _), "From(null) produced a definition that does not validate.");
        }

        // ---- the fluent surface itself -------------------------------------------------------------

        [Test]
        public void Create_ProducesAValidDefinition()
        {
            A2BEffectDefinition def = A2BEffectBuilder.Create().Build();
            Assert.IsTrue(def.Validate(out string error), error);
        }

        [Test]
        public void Builder_ChainReturnsTheSameInstance_SoOrderOfCallsIsIrrelevant()
        {
            A2BEffectBuilder builder = A2BEffectBuilder.Create();
            Assert.AreSame(builder, builder.Duration(1f));
            Assert.AreSame(builder, builder.Linear());
            Assert.AreSame(builder, builder.Count(3));
        }

        [Test]
        public void Linear_Arc_And_Spiral_SelectTheMatchingPath()
        {
            Assert.IsInstanceOf<A2BLinearPath>(A2BEffectBuilder.Create().Linear().Build().Path);
            Assert.IsInstanceOf<A2BBezierPath>(A2BEffectBuilder.Create().Arc(3f).Build().Path);
            Assert.IsInstanceOf<A2BProceduralPath>(A2BEffectBuilder.Create().Spiral(1f).Build().Path);
        }

        [Test]
        public void Arc_ForwardsItsDesignerFacingParameters()
        {
            // FR-9: "arc 2 units up", never "control point at (x,y,z)".
            var path = (A2BBezierPath)A2BEffectBuilder.Create().Arc(2.5f, 0.3f, 0.4f).Build().Path;
            Assert.AreEqual(2.5f, path.ArcHeight, 1e-5f);
            Assert.AreEqual(0.3f, path.ArcBias, 1e-5f);
            Assert.AreEqual(0.4f, path.ArcJitter, 1e-5f);
        }

        [Test]
        public void Spiral_ForwardsItsParameters()
        {
            var path = (A2BProceduralPath)A2BEffectBuilder.Create().Spiral(3f, 7f).Build().Path;
            Assert.AreEqual(3f, path.Amplitude, 1e-5f);
            Assert.AreEqual(7f, path.Frequency, 1e-5f);
        }

        [Test]
        public void Count_Fixed_SetsBothEnds()
        {
            var e = (A2BBurstEmission)A2BEffectBuilder.Create().Count(11).Build().Emission;
            Assert.AreEqual(11, e.MinCount);
            Assert.AreEqual(11, e.MaxCount);
        }

        [Test]
        public void Count_Range_KeepsMaxAtOrAboveMin()
        {
            var e = (A2BBurstEmission)A2BEffectBuilder.Create().Count(9, 2).Build().Emission;
            Assert.AreEqual(9, e.MinCount);
            Assert.AreEqual(9, e.MaxCount, "An inverted count range was accepted verbatim.");
        }

        [TestCase(-5)]
        [TestCase(0)]
        public void Count_BelowOne_ClampsToOne(int requested)
        {
            var e = (A2BBurstEmission)A2BEffectBuilder.Create().Count(requested).Build().Emission;
            Assert.AreEqual(1, e.MinCount);
        }

        [Test]
        public void Duration_BelowTheFloor_IsClamped_RatherThanProducingAnInvalidDefinition()
        {
            A2BEffectDefinition def = A2BEffectBuilder.Create().Duration(-3f).Build();
            Assert.AreEqual(0.01f, def.Duration, 1e-6f);
            Assert.IsTrue(def.Validate(out _), "A negative duration produced a definition that cannot play.");
        }

        [Test]
        public void DurationJitter_IsClampedToUnitRange()
        {
            Assert.AreEqual(1f, A2BEffectBuilder.Create().DurationJitter(4f).Build().DurationJitter, 1e-5f);
            Assert.AreEqual(0f, A2BEffectBuilder.Create().DurationJitter(-4f).Build().DurationJitter, 1e-5f);
        }

        [Test]
        public void Scatter_NegativeRadius_ClampsToZero()
        {
            var e = (A2BBurstEmission)A2BEffectBuilder.Create().Scatter(-2f).Build().Emission;
            Assert.AreEqual(0f, e.ScatterRadius, 1e-6f);
        }

        [Test]
        public void Scatter_WithAxisWeights_ForwardsBoth()
        {
            var e = (A2BBurstEmission)A2BEffectBuilder.Create().Scatter(3f, new Vector3(1f, 1f, 0f)).Build().Emission;
            Assert.AreEqual(3f, e.ScatterRadius, 1e-6f);
            Assert.AreEqual(new Vector3(1f, 1f, 0f), e.ScatterAxisWeights);
        }

        [Test]
        public void ReleaseModeHelpers_SetTheMode()
        {
            Assert.AreEqual(A2BReleaseMode.AllAtOnce,
                ((A2BBurstEmission)A2BEffectBuilder.Create().AllAtOnce().Build().Emission).ReleaseMode);

            var staggered = (A2BBurstEmission)A2BEffectBuilder.Create().Stagger(0.07f).Build().Emission;
            Assert.AreEqual(A2BReleaseMode.FixedStagger, staggered.ReleaseMode);
            Assert.AreEqual(0.07f, staggered.StaggerInterval, 1e-6f);

            var spread = (A2BBurstEmission)A2BEffectBuilder.Create().SpreadOver(0.9f).Build().Emission;
            Assert.AreEqual(A2BReleaseMode.SpreadOverDuration, spread.ReleaseMode);
            Assert.AreEqual(0.9f, spread.SpreadDuration, 1e-6f);
        }

        [Test]
        public void BurstHelpers_ReplaceANonBurstEmission_RatherThanFailingSilently()
        {
            // Count/Stagger/Scatter only mean something for a burst. Calling them after a custom
            // emission must install a burst, not no-op.
            A2BEffectDefinition def = A2BEffectBuilder.Create()
                .Emission(new NoOpEmission())
                .Count(5)
                .Build();

            Assert.IsInstanceOf<A2BBurstEmission>(def.Emission);
            Assert.AreEqual(5, def.Emission.ResolveItemCount(1u));
        }

        private sealed class NoOpEmission : IA2BEmission
        {
            public int ResolveItemCount(uint effectSeed) => 1;
            public float ResolveDelay(uint effectSeed, int itemIndex, int itemCount) => 0f;
            public Vector3 ResolveScatter(uint effectSeed, int itemIndex, int itemCount) => Vector3.zero;
            public float ScatterRadius => 0f;
        }

        [Test]
        public void ResolveItemDuration_WithoutJitter_IsExactlyTheAuthoredDuration()
        {
            A2BEffectDefinition def = A2BEffectBuilder.Create().Duration(0.75f).DurationJitter(0f).Build();
            // Reached through the play path; ResolveItemDuration itself is internal to Core.
            var scheduler = new A2BScheduler();
            var time = new A2BManualTimeSource();
            var presenter = new RecordingPresenter();
            var args = new A2BPlayArgs(new A2BStaticEndpoint(Vector3.zero),
                new A2BStaticEndpoint(Vector3.right), presenter, seed: 5u);

            A2BEffectHandle handle = scheduler.Play(
                A2BEffectBuilder.From(def).Linear().Ease(A2BEaseKind.Linear).Count(1).AllAtOnce().Scatter(0f).Build(),
                in args);
            scheduler.SetTimeSource(handle, time);

            time.Advance(0.375f);
            scheduler.Tick();

            Assert.AreEqual(0.5f, presenter.StateForItem(0).Progress, 1e-4f,
                "Half the authored duration did not produce half the progress; DurationJitter(0) is not exact.");
        }
    }
}
