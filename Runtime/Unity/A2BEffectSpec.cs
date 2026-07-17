using System.Collections.Generic;
using A2BKit.Core;

namespace A2BKit.Unity
{
    /// <summary>
    /// Everything needed to play an effect, independent of whether a designer authored it in an asset
    /// or a programmer built it in code.
    ///
    /// This exists to make the two authoring surfaces actual peers (FR-1/FR-2). They were not:
    /// <see cref="A2B.Play"/> accepted only an <see cref="A2BEffectAsset"/>, so building a definition
    /// with <see cref="A2BEffectBuilder"/> — the whole code-first path — left you constructing a
    /// space adapter and an <see cref="A2BPresenter"/> by hand just to reach the one-liner. The asset
    /// had a front door and code had a service entrance.
    ///
    /// An asset now simply *is* a spec (<see cref="A2BEffectAsset.ToSpec"/>), and everything downstream
    /// takes the spec.
    /// </summary>
    public sealed class A2BEffectSpec
    {
        /// <summary>The coordinate domain to play in.</summary>
        public A2BSpaceKind Space = A2BSpaceKind.Canvas;

        /// <summary>What each item looks like. Required.</summary>
        public IA2BPayloadRenderer Payload;

        /// <summary>Motion, emission, appearance. Required.</summary>
        public A2BEffectDefinition Definition;

        /// <summary>Optional trails/impacts/audio. May be null or empty.</summary>
        public IList<IA2BFeedback> Feedbacks;

        /// <summary>Optional custom space adapter. Null uses the built-in for <see cref="Space"/>.</summary>
        public IA2BSpaceAdapterFactory SpaceOverride;

        /// <summary>
        /// Identity for pool caching. Two plays sharing an owner share a pool, so pass the asset (or
        /// any stable object) rather than letting every play build its own pools (AD-14).
        /// </summary>
        public object Owner;

        public A2BEffectSpec() { }

        public A2BEffectSpec(A2BEffectDefinition definition, IA2BPayloadRenderer payload, A2BSpaceKind space)
        {
            Definition = definition;
            Payload = payload;
            Space = space;
        }

        /// <summary>Checks what can be checked without a scene. Returns false rather than throwing (AD-8).</summary>
        public bool Validate(out string error)
        {
            if (Definition == null) { error = "Definition is missing."; return false; }
            if (!Definition.Validate(out error)) return false;
            if (Payload == null) { error = "No payload is assigned."; return false; }
            error = null;
            return true;
        }

        // ---- fluent sugar, so a code-first effect reads as one expression ----------------------

        public A2BEffectSpec In(A2BSpaceKind space) { Space = space; return this; }

        public A2BEffectSpec With(IA2BPayloadRenderer payload) { Payload = payload; return this; }

        /// <summary>Adds a feedback (trail, impact, sound). Chainable; call repeatedly to stack them.</summary>
        public A2BEffectSpec Feedback(IA2BFeedback feedback)
        {
            if (feedback == null) return this;
            Feedbacks ??= new List<IA2BFeedback>(2);
            Feedbacks.Add(feedback);
            return this;
        }

        public A2BEffectSpec UsingSpace(IA2BSpaceAdapterFactory factory) { SpaceOverride = factory; return this; }

        /// <summary>Shares pools with everything else using the same owner (AD-14).</summary>
        public A2BEffectSpec OwnedBy(object owner) { Owner = owner; return this; }
    }

    /// <summary>Turns a built definition straight into a playable spec, so the code path is a one-liner too.</summary>
    public static class A2BEffectBuilderExtensions
    {
        /// <summary>
        /// Finishes the builder into a spec:
        /// <c>A2BEffectBuilder.Create().Arc(2f).Count(12).AsSpec(payload, A2BSpaceKind.Canvas)</c>.
        /// Cache the result — building per play allocates (the builder is setup code, not tick code).
        /// </summary>
        public static A2BEffectSpec AsSpec(
            this A2BEffectBuilder builder, IA2BPayloadRenderer payload, A2BSpaceKind space = A2BSpaceKind.Canvas)
            => new A2BEffectSpec(builder.Build(), payload, space);
    }
}
