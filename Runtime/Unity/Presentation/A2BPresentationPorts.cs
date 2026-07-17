using A2BKit.Core;
using UnityEngine;

namespace A2BKit.Unity
{
    /// <summary>
    /// Converts coordinates for one space, and owns the item's Transform.
    ///
    /// This lives in A2BKit.Unity rather than Core because it touches the scene graph, which Core
    /// bans (AD-1/AD-18). It is still an open/closed extension point (FR-6) — just one layer out.
    ///
    /// This type is the ONLY place permitted to call Camera.WorldToScreenPoint,
    /// RectTransformUtility.*, or otherwise convert between coordinate domains (AD-4), and the ONLY
    /// place permitted to write an item's position, rotation, scale or parent (AD-15). A conversion
    /// call site anywhere else is a defect regardless of correctness.
    /// </summary>
    public interface IA2BSpaceAdapter
    {
        /// <summary>Which space this adapter serves. Drives pool identity (AD-14).</summary>
        A2BSpaceKind Space { get; }

        /// <summary>The parent every item of this adapter is placed under. Defines working space.</summary>
        Transform Root { get; }

        /// <summary>Endpoint to working space, honouring its A2BEndpointSpace (AD-4).</summary>
        Vector3 ToWorkingSpace(in A2BEndpointSample sample);

        /// <summary>Unitless [-1,1]^3 scatter to working-space units (AD-16).</summary>
        Vector3 ScaleScatter(in Vector3 unitOffset, float radius);

        /// <summary>
        /// Writes the item's transform. The only place that happens (AD-15) — payload renderers
        /// touch drawables only.
        /// </summary>
        void ApplyToTransform(Transform target, in A2BVisualState state);
    }

    /// <summary>
    /// Makes one item look like something: sprite, mesh, text, particle.
    ///
    /// Owns DRAWABLE PROPERTIES ONLY — sprite, mesh, colour, text, material. A renderer that writes
    /// transform.position/localPosition/parent/localScale is a defect regardless of correctness
    /// (AD-15): two renderers each obeying AD-4 could still disagree on localPosition vs position and
    /// put a Canvas-space effect at world origin.
    /// </summary>
    public interface IA2BPayloadRenderer
    {
        /// <summary>Distinguishes pools. Combined with definition and space to form pool identity (AD-14).</summary>
        string PayloadKey { get; }

        /// <summary>
        /// Returns a fresh renderer carrying this one's configuration but none of its runtime state.
        ///
        /// Unlike the other strategies, a payload renderer is NOT stateless — it owns a pool and a
        /// root, so AD-2's "shared, stateless" rule cannot apply to it. But [SerializeReference]
        /// gives an asset exactly ONE authored instance, so two A2BEffectPlayers on two different
        /// canvases would otherwise share it: the second presenter's Initialize is ignored and its
        /// items silently parent to the FIRST one's canvas. That is a wrong-canvas bug with no error
        /// message.
        ///
        /// So the authored instance is a prototype and every presenter clones it — which is also
        /// what makes pool identity genuinely (PayloadKind, Definition, Space) per AD-14.
        /// </summary>
        IA2BPayloadRenderer CreateRuntimeInstance();

        /// <summary>Called once before first use. Parent is the adapter Root the items live under.</summary>
        void Initialize(Transform root, int prewarmCount);

        /// <summary>Acquires a pooled visual and returns the Transform for the adapter to place (AD-15).</summary>
        Transform Acquire(in A2BItemSpawnInfo info);

        /// <summary>Applies drawable properties only. Must not touch the Transform.</summary>
        void UpdateItem(Transform item, in A2BVisualState state);

        /// <summary>Returns the visual to its pool.</summary>
        void Release(Transform item);

        /// <summary>Destroys pooled instances. Called on teardown and domain reload (NFR-5).</summary>
        void Dispose();

        /// <summary>Pool occupancy for the debug overlay (FR-22).</summary>
        void GetPoolStats(out int active, out int available);
    }
}
