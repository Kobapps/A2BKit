using A2BKit.Core;
using UnityEngine;

namespace A2BKit.Unity
{
    /// <summary>
    /// An optional embellishment attached to an effect: a trail behind each coin, a spark where it
    /// lands, a pop as it spawns, a sound on arrival.
    ///
    /// Feedback is deliberately a SEPARATE port from <see cref="IA2BPayloadRenderer"/>. A payload
    /// answers "what is this item"; a feedback answers "what else happens because of it". Folding
    /// them together would mean every payload re-implemented trails, and adding a trail to text would
    /// mean editing the text payload — exactly the open/closed break FR-10 exists to prevent.
    ///
    /// Lives in A2BKit.Unity, not Core, because it touches the scene graph (AD-1/AD-18).
    ///
    /// Rules an implementer must not break:
    /// - **Never move the item.** The space adapter owns position, rotation, scale and parent (AD-15).
    ///   A feedback may add CHILD objects and set drawable properties; it may not write the item's own
    ///   Transform. Its own spawned objects are its own business.
    /// - **Never throw.** Log through <see cref="A2BLog"/> and degrade (AD-8).
    /// - **Never allocate per frame.** <see cref="OnItemUpdated"/> runs for every item every frame;
    ///   treat it like the tick path it is (AD-3). Pool anything you spawn.
    /// - **Be a prototype.** Like payloads, one authored instance is shared by an asset, so a feedback
    ///   that owns runtime state must hand out clones (AD-14) — derive from <see cref="A2BFeedbackBase"/>
    ///   and you get this for free.
    /// </summary>
    public interface IA2BFeedback
    {
        /// <summary>Names this feedback in the inspector and in diagnostics.</summary>
        string FeedbackKey { get; }

        /// <summary>
        /// A copy carrying this one's configuration and none of its runtime state. Same prototype
        /// rule as payload renderers, and for the same reason: an asset has ONE authored instance,
        /// so two presenters would otherwise share one pool and one root.
        /// </summary>
        IA2BFeedback CreateRuntimeInstance();

        /// <summary>Called once before first use. <paramref name="root"/> is the adapter's item root.</summary>
        void Initialize(Transform root);

        /// <summary>An item just appeared. Attach trails, play a spawn pop.</summary>
        void OnItemSpawned(Transform item, in A2BItemSpawnInfo info);

        /// <summary>
        /// Per-frame, per-item. Runs inside the tick — no allocation, no GetComponent, no strings.
        /// Most feedbacks leave this empty.
        /// </summary>
        void OnItemUpdated(Transform item, in A2BVisualState state);

        /// <summary>
        /// The item LANDED. This is the "effect on hit" hook: spark, punch, sound, haptic.
        /// Fires only on a genuine arrival — never on a cancelled or torn-down item.
        /// </summary>
        void OnItemArrived(Transform item, in A2BVisualState finalState);

        /// <summary>
        /// The item's visual is going back to the pool, for whatever reason. Detach anything you
        /// attached in <see cref="OnItemSpawned"/> — the item is about to be reused by another effect.
        /// Runs for arrivals AND cancellations, always after <see cref="OnItemArrived"/>.
        /// </summary>
        void OnItemReleased(Transform item, A2BReleaseReason reason);

        /// <summary>Destroy pooled objects. Must tolerate being called twice (NFR-5).</summary>
        void Dispose();
    }

    /// <summary>
    /// Base for feedbacks: implements the prototype clone and leaves every hook empty, so an
    /// implementer overrides only the moment they care about.
    ///
    /// A class, never a struct — it is stored behind an interface and a struct would box on every
    /// call, allocating in the tick while looking allocation-free (AD-2).
    /// </summary>
    public abstract class A2BFeedbackBase : IA2BFeedback
    {
        [Tooltip("Turn this feedback off without removing it. Handy for A/B-ing feel.")]
        public bool Enabled = true;

        private Transform _root;
        private bool _initialized;

        /// <summary>The adapter's item root. Parent anything you spawn under it, not under the item.</summary>
        protected Transform Root => _root;

        protected bool IsInitialized => _initialized;

        public abstract string FeedbackKey { get; }

        /// <summary>
        /// MemberwiseClone copies subclass fields by reference too, which would share runtime state.
        /// Resetting _initialized forces <see cref="OnInitialized"/> to re-run and rebuild it — which
        /// is why subclass state belongs there and never in a field initializer.
        /// </summary>
        public virtual IA2BFeedback CreateRuntimeInstance()
        {
            var clone = (A2BFeedbackBase)MemberwiseClone();
            clone._root = null;
            clone._initialized = false;
            return clone;
        }

        public void Initialize(Transform root)
        {
            if (_initialized) return;
            _root = root;
            _initialized = true;
            OnInitialized();
        }

        public void OnItemSpawned(Transform item, in A2BItemSpawnInfo info)
        {
            if (!Enabled || !_initialized || item == null) return;
            try { Spawned(item, in info); }
            catch (System.Exception e) { A2BLog.Exception(null, e); }   // AD-8: user code, never escapes
        }

        public void OnItemUpdated(Transform item, in A2BVisualState state)
        {
            if (!Enabled || !_initialized || item == null) return;
            Updated(item, in state);   // no try/catch: this is the per-frame path (AD-3)
        }

        public void OnItemArrived(Transform item, in A2BVisualState finalState)
        {
            if (!Enabled || !_initialized || item == null) return;
            try { Arrived(item, in finalState); }
            catch (System.Exception e) { A2BLog.Exception(null, e); }
        }

        public void OnItemReleased(Transform item, A2BReleaseReason reason)
        {
            if (!_initialized || item == null) return;
            // Deliberately NOT gated on Enabled: a feedback disabled mid-flight must still detach
            // whatever it attached, or the next effect inherits a stray trail.
            try { Released(item, reason); }
            catch (System.Exception e) { A2BLog.Exception(null, e); }
        }

        public void Dispose()
        {
            if (!_initialized) return;
            try { Disposed(); }
            catch (System.Exception e) { A2BLog.Exception(null, e); }
            _initialized = false;
            _root = null;
        }

        /// <summary>Build runtime state here — never in a field initializer (see CreateRuntimeInstance).</summary>
        protected virtual void OnInitialized() { }

        protected virtual void Spawned(Transform item, in A2BItemSpawnInfo info) { }
        protected virtual void Updated(Transform item, in A2BVisualState state) { }
        protected virtual void Arrived(Transform item, in A2BVisualState finalState) { }
        protected virtual void Released(Transform item, A2BReleaseReason reason) { }
        protected virtual void Disposed() { }
    }
}
