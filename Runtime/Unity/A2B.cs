using System.Collections.Generic;
using System.Threading;
using A2BKit.Core;
using Cysharp.Threading.Tasks;
using UnityEngine;

namespace A2BKit.Unity
{
    /// <summary>
    /// The one-liner entry point.
    ///
    /// Exists to serve SM-3 (a coin-to-wallet effect running in five minutes without reading source).
    /// It is a convenience facade over the real API, not a replacement for it: anything tuned or
    /// perf-critical should hold its own A2BPresenter and definition rather than going through here.
    ///
    /// Presenters are cached per (payload, space, root) — the pool identity from AD-14 — so repeated
    /// calls reuse pools instead of building a new one per play.
    /// </summary>
    public static class A2B
    {
        private static readonly Dictionary<PresenterKey, A2BPresenter> _presenters =
            new Dictionary<PresenterKey, A2BPresenter>();

        /// <summary>
        /// Flies items from a world Transform to a UI RectTransform — the canonical coin-to-wallet case.
        /// Returns an invalid handle rather than throwing if something is missing (AD-8).
        /// </summary>
        public static A2BEffectHandle Play(
            A2BEffectAsset asset,
            Transform origin,
            Transform destination,
            string text = null,
            float value = 0f,
            CancellationToken cancellationToken = default)
        {
            if (asset == null)
            {
                A2BLog.Error(null, "A2B.Play failed: asset is null.");
                return A2BEffectHandle.Invalid;
            }
            return Play(asset.ToSpec(), origin, destination, text, value, cancellationToken);
        }

        /// <summary>
        /// Plays a spec — the code-first peer of the asset path (FR-2).
        ///
        /// <code>
        /// _spec ??= A2BEffectBuilder.Create().Arc(2f).Count(16).AsSpec(_coinPayload);
        /// A2B.Play(_spec, chest, walletIcon);
        /// </code>
        ///
        /// Cache the spec: rebuilding one per play allocates and defeats pool sharing.
        /// </summary>
        public static A2BEffectHandle Play(
            A2BEffectSpec spec,
            Transform origin,
            Transform destination,
            string text = null,
            float value = 0f,
            CancellationToken cancellationToken = default)
        {
            if (spec == null)
            {
                A2BLog.Error(null, "A2B.Play failed: spec is null.");
                return A2BEffectHandle.Invalid;
            }

            var context = spec.Owner as UnityEngine.Object;

            if (!spec.Validate(out string error))
            {
                A2BLog.Error(context, "A2B.Play failed: " + error);
                return A2BEffectHandle.Invalid;
            }
            if (origin == null || destination == null)
            {
                A2BLog.Error(context, "A2B.Play failed: origin or destination is null.");
                return A2BEffectHandle.Invalid;
            }

            A2BPresenter presenter = ResolvePresenter(spec, destination);
            if (presenter == null) return A2BEffectHandle.Invalid;

            var args = new A2BPlayArgs(
                origin: MakeEndpoint(origin),
                destination: MakeEndpoint(destination),
                presenter: presenter,
                text: text,
                value: value,
                cancellationToken: cancellationToken);

            return A2BRunner.Scheduler.Play(spec.Definition, in args, context);
        }

        /// <summary>Plays and awaits completion (FR-15).</summary>
        public static async UniTask<A2BCompletionReason> PlayAsync(
            A2BEffectAsset asset,
            Transform origin,
            Transform destination,
            string text = null,
            float value = 0f,
            CancellationToken cancellationToken = default)
        {
            A2BEffectHandle handle = Play(asset, origin, destination, text, value, cancellationToken);
            if (!handle.IsValid) return A2BCompletionReason.Invalid;
            return await handle.ToUniTask(cancellationToken);
        }

        /// <summary>Plays a spec and awaits completion — the code-first peer of the asset overload.</summary>
        public static async UniTask<A2BCompletionReason> PlayAsync(
            A2BEffectSpec spec,
            Transform origin,
            Transform destination,
            string text = null,
            float value = 0f,
            CancellationToken cancellationToken = default)
        {
            A2BEffectHandle handle = Play(spec, origin, destination, text, value, cancellationToken);
            if (!handle.IsValid) return A2BCompletionReason.Invalid;
            return await handle.ToUniTask(cancellationToken);
        }

