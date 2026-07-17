using System.Collections.Generic;
using A2BKit.Core;
using A2BKit.Unity;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace A2BKit.Editor
{
    /// <summary>
    /// Plays an effect in the editor, without entering play mode (FR-21).
    ///
    /// Two decisions make this trustworthy rather than a lookalike animation:
    ///
    /// 1. It runs a real <see cref="A2BScheduler"/>, driven by a real
    ///    <see cref="A2BManualTimeSource"/> — the IDENTICAL seam the EditMode tests drive (AD-12).
    ///    The scheduler cannot tell it is being previewed. Everything the designer sees — stagger,
    ///    jitter, easing, scatter, arrival order — is produced by the shipping code path, so preview
    ///    cannot drift from runtime unless runtime drifts from itself.
    ///
    /// 2. It creates NO GameObjects. The presenter below records visual state into a list and the
    ///    scene view draws it with Handles (see <see cref="A2BEffectGizmos"/>). This is the whole
    ///    answer to FR-21/NFR-5's "must not leak preview objects": teardown cannot miss an object,
    ///    because no object was ever made. A preview built the obvious way — instantiating the real
    ///    payload — leaks a hidden coin into the user's scene the first time a domain reload lands
    ///    mid-flight, and that coin gets saved into their scene file.
    ///
    /// A consequence worth naming: the preview needs no payload renderer, so an asset with a null
    /// Payload still previews its motion. Only <see cref="A2BEffectDefinition.Validate"/> gates it.
    /// The preview's working space is world space, matching what the scene view draws; a Canvas-space
    /// effect previews its trajectory in the scene, not its canvas projection.
    /// </summary>
    [InitializeOnLoad]
    public static class A2BEffectPreview
    {
        /// <summary>
        /// Caps a single step. The editor stalls for whole seconds on compiles and asset imports, and
        /// the resulting delta would advance every item straight past t=1 — the preview would appear
        /// to "finish instantly" whenever the user touched a script. Runtime never sees this because
        /// it never stalls like an editor does, so clamping here does not diverge from it.
        /// </summary>
        private const float MaxStep = 0.1f;

        /// <summary>Fixed so a replay reproduces the previous run exactly (NFR-4, AD-10).</summary>
        private const uint PreviewSeed = A2BEffectGizmos.GizmoSeed;

        private static readonly A2BScheduler Scheduler = new A2BScheduler();
        private static readonly A2BManualTimeSource Clock = new A2BManualTimeSource();
        private static readonly PreviewPresenter Presenter = new PreviewPresenter();

        private static A2BEffectHandle _handle;
        private static IA2BEndpointProvider _origin;
        private static IA2BEndpointProvider _destination;
        private static double _lastUpdateTime;
        private static bool _subscribed;

        /// <summary>True while the preview is animating.</summary>
        public static bool IsPlaying { get; private set; }

        /// <summary>The asset being previewed, or null.</summary>
        public static A2BEffectAsset Asset { get; private set; }

        /// <summary>
        /// The object the preview was started from (typically an <see cref="A2BEffectPlayer"/>), so
        /// the gizmo only overlays items on the player they belong to. May be null.
        /// </summary>
        public static Object Context { get; private set; }

        /// <summary>Simulated seconds since the current run started.</summary>
        public static float Elapsed => Clock.Elapsed;

        /// <summary>Restart on completion. On by default: a burst is over in under a second, and a
        /// designer tuning stagger needs to see it repeat, not click Play forty times.</summary>
        public static bool Loop { get; set; } = true;

        /// <summary>
        /// Registers teardown against every event that can invalidate a running preview.
        ///
        /// Each subscription closes a specific leak, not a hypothetical one: a domain reload wipes
        /// the statics below while <c>EditorApplication.update</c> keeps a live delegate to a method
        /// whose backing state is gone; a scene change leaves the endpoint Transforms destroyed and
        /// the preview ticking against fake-null; entering play mode has the runtime scheduler and
        /// this one both driving items. Stop() is idempotent, so overlapping triggers are harmless.
        /// </summary>
        static A2BEffectPreview()
        {
            AssemblyReloadEvents.beforeAssemblyReload += Stop;
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
            EditorApplication.quitting += Stop;
            EditorSceneManager.sceneOpened += OnSceneOpened;
            EditorSceneManager.sceneClosed += OnSceneClosed;
        }

        /// <summary>Previews between two Transforms, following them live as the user drags them.</summary>
        public static bool Play(A2BEffectAsset asset, Transform origin, Transform destination, Object context = null)
        {
            if (origin == null || destination == null) return false;
            return StartInternal(
                asset,
                new A2BTransformEndpoint(origin),
                destination is RectTransform rect
                    ? (IA2BEndpointProvider)new A2BRectTransformEndpoint(rect)
                    : new A2BTransformEndpoint(destination),
                context);
        }

        /// <summary>Previews between two fixed world points.</summary>
        public static bool Play(A2BEffectAsset asset, Vector3 origin, Vector3 destination, Object context = null)
            => StartInternal(asset, new A2BWorldPointEndpoint(origin), new A2BWorldPointEndpoint(destination), context);

        private static bool StartInternal(
            A2BEffectAsset asset, IA2BEndpointProvider origin, IA2BEndpointProvider destination, Object context)
        {
            Stop();

            if (asset == null || asset.Definition == null) return false;

            // Bad config reports and declines; it never throws at the caller, who is an inspector
            // button (AD-8). The asset editor shows the same error inline via Validate.
            if (!asset.Definition.Validate(out string error))
            {
                // Through A2BLog, not Debug: the message names the asset and clicking it selects the
                // culprit (FR-23). Interpolating here is fine — this is a button press, not a tick.
                A2BLog.Warn(asset, "Cannot preview '" + asset.name + "': " + error);
                return false;
            }

            Presenter.Clear();
            Clock.Reset();

            Asset = asset;
            Context = context;
            _origin = origin;
            _destination = destination;

            if (!Restart()) { Stop(); return false; }

            IsPlaying = true;
            _lastUpdateTime = EditorApplication.timeSinceStartup;
            Subscribe();
            SceneView.RepaintAll();
            return true;
        }

        private static bool Restart()
        {
            Presenter.Clear();
            Clock.Reset();

            var args = new A2BPlayArgs(_origin, _destination, Presenter, null, 0f, PreviewSeed);
            _handle = Scheduler.Play(Asset.Definition, in args, Asset);
            if (!_handle.IsValid) return false;

            // The seam (AD-12). Without this the slot would pull from the scaled/unscaled Time
            // source, which reads zero outside play mode — the preview would sit frozen and look
            // broken rather than being obviously unhooked.
            Scheduler.SetTimeSource(in _handle, Clock);
            return true;
        }

        /// <summary>
        /// Tears the preview down completely. Idempotent, and safe to call when nothing is playing —
        /// which is what lets every teardown hook call it unconditionally.
        /// </summary>
        public static void Stop()
        {
            bool wasPlaying = IsPlaying;

            Unsubscribe();

            _handle.Cancel();
            _handle = A2BEffectHandle.Invalid;

            // Belt and braces: Cancel above releases this run's items, CancelAll catches anything a
            // failed start left half-registered. Both funnel through the scheduler's single exit
            // (AD-9), so items are returned exactly once.
            Scheduler.CancelAll();

            Presenter.Clear();
            Clock.Reset();

            IsPlaying = false;
            Asset = null;
            Context = null;
            _origin = null;
            _destination = null;

            if (wasPlaying) SceneView.RepaintAll();
        }

        private static void Subscribe()
        {
            if (_subscribed) return;
            EditorApplication.update += OnEditorUpdate;
            _subscribed = true;
        }

        private static void Unsubscribe()
        {
            if (!_subscribed) return;
            EditorApplication.update -= OnEditorUpdate;
            _subscribed = false;
        }

        /// <summary>
        /// One preview frame. This is the editor's stand-in for <c>A2BRunner.LateUpdate</c> — it
        /// advances the clock and ticks, and does nothing else the runtime does not do.
        /// </summary>
        private static void OnEditorUpdate()
        {
            if (!IsPlaying || Asset == null)
            {
                Stop();
                return;
            }

            double now = EditorApplication.timeSinceStartup;
            float dt = Mathf.Clamp((float)(now - _lastUpdateTime), 0f, MaxStep);
            _lastUpdateTime = now;

            Clock.Advance(dt);
            Scheduler.Tick();

            if (!_handle.IsValid)
            {
                // The run ended: completed, or cancelled because an endpoint Transform was deleted
                // under us (FR-13). Either way the loop must not restart against a dead endpoint.
                if (!Loop || !Restart())
                {
                    Stop();
                    return;
                }
            }

            SceneView.RepaintAll();
        }

        private static void OnPlayModeStateChanged(PlayModeStateChange change) => Stop();

        private static void OnSceneOpened(Scene scene, OpenSceneMode mode) => Stop();

        private static void OnSceneClosed(Scene scene) => Stop();

        /// <summary>Copies the currently-live items for drawing. Clears <paramref name="results"/> first.</summary>
        internal static void CopyActiveItems(List<A2BVisualState> results) => Presenter.CopyActiveItems(results);

        /// <summary>Items currently in flight in the preview.</summary>
        public static int ActiveItemCount => Presenter.ActiveCount;

        /// <summary>
        /// A presenter that draws nothing and owns nothing (AD-18).
        ///
        /// Core speaks to presentation only in item ids and value structs, never Transforms — which
        /// is exactly what makes this possible: the preview satisfies the full port with a list.
        /// The same property is why Core's tests need no scene, so this class is that test stub
        /// grown a public face rather than a parallel invention.
        /// </summary>
        private sealed class PreviewPresenter : IA2BPresenter
        {
            private readonly List<A2BVisualState> _states = new List<A2BVisualState>(64);
            private readonly List<bool> _active = new List<bool>(64);
            private readonly Stack<int> _free = new Stack<int>(64);

            public int ActiveCount
            {
                get
                {
                    int count = 0;
                    for (int i = 0; i < _active.Count; i++)
                        if (_active[i]) count++;
                    return count;
                }
            }

            public int Acquire(in A2BItemSpawnInfo info)
            {
                if (_free.Count > 0)
                {
                    int reused = _free.Pop();
                    _active[reused] = true;
                    _states[reused] = default;
                    return reused;
                }

                _states.Add(default);
                _active.Add(true);
                return _states.Count - 1;
            }

            public void Apply(int itemId, in A2BVisualState state)
            {
                if (!IsLive(itemId)) return;
                _states[itemId] = state;
            }

            public void Release(int itemId, A2BReleaseReason reason)
            {
                // The port requires tolerating a release for an id already released (AD-9's single
                // exit re-releases defensively); pushing it twice would hand one id to two items.
                if (!IsLive(itemId)) return;
                _active[itemId] = false;
                _free.Push(itemId);
            }

            /// <summary>Preview works in world space, so a unit offset scales straight to units (AD-16).</summary>
            public Vector3 ScaleScatter(in Vector3 unitOffset, float radius) => unitOffset * radius;

            /// <summary>The preview's working space IS world space — the space the scene view draws in.</summary>
            // Preview runs in an identity working space — it draws the path, it does not host a canvas.
            public Vector3 ToWorkingSpace(in A2BEndpointSample sample) => sample.Position;

            public void CopyActiveItems(List<A2BVisualState> results)
            {
                results.Clear();
                for (int i = 0; i < _states.Count; i++)
                    if (_active[i]) results.Add(_states[i]);
            }

            public void Clear()
            {
                _states.Clear();
                _active.Clear();
                _free.Clear();
            }

            private bool IsLive(int itemId) => itemId >= 0 && itemId < _active.Count && _active[itemId];
        }
    }
}
