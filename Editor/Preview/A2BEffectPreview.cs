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
    /// 2. By default it creates NO GameObjects. The recording presenter below records visual state into
    ///    a list and the scene view draws it with Handles (see <see cref="A2BEffectGizmos"/>). This is
    ///    the safe default for the selected-player gizmo: teardown cannot miss an object, because no
    ///    object was ever made.
    ///
    /// The default previews MOTION (dots on the path), not the actual sprite/mesh. The visual editor
    /// asks for more — the real coin, in both the Scene and the Game view, with no play mode — so it
    /// opts into a REAL-PAYLOAD mode (pass <c>renderRealPayload: true</c> to <see cref="Begin"/>). That
    /// mode builds the shipping <see cref="A2BPresenter"/> (space adapter + pooled payload renderer +
    /// feedbacks), so genuine renderers draw the effect in both views.
    ///
    /// Real-payload mode still cannot leak, because the preview OWNS its stage: a single root created
    /// with <see cref="HideFlags.DontSave"/> (so it is never serialized into the user's scene), passed
    /// to the adapter as its root override, and destroyed whole on every teardown path via
    /// <see cref="DestroyPreviewRoot"/>. The pooled renderers already tear down with DestroyImmediate
    /// outside play mode by design, so the objects they make die with the root, not on the next reload.
    ///
    /// A consequence worth naming: the recording mode needs no payload renderer, so an asset with a
    /// null Payload still previews its motion. Only <see cref="A2BEffectDefinition.Validate"/> gates it.
    /// The recording mode's working space is world space, matching what the scene view draws; a
    /// Canvas-space effect previews its trajectory in the scene, not its canvas projection. Real-payload
    /// mode uses the actual adapter, so a Canvas effect renders on a real overlay canvas instead.
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
        private static bool _paused;

        // Real-payload mode. The presenter the scheduler drives is whichever ActivePresenter points at:
        // the recording Presenter (motion dots) or _realPresenter (actual pooled renderers). _previewRoot
        // is the self-owned DontSave stage the real presenter's objects hang under, torn down whole.
        private static IA2BPresenter ActivePresenter;
        private static A2BPresenter _realPresenter;
        private static GameObject _previewRoot;
        private static bool _renderReal;

        /// <summary>True while a preview SESSION exists — whether it is animating, paused, or scrubbed.</summary>
        public static bool IsPlaying { get; private set; }

        /// <summary>True when a session exists but the clock is not advancing (paused or scrubbed).</summary>
        public static bool Paused => _paused;

        /// <summary>Playback rate for auto-advance. 1 = real time. The scrub path ignores it.</summary>
        public static float Speed = 1f;

        /// <summary>
        /// Total simulated seconds the current effect runs for, end to end — what the editor timeline
        /// maps its scrub bar onto. Zero when nothing is loaded.
        /// </summary>
        public static float Span =>
            Asset != null && Asset.Definition != null ? Asset.Definition.ResolveSpan(PreviewSeed) : 0f;

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
            ActivePresenter = Presenter;
            AssemblyReloadEvents.beforeAssemblyReload += Stop;
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
            EditorApplication.quitting += Stop;
            EditorSceneManager.sceneOpened += OnSceneOpened;
            EditorSceneManager.sceneClosed += OnSceneClosed;
        }

        /// <summary>True when the current session is drawing the actual pooled payload, not motion dots.</summary>
        public static bool IsRenderingRealPayload => _renderReal && _realPresenter != null;

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
                context,
                renderReal: false);
        }

        /// <summary>Previews between two fixed world points.</summary>
        public static bool Play(A2BEffectAsset asset, Vector3 origin, Vector3 destination, Object context = null)
            => StartInternal(asset, new A2BWorldPointEndpoint(origin), new A2BWorldPointEndpoint(destination),
                context, renderReal: false);

        /// <summary>
        /// Starts a session from arbitrary endpoint providers — the entry the visual editor uses so it
        /// can feed live, draggable virtual points as well as scene Transforms. Starts playing; the
        /// caller pauses or scrubs from there.
        ///
        /// <paramref name="renderRealPayload"/> switches on real-payload mode: the actual sprite/mesh
        /// draws in the Scene AND Game views (no play mode), instead of motion dots. It falls back to
        /// motion dots if the asset has no payload or the presenter cannot be built.
        /// </summary>
        public static bool Begin(
            A2BEffectAsset asset, IA2BEndpointProvider origin, IA2BEndpointProvider destination,
            Object context = null, bool renderRealPayload = false)
            => StartInternal(asset, origin, destination, context, renderRealPayload);

        /// <summary>
        /// Pauses or resumes auto-advance without tearing the session down, so the scrub bar can hold
        /// a frame. Resuming re-anchors the wall-clock so the held time does not arrive as one huge
        /// delta and jump the effect to its end.
        /// </summary>
        public static void SetPaused(bool paused)
        {
            if (Asset == null) return;
            _paused = paused;
            if (!paused) _lastUpdateTime = EditorApplication.timeSinceStartup;
            RepaintViews();
        }

        /// <summary>
        /// Shows the effect exactly as it looks <paramref name="seconds"/> into its run, and holds
        /// there. It re-simulates from zero in fixed 1/60 steps — the scheduler is forward-only and
        /// stateful, so "jump to t" means "replay to t" — which is why the result is the real thing
        /// rather than an interpolation, and why it is deterministic (the seed is fixed).
        /// </summary>
        public static void Scrub(float seconds)
        {
            if (Asset == null) return;
            if (!Restart()) { Stop(); return; }

            _paused = true;
            seconds = Mathf.Max(0f, seconds);

            const float step = 1f / 60f;
            int guard = 0;
            while (Clock.Elapsed < seconds && _handle.IsValid && guard++ < 200000)
            {
                float dt = Mathf.Min(step, seconds - Clock.Elapsed);
                if (dt <= 0f) break;
                Clock.Advance(dt);
                Scheduler.Tick();
            }

            RepaintViews();
        }

        private static bool StartInternal(
            A2BEffectAsset asset, IA2BEndpointProvider origin, IA2BEndpointProvider destination,
            Object context, bool renderReal)
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

            // Pick the presenter for this session. Real-payload mode needs a payload; without one it
            // falls back to motion dots rather than declining, so a definition can always be previewed.
            ActivePresenter = Presenter;
            _renderReal = false;
            if (renderReal && asset.Payload != null && TryBuildRealPresenter(asset))
            {
                ActivePresenter = _realPresenter;
                _renderReal = true;
            }

            if (!Restart()) { Stop(); return false; }

            IsPlaying = true;
            _paused = false;
            _lastUpdateTime = EditorApplication.timeSinceStartup;
            Subscribe();
            RepaintViews();
            return true;
        }

        /// <summary>
        /// Builds the shipping presenter over a self-owned <see cref="HideFlags.DontSave"/> root, so the
        /// real payload draws in both views yet the whole stage is one object to destroy on teardown.
        /// The adapter is resolved through the same registry the runtime uses (FR-6), given our root as
        /// its override so nothing lands under the runtime's shared, session-persistent adapter roots.
        /// </summary>
        private static bool TryBuildRealPresenter(A2BEffectAsset asset)
        {
            try
            {
                Transform rootOverride = CreatePreviewRoot(asset.Space);
                var ctx = new A2BSpaceContext(asset.Space, null, null, null, rootOverride);
                IA2BSpaceAdapter adapter = A2BAdapters.Resolve(in ctx, asset.SpaceOverride);
                if (adapter == null) { DestroyPreviewRoot(); return false; }

                int prewarm = Mathf.Max(0, asset.Definition.PrewarmCount);
                _realPresenter = new A2BPresenter(adapter, asset.Payload, asset.Feedbacks, prewarm);
                return true;
            }
            catch (System.Exception e)
            {
                // Real-payload preview is a convenience; a failure here must degrade to motion dots,
                // never take the editor down (AD-8).
                A2BLog.Exception(asset, e);
                DestroyPreviewRoot();
                _realPresenter = null;
                return false;
            }
        }

        /// <summary>
        /// Creates the DontSave stage the real payload hangs under.
        ///
        /// A Canvas effect needs a screen-space overlay canvas so its uGUI items render in the Game view
        /// exactly where a HUD would — the whole point of the "no play mode" preview. It must be an
        /// OVERLAY, not a world-space canvas: a game camera is usually framed on the world (here an
        /// orthographic size-5 camera at the origin), so a world-space canvas placed out at the HUD's
        /// pixel coordinates would fall outside the frame and show nothing. The endpoints are fed as
        /// SCREEN samples so the adapter maps them to this overlay without re-projecting (see the window
        /// and <see cref="A2BEndpointSample.AtScreen"/>). ForceUpdateCanvases lays the rect out now, so a
        /// scrub taken the instant after creation resolves against a real size rather than a zero rect.
        ///
        /// Every other space just needs a plain Transform. DontSave, not HideAndDontSave, so the
        /// transient objects stay visible in the hierarchy while previewing and vanish on Stop — matching
        /// the runtime roots.
        /// </summary>
        private static Transform CreatePreviewRoot(A2BSpaceKind space)
        {
            if (space == A2BSpaceKind.Canvas)
            {
                _previewRoot = new GameObject("[A2BKit Preview Canvas]", typeof(RectTransform), typeof(Canvas));
                _previewRoot.hideFlags = HideFlags.DontSave;

                var canvas = _previewRoot.GetComponent<Canvas>();
                canvas.renderMode = RenderMode.ScreenSpaceOverlay;
                canvas.sortingOrder = 200;   // above a game's HUD, like the runtime canvas pool

                var rect = (RectTransform)_previewRoot.transform;
                rect.anchorMin = Vector2.zero;
                rect.anchorMax = Vector2.one;
                rect.offsetMin = Vector2.zero;
                rect.offsetMax = Vector2.zero;

                // Force layout so the overlay rect has its screen size immediately — a scrub runs
                // synchronously right after this and would otherwise map against a zero-sized rect.
                Canvas.ForceUpdateCanvases();
                return rect;
            }

            _previewRoot = new GameObject("[A2BKit Preview Root]");
            _previewRoot.hideFlags = HideFlags.DontSave;
            return _previewRoot.transform;
        }

        /// <summary>
        /// Repaints the views the preview draws into. The Scene view always, and — in real-payload mode
        /// — every view, because the Game view does NOT re-render on its own in edit mode: without this
        /// the real items would be correctly positioned but the Game view would show a stale frame, which
        /// reads as "the preview does not work". Dot mode only draws Handles in the Scene view, so it does
        /// not pay for the heavier all-views repaint.
        /// </summary>
        private static void RepaintViews()
        {
            if (_renderReal)
                UnityEditorInternal.InternalEditorUtility.RepaintAllViews();
            else
                SceneView.RepaintAll();
        }

        private static void DestroyPreviewRoot()
        {
            if (_previewRoot == null) return;

            // Same reason the pooled renderers use DestroyImmediate outside play mode: a deferred
            // Destroy waits for a frame that an editor tick does not guarantee, and the object survives.
            if (Application.isPlaying) Object.Destroy(_previewRoot);
            else Object.DestroyImmediate(_previewRoot);
            _previewRoot = null;
        }

        private static bool Restart()
        {
            // Release the previous run's items BEFORE replaying. In real-payload mode a scrub replays
            // while items may still be live; without this cancel their pooled objects would pile up as
            // ghosts. Harmless when the handle is already invalid (a completed loop) or in dot mode.
            _handle.Cancel();

            if (ActivePresenter == Presenter) Presenter.Clear();
            Clock.Reset();

            var args = new A2BPlayArgs(_origin, _destination, ActivePresenter, null, 0f, PreviewSeed);
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

            // Real-payload teardown: dispose the presenter (pools, feedbacks) then destroy the stage.
            // Dispose returns objects the disciplined way; destroying the root then guarantees nothing
            // the adapter itself created (the overlay canvas) outlives the session.
            if (_realPresenter != null)
            {
                _realPresenter.Dispose();
                _realPresenter = null;
            }
            DestroyPreviewRoot();

            Presenter.Clear();
            Clock.Reset();

            ActivePresenter = Presenter;
            _renderReal = false;
            IsPlaying = false;
            _paused = false;
            Asset = null;
            Context = null;
            _origin = null;
            _destination = null;

            if (wasPlaying) RepaintViews();
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

            // Paused/scrubbed: keep the session alive and the drawn frame frozen, but do not advance.
            // Re-anchor the wall-clock each frame so that when playback resumes the held interval is
            // NOT delivered as one accumulated delta that jumps the effect to its end.
            if (_paused)
            {
                _lastUpdateTime = now;
                return;
            }

            float dt = Mathf.Clamp((float)(now - _lastUpdateTime), 0f, MaxStep);
            _lastUpdateTime = now;

            Clock.Advance(dt * Mathf.Max(0f, Speed));
            Scheduler.Tick();
            StepPreviewParticles(dt * Mathf.Max(0f, Speed));

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

            RepaintViews();
        }

        /// <summary>
        /// Advances any pooled <see cref="ParticleSystem"/> payloads by the frame delta.
        ///
        /// Particle systems do not simulate in edit mode — the player loop that would step them is not
        /// running — so a particle payload played through <c>ParticleSystem.Play</c> just sits there and
        /// the designer sees nothing move. Stepping each active system with restart:false keeps them in
        /// lockstep with the preview clock, which is what makes a particle burst visible without play
        /// mode. Runtime is untouched: this only runs from the editor tick, on the preview's own objects.
        /// </summary>
        private static void StepPreviewParticles(float dt)
        {
            if (!_renderReal || _previewRoot == null || dt <= 0f) return;

            // includeInactive:false skips the pool root (it is SetActive(false)), so only live items step.
            var systems = _previewRoot.GetComponentsInChildren<ParticleSystem>(false);
            for (int i = 0; i < systems.Length; i++)
            {
                ParticleSystem ps = systems[i];
                if (ps == null) continue;
                // withChildren:false because the walk already returned every child system individually;
                // restart:false to advance from the current state rather than replaying from zero.
                ps.Simulate(dt, false, false, false);
            }
        }

        private static void OnPlayModeStateChanged(PlayModeStateChange change) => Stop();

        private static void OnSceneOpened(Scene scene, OpenSceneMode mode) => Stop();

        private static void OnSceneClosed(Scene scene) => Stop();

        /// <summary>Copies the currently-live items for drawing. Clears <paramref name="results"/> first.</summary>
        internal static void CopyActiveItems(List<A2BVisualState> results) => Presenter.CopyActiveItems(results);

        /// <summary>Items currently in flight in the preview, whichever presenter is driving it.</summary>
        public static int ActiveItemCount =>
            IsRenderingRealPayload ? _realPresenter.ActiveItemCount : Presenter.ActiveCount;

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
