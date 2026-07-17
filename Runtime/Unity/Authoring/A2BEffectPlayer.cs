using System;
using System.Threading;
using A2BKit.Core;
using Cysharp.Threading.Tasks;
using UnityEngine;
using UnityEngine.Events;

namespace A2BKit.Unity
{
    /// <summary>
    /// Drop-on component: point it at an asset, an origin and a destination, call Play().
    /// This is the five-minute path (SM-3) — no code required for the common coin-to-wallet case.
    ///
    /// Bridges to UnityEvents for designers. Note the honest cost: UnityEvent dispatch is not
    /// allocation-free, so this component is the convenience path (AD-11). Code wanting the
    /// zero-allocation guarantee registers an IA2BEffectListener on the handle directly.
    /// </summary>
    [AddComponentMenu("A2BKit/A2B Effect Player")]
    public sealed class A2BEffectPlayer : MonoBehaviour, IA2BEffectListener
    {
        [Header("Effect")]
        public A2BEffectAsset Effect;

        [Header("Endpoints")]
        [Tooltip("Where items spawn. Defaults to this GameObject when left empty.")]
        public Transform Origin;

        [Tooltip("Where items fly to. A RectTransform here is treated as a UI target.")]
        public Transform Destination;

        [Header("Canvas space")]
        [Tooltip("Canvas the items are parented to. Left empty, A2BKit creates its own — which keeps " +
                 "moving items from dirtying your HUD canvas every frame (AD-14).")]
        public RectTransform CanvasRoot;

        [Tooltip("Camera used to project world origins onto the canvas. Defaults to Camera.main.")]
        public Camera SourceCamera;

        [Header("Behaviour")]
        [Tooltip("Play automatically when this component is enabled.")]
        public bool PlayOnEnable;

        // Each event is constructed here, not left to the serializer. Unity fills these in when the
        // component is deserialized from a scene, but a component created in CODE
        // (`AddComponent<A2BEffectPlayer>()`) gets nulls — so `player.OnItemArrivedEvent.AddListener(…)`
        // threw a NullReferenceException on the very first line of the code-first path. An initializer
        // costs nothing and Unity's serializer overwrites it with the authored value anyway.
        [Header("Events")]
        public UnityEvent OnStartedEvent = new UnityEvent();

        [Tooltip("Fires when the FIRST item lands. This is the one that should start a counter roll-up.")]
        public UnityEvent OnFirstItemArrivedEvent = new UnityEvent();

        public UnityEvent OnItemArrivedEvent = new UnityEvent();
        public UnityEvent OnCompletedEvent = new UnityEvent();
        public UnityEvent OnCancelledEvent = new UnityEvent();

        private A2BPresenter _presenter;
        private A2BEffectHandle _handle;

        /// <summary>The currently running effect, if any.</summary>
        public A2BEffectHandle Handle => _handle;
        public bool IsPlaying => _handle.IsValid;

        private void OnEnable()
        {
            if (PlayOnEnable) Play();
        }

        private void OnDisable()
        {
            // Never leave items in flight owned by a disabled component (AD-9).
            _handle.Cancel();
        }

        private void OnDestroy()
        {
            _handle.Cancel();
            _presenter?.Dispose();
            _presenter = null;
        }

        /// <summary>Plays the effect. Returns an invalid handle on misconfiguration — never throws (AD-8).</summary>
        public A2BEffectHandle Play() => Play(null, 0f);

        /// <summary>Plays with per-item text (e.g. "+250") for text payloads.</summary>
        public A2BEffectHandle Play(string text) => Play(text, 0f);

        /// <summary>Plays with a numeric value; text payloads format it without allocating (AD-20).</summary>
        public A2BEffectHandle Play(float value) => Play(null, value);