        /// <summary>Cancels every effect on the shared runner.</summary>
        public static void CancelAll()
        {
            if (A2BRunner.Exists) A2BRunner.Scheduler.CancelAll();
        }

        private static IA2BEndpointProvider MakeEndpoint(Transform t)
            => t is RectTransform rect ? new A2BRectTransformEndpoint(rect) : (IA2BEndpointProvider)new A2BTransformEndpoint(t);

        private static A2BPresenter ResolvePresenter(A2BEffectSpec spec, Transform destination)
        {
            Canvas hostCanvas = spec.Space == A2BSpaceKind.Canvas && destination != null
                ? destination.GetComponentInParent<Canvas>()
                : null;

            // Owner, falling back to the spec itself: two plays of one asset share a pool, while a
            // caller who never set Owner still gets one pool per spec rather than one per play.
            object owner = spec.Owner ?? spec;

            var key = new PresenterKey(owner, spec.Space, hostCanvas);
            if (_presenters.TryGetValue(key, out A2BPresenter cached) && cached != null && cached.Adapter.Root != null)
                return cached;

            // Through the registry, never a local switch: a hard-coded switch here is exactly what
            // made FR-6's "register a custom Space Adapter" untrue from the public entry points.
            var context = new A2BSpaceContext(spec.Space, hostCanvas, null, destination, null);
            IA2BSpaceAdapter adapter = A2BAdapters.Resolve(in context, spec.SpaceOverride);

            var presenter = new A2BPresenter(adapter, spec.Payload, spec.Feedbacks, spec.Definition.PrewarmCount);
            _presenters[key] = presenter;
            return presenter;
        }

        private static Transform NewRoot(string rootName)
        {
            var go = new GameObject(rootName);

            // Guarded: DontDestroyOnLoad throws outside play mode (see A2BRunner) — an editor tool
            // calling A2B.Play() must not blow up on scaffolding. DontSave is the edit-mode half only:
            // in play mode it would keep the root alive past the session (see A2BCanvasPool).
            if (Application.isPlaying) Object.DontDestroyOnLoad(go);
            else go.hideFlags = HideFlags.DontSave;

            return go.transform;
        }

        /// <summary>
        /// Pool identity (AD-14). The Space term is not optional: without it a Canvas sprite would
        /// return to the pool carrying a RectTransform under a Canvas and then be handed to a
        /// World3D effect.
        /// </summary>
        private readonly struct PresenterKey : System.IEquatable<PresenterKey>
        {
            // object, not A2BEffectAsset: a code-built spec has no asset, and keying on one would
            // have forced every code-first effect into a single shared bucket.
            private readonly object _owner;
            private readonly A2BSpaceKind _space;
            private readonly Canvas _canvas;

            public PresenterKey(object owner, A2BSpaceKind space, Canvas canvas)
            {
                _owner = owner;
                _space = space;
                _canvas = canvas;
            }

            public bool Equals(PresenterKey other)
                => ReferenceEquals(_owner, other._owner) && _space == other._space && _canvas == other._canvas;

            public override bool Equals(object obj) => obj is PresenterKey other && Equals(other);

            public override int GetHashCode()
            {
                unchecked
                {
                    // Object.GetHashCode(), not GetInstanceID(): Unity 6000.5 deprecates
                    // GetInstanceID() in favour of GetEntityId(), but GetEntityId does not exist on
                    // the 6000.0 floor this package claims in package.json. GetHashCode is stable
                    // for a live UnityEngine.Object and works across the whole range.
                    int hash = _owner != null ? _owner.GetHashCode() : 0;
                    hash = (hash * 397) ^ (int)_space;
                    hash = (hash * 397) ^ (_canvas != null ? _canvas.GetHashCode() : 0);
                    return hash;
                }
            }
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics() => _presenters.Clear();
    }
}
