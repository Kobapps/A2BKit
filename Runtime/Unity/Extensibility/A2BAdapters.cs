using System;
using System.Collections.Generic;
using A2BKit.Core;
using UnityEngine;

namespace A2BKit.Unity
{
    /// <summary>
    /// Everything the package knows when it needs to build a space adapter. Passed by <c>in</c>.
    /// </summary>
    public readonly struct A2BSpaceContext
    {
        /// <summary>The space the effect asked for.</summary>
        public readonly A2BSpaceKind Space;

        /// <summary>The canvas the destination lives on, if any. Null for world effects.</summary>
        public readonly Canvas HostCanvas;

        /// <summary>Camera used to project world positions. Null means Camera.main.</summary>
        public readonly Camera Camera;

        /// <summary>The destination transform, for adapters that need to reason about the target.</summary>
        public readonly Transform Destination;

        /// <summary>An explicit root override (e.g. A2BEffectPlayer.CanvasRoot). Null means "decide for me".</summary>
        public readonly Transform RootOverride;

        public A2BSpaceContext(A2BSpaceKind space, Canvas hostCanvas, Camera camera,
                               Transform destination, Transform rootOverride)
        {
            Space = space;
            HostCanvas = hostCanvas;
            Camera = camera;
            Destination = destination;
            RootOverride = rootOverride;
        }
    }

    /// <summary>
    /// Builds a space adapter. The seam that makes FR-6 ("register a custom Space Adapter without
    /// modifying any shipped class") actually true.
    ///
    /// It was NOT true before this existed: <see cref="A2B"/> and <see cref="A2BEffectPlayer"/> both
    /// hard-coded `switch (space) { Canvas: … World2D: … default: … }`, so a custom adapter could be
    /// written but never reached through any public entry point — you had to construct A2BPresenter
    /// by hand or edit a shipped file. The interface existed; the extensibility did not.
    /// </summary>
    public interface IA2BSpaceAdapterFactory
    {
        IA2BSpaceAdapter Create(in A2BSpaceContext context);
    }

    /// <summary>
    /// The package's extension registry: swap in your own space adapters, globally or per space.
    ///
    /// Two ways to extend, deliberately:
    /// - **Per asset** — set <see cref="A2BEffectAsset.SpaceOverride"/> in the inspector. Local, visible,
    ///   no code.
    /// - **Globally** — <see cref="SetFactory"/> here, e.g. from a bootstrap, to make every Canvas
    ///   effect in the game use your adapter. Broad, code-only.
    ///
    /// Everything else (paths, easings, emissions, payloads, feedbacks) needs no registry at all:
    /// they are [SerializeReference] fields, and the inspector's type picker finds every implementation
    /// via TypeCache automatically. Spaces are the exception because the choice is an enum, and an enum
    /// cannot be extended from outside — hence this.
    /// </summary>
    public static class A2BAdapters
    {
        private static readonly Dictionary<A2BSpaceKind, IA2BSpaceAdapterFactory> _overrides =
            new Dictionary<A2BSpaceKind, IA2BSpaceAdapterFactory>();

        /// <summary>
        /// Routes every effect in the given space through your factory. Pass null to restore the
        /// built-in. Call from a bootstrap; this is global state, so calling it per-effect is a smell —
        /// use <see cref="A2BEffectAsset.SpaceOverride"/> for a one-off.
        /// </summary>
        public static void SetFactory(A2BSpaceKind space, IA2BSpaceAdapterFactory factory)
        {
            if (factory == null) _overrides.Remove(space);
            else _overrides[space] = factory;
        }

        /// <summary>True when a custom factory is registered for this space.</summary>
        public static bool HasFactory(A2BSpaceKind space) => _overrides.ContainsKey(space);

        /// <summary>Drops all global overrides. Mostly for tests, which must not leak into each other.</summary>
        public static void ResetFactories() => _overrides.Clear();

        /// <summary>
        /// Resolves the adapter for a context. Precedence, most specific first:
        /// per-asset override → global registry → built-in.
        /// </summary>
        public static IA2BSpaceAdapter Resolve(in A2BSpaceContext context, IA2BSpaceAdapterFactory assetOverride)
        {
            if (assetOverride != null)
            {
                IA2BSpaceAdapter custom = SafeCreate(assetOverride, in context);
                if (custom != null) return custom;
                // A broken override falls through to the built-in rather than killing the effect (AD-8).
            }

            if (_overrides.TryGetValue(context.Space, out IA2BSpaceAdapterFactory registered))
            {
                IA2BSpaceAdapter custom = SafeCreate(registered, in context);
                if (custom != null) return custom;
            }

            return CreateBuiltIn(in context);
        }

        private static IA2BSpaceAdapter SafeCreate(IA2BSpaceAdapterFactory factory, in A2BSpaceContext context)
        {
            try
            {
                IA2BSpaceAdapter adapter = factory.Create(in context);
                if (adapter == null)
                    A2BLog.Warn(null, "A space adapter factory returned null; falling back to the built-in adapter.");
                return adapter;
            }
            catch (Exception e)
            {
                // User code. It must not take the effect (or the game) down with it (AD-8).
                A2BLog.Exception(null, e);
                return null;
            }
        }

        private static IA2BSpaceAdapter CreateBuiltIn(in A2BSpaceContext context)
        {
            switch (context.Space)
            {
                case A2BSpaceKind.Canvas:
                {
                    var root = context.RootOverride as RectTransform;
                    if (root == null) root = A2BCanvasPool.GetDedicatedRoot(context.HostCanvas);
                    return new A2BCanvasAdapter(root, context.Camera);
                }

                case A2BSpaceKind.World2D:
                    return new A2BWorld2DAdapter(ResolveWorldRoot(in context, "[A2B World2D Root]"), context.Camera);

                default:
                    return new A2BWorld3DAdapter(ResolveWorldRoot(in context, "[A2B World3D Root]"), context.Camera);
            }
        }

        private static Transform ResolveWorldRoot(in A2BSpaceContext context, string rootName)
        {
            if (context.RootOverride != null) return context.RootOverride;

            var go = new GameObject(rootName);
            go.hideFlags = HideFlags.DontSave;

            // Guarded: DontDestroyOnLoad throws outside play mode, and editor tooling builds adapters too.
            if (Application.isPlaying) UnityEngine.Object.DontDestroyOnLoad(go);

            return go.transform;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics() => _overrides.Clear();
    }
}