        public A2BEffectHandle Play(string text, float value)
        {
            if (Effect == null)
            {
                A2BLog.Error(this, "Play failed: no A2BEffectAsset assigned.");
                return A2BEffectHandle.Invalid;
            }
            if (!Effect.Validate(out string error))
            {
                A2BLog.Error(Effect, "Play failed: " + error);
                return A2BEffectHandle.Invalid;
            }
            if (Destination == null)
            {
                A2BLog.Error(this, "Play failed: no destination assigned.");
                return A2BEffectHandle.Invalid;
            }

            EnsurePresenter();
            if (_presenter == null) return A2BEffectHandle.Invalid;

            var args = new A2BPlayArgs(
                origin: BuildEndpoint(Origin != null ? Origin : transform),
                destination: BuildEndpoint(Destination),
                presenter: _presenter,
                text: text,
                value: value,
                cancellationToken: destroyCancellationToken);

            _handle = A2BRunner.Scheduler.Play(Effect.Definition, in args, this);
            _handle.AddListener(this);
            return _handle;
        }

        /// <summary>
        /// Plays and awaits completion. Cancels cleanly when this GameObject is destroyed, because
        /// destroyCancellationToken is wired in at Play (FR-15).
        /// </summary>
        public async UniTask<A2BCompletionReason> PlayAsync(
            string text = null, float value = 0f, CancellationToken cancellationToken = default)
        {
            A2BEffectHandle handle = Play(text, value);
            if (!handle.IsValid) return A2BCompletionReason.Invalid;
            return await handle.ToUniTask(cancellationToken);
        }

        public void Cancel() => _handle.Cancel();

        private static IA2BEndpointProvider BuildEndpoint(Transform t)
        {
            if (t is RectTransform rect) return new A2BRectTransformEndpoint(rect);
            return new A2BTransformEndpoint(t);
        }

        private void EnsurePresenter()
        {
            if (_presenter != null) return;

            IA2BSpaceAdapter adapter = BuildAdapter();
            if (adapter == null || adapter.Root == null)
            {
                A2BLog.Error(this, "Play failed: could not build a space adapter root.");
                return;
            }

            _presenter = new A2BPresenter(adapter, Effect.Payload, Effect.Feedbacks, Effect.Definition.PrewarmCount);
        }

        /// <summary>
        /// Resolves the adapter through <see cref="A2BAdapters"/> rather than a local switch, so a
        /// custom space adapter reaches this component too (FR-6) — via the asset's SpaceOverride or
        /// a globally registered factory.
        /// </summary>
        private IA2BSpaceAdapter BuildAdapter()
        {
            Canvas hostCanvas = Effect.Space == A2BSpaceKind.Canvas && Destination != null
                ? Destination.GetComponentInParent<Canvas>()
                : null;

            Transform rootOverride = Effect.Space == A2BSpaceKind.Canvas && CanvasRoot != null
                ? CanvasRoot
                : null;

            var context = new A2BSpaceContext(Effect.Space, hostCanvas, SourceCamera, Destination, rootOverride);
            return A2BAdapters.Resolve(in context, Effect.SpaceOverride);
        }

        // ---- IA2BEffectListener: forwards to the designer-facing UnityEvents -------------------

        void IA2BEffectListener.OnStarted(in A2BEffectHandle handle) => OnStartedEvent?.Invoke();
        void IA2BEffectListener.OnItemSpawned(in A2BEffectHandle handle, int itemIndex) { }
        void IA2BEffectListener.OnFirstItemArrived(in A2BEffectHandle handle, int itemIndex) => OnFirstItemArrivedEvent?.Invoke();
        void IA2BEffectListener.OnItemArrived(in A2BEffectHandle handle, int itemIndex) => OnItemArrivedEvent?.Invoke();
        void IA2BEffectListener.OnCompleted(in A2BEffectHandle handle) => OnCompletedEvent?.Invoke();
        void IA2BEffectListener.OnCancelled(in A2BEffectHandle handle, A2BCompletionReason reason) => OnCancelledEvent?.Invoke();
    }
}
