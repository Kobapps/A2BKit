using UnityEngine;

namespace A2BKit.Core
{
    // The ports. Everything variable about an effect enters through one of these (AD-2, NFR-6).
    //
    // Every implementation MUST be a class, never a struct: a struct assigned to an interface-typed
    // field boxes on assignment and again on every virtual call, allocating in the hot loop while the
    // code still reads as zero-alloc (AD-2). [SerializeReference] independently enforces this — it
    // refuses value types.
    //
    // Implementations MUST be stateless and are shared across effects and items. Per-effect data
    // travels in the `in`-passed context struct. A strategy with mutable instance state breaks
    // FR-3's "concurrent plays share no mutable state".
    //
    // Note what is NOT here: IA2BSpaceAdapter and IA2BPayloadRenderer. They need Transform, and Core
    // bans scene-graph types (AD-1), so they live in A2BKit.Unity behind IA2BPresenter (AD-18).

    /// <summary>
    /// Maps normalized progress to a position in working space.
    /// Pure: no frame state, no side effects, no allocation (AD-13).
    /// </summary>
    public interface IA2BPath
    {
        /// <summary>
        /// Must satisfy Evaluate(ctx, 0) == ctx.Origin and Evaluate(ctx, 1) == ctx.Destination
        /// within tolerance — including for custom paths. Arrival is defined as t &gt;= 1, so a path
        /// that does not land on the destination makes ItemArrived/FirstItemArrived meaningless.
        /// A2BPathConformance asserts this; run it against your own path.
        /// </summary>
        Vector3 Evaluate(in A2BPathContext ctx, float t);
    }

    /// <summary>
    /// Reparameterizes progress over time. Applied to t BEFORE the path evaluates it —
    /// a path that eases internally double-applies and breaks composition (AD-13, FR-11).
    /// </summary>
    public interface IA2BEasing
    {
        float Evaluate(float t);
    }

    /// <summary>
    /// Decides how the N items are released over time and scattered in space.
    /// Everything here is COMPUTED from (seed, index) on demand — never collected into a
    /// per-item list, which would allocate per play and scale with item count (AD-10).
    /// </summary>
    public interface IA2BEmission
    {
        /// <summary>How many items this play emits. May vary per play via the seed.</summary>
        int ResolveItemCount(uint effectSeed);

        /// <summary>Seconds before this item starts moving. Pure function of (seed, index).</summary>
        float ResolveDelay(uint effectSeed, int itemIndex, int itemCount);

        /// <summary>
        /// Initial scatter offset around the origin, UNITLESS and normalized to [-1,1]^3.
        /// The space adapter assigns units (AD-16) — otherwise "radius 50" means 50px on a
        /// Canvas and 50m in World3D from the same asset.
        /// </summary>
        Vector3 ResolveScatter(uint effectSeed, int itemIndex, int itemCount);

        /// <summary>
        /// The scale the space adapter multiplies <see cref="ResolveScatter"/> by, in working-space
        /// units. Zero disables scatter entirely.
        ///
        /// On the interface rather than read off a concrete type: the scheduler previously
        /// downcast to A2BBurstEmission to reach this, which silently gave every CUSTOM emission a
        /// scatter radius of zero — an open/closed violation (FR-10) that would look like "scatter
        /// just doesn't work for my emission" and be near-impossible to trace. It also put a type
        /// check in the per-frame tick path (AD-3).
        /// </summary>
        float ScatterRadius { get; }
    }

    /// <summary>
    /// Supplies delta time. The ONLY type permitted to read UnityEngine.Time (AD-12).
    /// Tests and the editor preview inject a manual source — the identical seam, which is why
    /// preview cannot drift from runtime behavior.
    /// </summary>
    public interface IA2BTimeSource
    {
        float DeltaTime { get; }
    }

    /// <summary>
    /// Resolves an endpoint to a WORLD position, every frame — never cached at play (FR-12).
    /// Returning an invalid sample is how a destroyed target is reported; it must not throw (AD-8).
    /// </summary>
    public interface IA2BEndpointProvider
    {
        A2BEndpointSample Resolve();
    }

    /// <summary>
    /// Core's single presentation port (AD-18). Speaks in item ids and value structs, never Transforms,
    /// so Core stays free of scene-graph types (AD-1) and testable with a recording stub and no scene.
    /// A2BPresenter in A2BKit.Unity implements this by composing a space adapter and a payload renderer.
    /// </summary>
    public interface IA2BPresenter
    {
        /// <summary>Acquire a visual for one item. Returns an id used for later Apply/Release.</summary>
        int Acquire(in A2BItemSpawnInfo info);

        /// <summary>Position and style the item for this frame.</summary>
        void Apply(int itemId, in A2BVisualState state);

        /// <summary>
        /// Return the item's visual to its pool. Must tolerate being called for an id it already released.
        ///
        /// The reason is what lets the presentation layer fire arrival feedback (a spark, a punch, a
        /// sound) on a landing and stay silent on a teardown — otherwise cancelling a 50-coin burst
        /// would emit 50 impacts at once.
        /// </summary>
        void Release(int itemId, A2BReleaseReason reason);

        /// <summary>
        /// Converts a unitless [-1,1]^3 scatter offset into working-space units (AD-16).
        /// This is the presenter's, not emission's, because only it knows the space.
        /// </summary>
        Vector3 ScaleScatter(in Vector3 unitOffset, float radius);

        /// <summary>
        /// Converts a resolved endpoint into the effect's working space (AD-4).
        ///
        /// Takes the whole sample rather than a bare Vector3 so the adapter can honour
        /// <see cref="A2BEndpointSpace"/>: a screen-space endpoint is already projected and must not
        /// be projected a second time.
        /// </summary>
        Vector3 ToWorkingSpace(in A2BEndpointSample sample);
    }

    /// <summary>
    /// The allocation-free event path (AD-11). Supply one reusable implementer; dispatch is a plain
    /// interface call. The C# event convenience API on A2BEffectHandle costs Per-Play allocation when
    /// the subscriber captures — that is documented, not hidden.
    /// A listener that throws is caught and logged; it cannot leak pooled items or suppress the rest (AD-8).
    /// </summary>
    public interface IA2BEffectListener
    {
        void OnStarted(in A2BEffectHandle handle);
        void OnItemSpawned(in A2BEffectHandle handle, int itemIndex);

        /// <summary>Fires exactly once per effect, on the first arrival, always before OnCompleted.</summary>
        void OnFirstItemArrived(in A2BEffectHandle handle, int itemIndex);

        void OnItemArrived(in A2BEffectHandle handle, int itemIndex);

        /// <summary>Exactly one of OnCompleted / OnCancelled fires per effect — never both, never neither.</summary>
        void OnCompleted(in A2BEffectHandle handle);

        void OnCancelled(in A2BEffectHandle handle, A2BCompletionReason reason);
    }

    /// <summary>
    /// Adapter base so listeners override only what they need. A class, so it never boxes (AD-2).
    /// </summary>
    public abstract class A2BEffectListenerBase : IA2BEffectListener
    {
        public virtual void OnStarted(in A2BEffectHandle handle) { }
        public virtual void OnItemSpawned(in A2BEffectHandle handle, int itemIndex) { }
        public virtual void OnFirstItemArrived(in A2BEffectHandle handle, int itemIndex) { }
        public virtual void OnItemArrived(in A2BEffectHandle handle, int itemIndex) { }
        public virtual void OnCompleted(in A2BEffectHandle handle) { }
        public virtual void OnCancelled(in A2BEffectHandle handle, A2BCompletionReason reason) { }
    }
}
