using A2BKit.Core;
using NUnit.Framework;
using UnityEngine;

namespace A2BKit.Tests.EditMode
{
    /// <summary>
    /// Guards <see cref="A2BEffectDefinition.ResolveSpan"/> — the total end-to-end length the editor
    /// timeline scrubs over. The value the whole scrub bar depends on, so it is worth pinning: a span
    /// that under-reports would cut the tail of a staggered burst off the timeline.
    /// </summary>
    public sealed class A2BEffectSpanTests
    {
        private const uint Seed = 0x2B2B2B2Bu;

        [Test]
        public void All_at_once_with_no_jitter_spans_exactly_one_duration()
        {
            var def = new A2BEffectDefinition
            {
                Duration = 0.8f,
                DurationJitter = 0f,
                Emission = new A2BBurstEmission { MinCount = 10, MaxCount = 10, ReleaseMode = A2BReleaseMode.AllAtOnce }
            };

            Assert.That(def.ResolveSpan(Seed), Is.EqualTo(0.8f).Within(1e-4f));
        }

        [Test]
        public void Fixed_stagger_span_reaches_the_last_item_plus_its_duration()
        {
            var def = new A2BEffectDefinition
            {
                Duration = 0.5f,
                DurationJitter = 0f,
                Emission = new A2BBurstEmission
                {
                    MinCount = 8,
                    MaxCount = 8,
                    ReleaseMode = A2BReleaseMode.FixedStagger,
                    StaggerInterval = 0.1f
                }
            };

            // Last of 8 items releases at 7 * 0.1, then travels for 0.5.
            Assert.That(def.ResolveSpan(Seed), Is.EqualTo(0.7f + 0.5f).Within(1e-4f));
        }

        [Test]
        public void Span_is_never_shorter_than_a_single_duration()
        {
            var def = new A2BEffectDefinition
            {
                Duration = 0.6f,
                DurationJitter = 0.5f,
                Emission = new A2BBurstEmission
                {
                    MinCount = 20,
                    MaxCount = 20,
                    ReleaseMode = A2BReleaseMode.SpreadOverDuration,
                    SpreadDuration = 0.4f
                }
            };

            Assert.That(def.ResolveSpan(Seed), Is.GreaterThanOrEqualTo(0.6f));
        }

        [Test]
        public void Span_is_deterministic_for_a_given_seed()
        {
            var def = new A2BEffectDefinition
            {
                Duration = 0.7f,
                DurationJitter = 0.3f,
                Emission = new A2BBurstEmission
                {
                    MinCount = 6,
                    MaxCount = 14,
                    ReleaseMode = A2BReleaseMode.FixedStagger,
                    StaggerInterval = 0.05f,
                    DelayJitter = 0.1f
                }
            };

            Assert.That(def.ResolveSpan(Seed), Is.EqualTo(def.ResolveSpan(Seed)));
        }

        [Test]
        public void A_null_emission_falls_back_to_duration_rather_than_throwing()
        {
            var def = new A2BEffectDefinition { Duration = 0.9f, Emission = null };

            Assert.That(def.ResolveSpan(Seed), Is.EqualTo(0.9f).Within(1e-4f));
        }
    }
}
