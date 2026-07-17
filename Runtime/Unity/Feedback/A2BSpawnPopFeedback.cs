using System.Collections.Generic;
using A2BKit.Core;
using UnityEngine;
using UnityEngine.UI;

namespace A2BKit.Unity
{
    /// <summary>
    /// The item announces itself as it appears — a bright flash that decays into its normal colour
    /// over the first fraction of its flight, with an optional fade-in from transparent.
    ///
    /// **Why this is a colour pop and not a scale pop.** The brief was an overshoot-and-settle on
    /// scale, and that feature cannot be built here without breaking AD-15: scale on the item is the
    /// Space Adapter's to write, and the adapter rewrites <c>localScale</c> from
    /// <c>A2BVisualState</c> every single frame, so a feedback's scale write would be overwritten
    /// within the same frame anyway — the rule and the mechanics agree. The usual escape, "own a
    /// child wrapper", does not apply either, and it is worth being precise about why: a wrapper
    /// works when you own the thing being scaled, but here the visual *is* the item, and a child of
    /// the item can only scale itself, not its parent's sprite. Scaling the item from a child would
    /// require reparenting the item's renderer under the wrapper — a Transform write on the item, and
    /// a mutation of an object owned by the payload pool. So the scale pop is dropped rather than
    /// smuggled in. An author who wants one has the sanctioned route: a scale curve on the path or
    /// the emission feeds <c>A2BVisualState.Scale</c>, and the adapter applies it — which is AD-15
    /// working as designed, not a workaround.
    ///
    /// Colour is fair game: the feedback port grants drawable properties explicitly, and the
    /// presenter calls feedbacks *after* <c>renderer.UpdateItem</c>, so a write here lands last and
    /// survives the frame.
    ///
    /// **Driven by Progress, not by a clock.** <c>state.Progress</c> is already normalized flight
    /// time, so the pop needs no delta, reads no <c>Time</c> (AD-12), stores no per-item elapsed, and
    /// replays identically under a manual time source (NFR-4). It also means the pop stretches with
    /// the effect's duration, which is what an author authoring "the first quarter of the flight"
    /// actually wants.
    /// </summary>
    [System.Serializable]
    public sealed class A2BSpawnPopFeedback : A2BFeedbackBase
    {
        /// <summary>Fraction of the item's flight the pop covers. 0.25 = the first quarter.</summary>
        public float PopFraction = 0.25f;

        /// <summary>The colour the item flashes toward at full blend.</summary>
        public Color FlashColor = Color.white;

        /// <summary>Blend weight over the normalized pop. Overshoot lives here, on colour instead of scale.</summary>
        public AnimationCurve Curve = new AnimationCurve(
            new Keyframe(0f, 1f), new Keyframe(0.3f, 0.85f), new Keyframe(1f, 0f));

        /// <summary>Also ramp alpha from 0, so the item materializes rather than snapping in.</summary>
        public bool FadeIn = true;

        /// <summary>
        /// Drawable resolved once per spawn. GetComponent here is a per-item cost on the spawn path,
        /// which the payload pool already pays; what AD-3 forbids is paying it in
        /// <see cref="Updated"/>, and that path is a dictionary probe and a colour write.
        /// </summary>
        private sealed class Binding
        {
            public SpriteRenderer Sprite;
            public Graphic Graphic;

            /// <summary>True once the item's real colour has been handed back. See the restore note.</summary>
            public bool Settled;
        }

        // Runtime state — built in OnInitialized, never in a field initializer, or two clones of this
        // prototype would share one binding table (AD-14).
        private Dictionary<Transform, Binding> _bindings;
        private Stack<Binding> _free;
        private AnimationCurve _curve;

        public override string FeedbackKey => "SpawnPop";

        protected override void OnInitialized()
        {
            _bindings = new Dictionary<Transform, Binding>(32, A2BTransformComparer.Instance);

            // Bindings are recycled rather than allocated per spawn: a 200-item burst would otherwise
            // allocate 200 objects at Play(), which is the spike NFR-1 exists to prevent (AD-3).
            _free = new Stack<Binding>(32);

            _curve = Curve != null && Curve.length > 0
                ? Curve
                : new AnimationCurve(new Keyframe(0f, 1f), new Keyframe(1f, 0f));
        }

        protected override void Spawned(Transform item, in A2BItemSpawnInfo info)
        {
            if (_bindings == null || _bindings.ContainsKey(item)) return;

            Binding binding = _free.Count > 0 ? _free.Pop() : new Binding();

            // GetComponentInChildren, not GetComponent: a prefab payload's renderer is usually a
            // child of the item root, and includeInactive matters because the item is activated by
            // the pool's actionOnGet, whose ordering against this hook is not ours to assume.
            binding.Sprite = item.GetComponentInChildren<SpriteRenderer>(true);
            binding.Graphic = binding.Sprite == null ? item.GetComponentInChildren<Graphic>(true) : null;
            binding.Settled = false;

            if (binding.Sprite == null && binding.Graphic == null)
            {
                // A mesh or particle payload has no colour property this feedback can drive. Not an
                // error — it is a legitimate combination — and it must not log per item (AD-3/AD-8).
                _free.Push(binding);
                return;
            }

            _bindings[item] = binding;
        }

        /// <summary>
        /// Per item, per frame (AD-3): a dictionary probe, a curve evaluate, a colour write. No
        /// GetComponent, no allocation, no closure.
        /// </summary>
        protected override void Updated(Transform item, in A2BVisualState state)
        {
            if (_bindings == null) return;
            if (!_bindings.TryGetValue(item, out Binding binding)) return;
            if (binding.Settled) return;

            float span = Mathf.Max(0.0001f, PopFraction);
            float t = Mathf.Clamp01(state.Progress / span);

            Color color;
            if (t >= 1f)
            {
                // The settle frame, and it is mandatory rather than tidy. A2BSpritePayloadRenderer
                // caches the last colour it wrote and skips redundant writes, so once this feedback
                // has written behind its back, the payload can believe it already wrote a colour that
                // is not on screen — and the item would stay flashed for the rest of its flight.
                // Handing the item's real colour back once, exactly, re-syncs that cache.
                color = state.Color;
                binding.Settled = true;
            }
            else
            {
                // Unclamped: the curve is allowed to overshoot past FlashColor, which is where the
                // "pop" in the name comes from now that scale is off the table.
                color = Color.LerpUnclamped(state.Color, FlashColor, _curve.Evaluate(t));
                if (FadeIn) color.a *= t;
            }

            if (binding.Sprite != null) binding.Sprite.color = color;
            else if (binding.Graphic != null) binding.Graphic.color = color;
        }

        protected override void Released(Transform item, A2BReleaseReason reason)
        {
            if (_bindings == null) return;
            if (!_bindings.TryGetValue(item, out Binding binding)) return;

            _bindings.Remove(item);

            // Drop the component references before recycling: holding a destroyed SpriteRenderer
            // keeps a managed wrapper alive for an object the payload pool has already destroyed.
            binding.Sprite = null;
            binding.Graphic = null;
            binding.Settled = false;
            _free.Push(binding);
        }

        protected override void Disposed()
        {
            // Nothing is spawned by this feedback, so there is nothing to destroy — but the tables
            // still reference pooled Transforms, and a second Dispose must be harmless (NFR-5).
            _bindings?.Clear();
            _free?.Clear();
        }
    }
}
