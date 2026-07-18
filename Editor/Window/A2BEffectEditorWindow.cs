using System;
using System.Collections.Generic;
using A2BKit.Core;
using A2BKit.Unity;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

namespace A2BKit.Editor
{
    /// <summary>
    /// A visual, in-scene editor for a single <see cref="A2BEffectAsset"/> (FR-20/FR-21).
    ///
    /// It does three things the inspector and the selected-player gizmo cannot do together:
    ///
    /// 1. Previews the effect WITHOUT play mode and lets you scrub its timeline. Both are the real
    ///    simulation — this window only drives <see cref="A2BEffectPreview"/>, which runs the shipping
    ///    scheduler on an injected clock (AD-12). Scrubbing to t re-simulates from zero to t, because
    ///    the scheduler is forward-only; the frame you see is therefore the frame the game would show.
    ///
    /// 2. Draws the path and the live items in the scene with no <see cref="A2BEffectPlayer"/> present.
    ///    The DrawGizmo path in <see cref="A2BEffectGizmos"/> only fires for a selected player, so this
    ///    window paints its own <see cref="SceneView.duringSceneGui"/> overlay — reusing that class's
    ///    sampler and palette so the two never diverge.
    ///
    /// 3. Lets you set the two endpoints either as scene Transforms (pick, or "Use Selection") or as
    ///    virtual points you drag with a scene handle — so an effect can be tuned before the objects it
    ///    will fire between exist.
    ///
    /// It owns the preview only while open and only while the preview's Context is this window, so it
    /// never fights the player gizmo's own preview, and it stops cleanly on close (NFR-5).
    /// </summary>
    public sealed class A2BEffectEditorWindow : EditorWindow
    {
        private enum EndpointMode
        {
            SceneObject,
            Virtual
        }

        [SerializeField] private A2BEffectAsset _asset;
        [SerializeField] private EndpointMode _originMode = EndpointMode.Virtual;
        [SerializeField] private EndpointMode _destinationMode = EndpointMode.Virtual;
        [SerializeField] private Transform _originObject;
        [SerializeField] private Transform _destinationObject;
        [SerializeField] private Vector3 _originPoint = Vector3.zero;
        [SerializeField] private Vector3 _destinationPoint = new Vector3(3f, 0f, 0f);
        [SerializeField] private bool _loop = true;
        [SerializeField] private float _speed = 1f;
        [SerializeField] private bool _showPayloadVisuals = true;
        [SerializeField] private bool _trailAdvanced;

        // Per-module fold state, like the Particle System inspector — each module remembers open/closed.
        [SerializeField] private bool _mTiming = true;
        [SerializeField] private bool _mPath = true;
        [SerializeField] private bool _mEmission = true;
        [SerializeField] private bool _mScale;
        [SerializeField] private bool _mAppearance;
        [SerializeField] private bool _mPayload = true;
        [SerializeField] private bool _mTrail = true;
        [SerializeField] private bool _mFeedbacks;
        [SerializeField] private bool _mAdvanced;
        [SerializeField] private bool _mEndpoints = true;
        [SerializeField] private bool _showLifeEnvelope = true;

        // Which spline control point shows the full 3D move gizmo; the rest are click-to-select dots. -1 = none.
        [SerializeField] private int _selectedPathPoint = -1;

        // One SerializedObject drives every module below the transport, so the stock property drawers
        // (subclass selector, curve, gradient) and Undo all work exactly as in the inspector.
        private SerializedObject _serialized;

        // Set when a field changes while the preview is PLAYING (not paused). The running effect is built
        // from a clone of the asset, so it does not see edits live; we rebuild it — but only once the drag
        // ends (hotControl clears), so a slider drag does not restart the effect every frame.
        [NonSerialized] private bool _previewDirty;

        // ---- UI Toolkit element handles (the window is UITK; OnGUI is dead once rootVisualElement fills) --
        private ObjectField _assetField;
        private ScrollView _uiScroll;
        private VisualElement _playRow;
        private Slider _scrub;
        private Label _timeLabel;
        private Image _trailImage;
        private bool _lastSession;

        // A live silhouette of the trail (head→tail taper, tinted by its gradient), rebuilt only when a
        // shape/colour field changes — see TrailPreviewTexture.
        [NonSerialized] private Texture2D _trailPreview;
        [NonSerialized] private int _trailPreviewHash;
        private static Gradient s_defaultTrailGradient;

        // Reused across scene repaints so the overlay does not allocate a fresh list every frame.
        private readonly List<A2BVisualState> _liveItems = new List<A2BVisualState>(64);

        // Live providers that resolve the CURRENT endpoint each frame by delegating back to the window
        // (GetOrigin/GetDestination). Because they read live, a restart never invalidates them, a moved
        // scene object is followed, and a dragged virtual point is followed — one code path for all of
        // it. World space matches what the scene view draws (AD-16 for scatter).
        private IA2BEndpointProvider _originProvider;
        private IA2BEndpointProvider _destinationProvider;

        [MenuItem("Tools/A2BKit/A2B Effect Editor")]
        public static void Open()
        {
            var window = GetWindow<A2BEffectEditorWindow>("A2B Effect Editor");
            window.minSize = new Vector2(340f, 480f);
            window.Show();
        }

        /// <summary>Opens the editor already pointed at <paramref name="asset"/>.</summary>
        public static void Open(A2BEffectAsset asset)
        {
            Open();
            var window = GetWindow<A2BEffectEditorWindow>();
            window._asset = asset;
            window._serialized = null;
            window._assetField?.SetValueWithoutNotify(asset);
            window.RebuildBody();   // no-op if CreateGUI hasn't built the tree yet; it will use _asset.
            window.Repaint();
        }

        private void OnEnable()
        {
            BuildProviders();
            SceneView.duringSceneGui += OnSceneGUI;
            EditorApplication.update += OnEditorUpdate;
        }

        /// <summary>
        /// Builds the endpoint providers to match how the RUNTIME resolves endpoints — the difference
        /// between a canvas HUD effect landing on target and flying off-screen.
        ///
        /// A UI target is a RectTransform whose world position is meaningless on a screen-space canvas;
        /// feeding it as a world point makes the canvas adapter re-project an already-projected point
        /// (out past 20,000 px in practice). So a RectTransform becomes an
        /// <see cref="A2BRectTransformEndpoint"/>, which reports the correct SCREEN position through the
        /// host canvas's scaler — exactly the runtime coin-to-wallet path. A plain Transform stays a
        /// world <see cref="A2BTransformEndpoint"/> (the adapter projects it, for a 3D origin). A virtual
        /// point has no runtime equivalent: on a Canvas effect its handle stands in for a screen
        /// position, so it is fed as a SCREEN sample; on a world effect it is a world point. All are
        /// live — a moved object or dragged handle is followed every frame.
        /// </summary>
        private void BuildProviders()
        {
            bool canvas = _asset != null && _asset.Space == A2BSpaceKind.Canvas;
            _originProvider = BuildProvider(_originMode, _originObject, () => _originPoint, canvas);
            _destinationProvider = BuildProvider(_destinationMode, _destinationObject, () => _destinationPoint, canvas);
        }

        private static IA2BEndpointProvider BuildProvider(EndpointMode mode, Transform obj, Func<Vector3> point, bool canvas)
        {
            if (mode == EndpointMode.SceneObject && obj != null)
            {
                return obj is RectTransform rect
                    ? (IA2BEndpointProvider)new A2BRectTransformEndpoint(rect)
                    : new A2BTransformEndpoint(obj);
            }
            return new LivePointProvider { Get = point, AsScreen = canvas };
        }

        private void OnDisable()
        {
            SceneView.duringSceneGui -= OnSceneGUI;
            EditorApplication.update -= OnEditorUpdate;

            // Only tear down the preview if it is OURS — another window or a player gizmo may own it.
            if (IsOurSession) A2BEffectPreview.Stop();

            if (_trailPreview != null)
            {
                DestroyImmediate(_trailPreview);
                _trailPreview = null;
            }
        }

        /// <summary>True when the running preview session belongs to this window.</summary>
        private bool IsOurSession =>
            A2BEffectPreview.IsPlaying && ReferenceEquals(A2BEffectPreview.Context, this);

        private void OnEditorUpdate()
        {
            // Keep the transport time/item readouts live while auto-advancing. Paused/scrubbed frames
            // are static, so there is nothing to repaint for.
            if (IsOurSession && !A2BEffectPreview.Paused) Repaint();
        }

        // ---- Endpoint resolution ---------------------------------------------------------------

        private Vector3 GetOrigin() =>
            _originMode == EndpointMode.SceneObject && _originObject != null
                ? _originObject.position
                : _originPoint;

        private Vector3 GetDestination() =>
            _destinationMode == EndpointMode.SceneObject && _destinationObject != null
                ? _destinationObject.position
                : _destinationPoint;

        // ---- Window GUI ------------------------------------------------------------------------

        // ================= UI Toolkit window ====================================================
        // Retained-mode UI: no per-frame IMGUI layout, so the layout-mismatch class of crashes cannot
        // happen. The scene handles (OnSceneGUI) and the preview/endpoint logic below are unchanged.

        public void CreateGUI()
        {
            VisualElement root = rootVisualElement;
            root.Clear();   // drop the default IMGUIContainer so OnGUI is never invoked.
            root.style.flexDirection = FlexDirection.Column;

            var top = new VisualElement();
            top.style.flexDirection = FlexDirection.Row;
            top.style.alignItems = Align.Center;
            SetPadding(top, 6, 6, 6, 4);

            _assetField = new ObjectField("Effect") { objectType = typeof(A2BEffectAsset), allowSceneObjects = false, value = _asset };
            _assetField.style.flexGrow = 1;
            _assetField.RegisterValueChangedCallback(e => SetAsset(e.newValue as A2BEffectAsset));
            top.Add(_assetField);

            var newBtn = new Button(() => { A2BEffectAsset a = CreateAsset(); if (a != null) SetAsset(a); }) { text = "New" };
            newBtn.style.width = 46;
            top.Add(newBtn);
            root.Add(top);

            _uiScroll = new ScrollView(ScrollViewMode.Vertical);
            _uiScroll.style.flexGrow = 1;
            root.Add(_uiScroll);

            RebuildBody();

            _lastSession = IsOurSession;
            root.schedule.Execute(TickTransport).Every(66);
        }

        private void SetAsset(A2BEffectAsset asset)
        {
            if (asset == _asset) return;
            if (IsOurSession) A2BEffectPreview.Stop();
            _asset = asset;
            _serialized = null;
            _assetField?.SetValueWithoutNotify(asset);
            RebuildBody();
        }

        /// <summary>(Re)builds the body under the asset picker — called on asset change and on structural edits.</summary>
        private void RebuildBody()
        {
            if (_uiScroll == null) return;
            _uiScroll.Clear();
            _trailImage = null;

            if (_asset == null)
            {
                _uiScroll.Add(HelpBox("Assign an A2B Effect asset to preview and edit it here, or create a new one."));
                return;
            }

            _serialized = new SerializedObject(_asset);

            // Everything binds/tracks on a FRESH container each rebuild. Binding twice on the persistent
            // scroll view throws ("an element can track only one serializedObject at a time"); a new element
            // that Clear() has just removed the previous of carries no stale binding.
            var body = new VisualElement();
            _uiScroll.Add(body);

            body.Add(BuildTransport());
            body.Add(BuildEndpointsModule());
            body.Add(BuildModule("Timing", ref _mTiming, DefProp("Duration"), DefProp("DurationJitter"), DefProp("Easing"), DefProp("UseUnscaledTime")));
            body.Add(BuildRefModule(DefProp("Path"), "Drag the path handles in the Scene view to shape the arc."));
            body.Add(BuildRefModule(DefProp("Emission"), "Drag the Scatter / Burst rings in the Scene view."));
            body.Add(BuildModule("Scale over Life", ref _mScale, DefProp("ScaleOverProgress"), DefProp("ArcLiftScale"), DefProp("ScaleFromPathDepth"), DefProp("PathDepthScaleStrength")));
            body.Add(BuildModule("Colour & Orientation", ref _mAppearance, DefProp("ColorOverProgress"), DefProp("AlignToVelocity")));
            body.Add(BuildRefModule(AssetProp("Payload"), null));
            body.Add(BuildTrailModule());
            body.Add(BuildFeedbacksModule());
            body.Add(BuildModule("Advanced", ref _mAdvanced, AssetProp("Space"), DefProp("EndpointLostPolicy"), DefProp("PrewarmCount"), AssetProp("SpaceOverride")));

            body.Bind(_serialized);
            body.TrackSerializedObjectValue(_serialized, _ => AfterEdit());
        }

        // ---- Module chrome (UITK) --------------------------------------------------------------

        private Foldout MakeFoldout(string title, bool open, Action<bool> onToggle)
        {
            var f = new Foldout { text = title, value = open };
            f.style.marginLeft = 6;
            f.style.marginRight = 6;
            f.style.marginBottom = 3;
            f.style.paddingBottom = 4;
            f.style.paddingRight = 4;
            f.style.backgroundColor = new Color(1f, 1f, 1f, 0.035f);
            SetRadius(f, 4);
            // Register on the foldout's OWN header toggle (the first Toggle), so a Toggle FIELD inside the
            // module — whose bool change would otherwise bubble here — never gets mistaken for a fold click.
            Toggle header = f.Q<Toggle>();
            if (header != null)
            {
                header.style.unityFontStyleAndWeight = FontStyle.Bold;
                header.style.marginBottom = 2;
                if (onToggle != null)
                    header.RegisterValueChangedCallback(e => { onToggle(e.newValue); e.StopPropagation(); });
            }
            return f;
        }

        private Foldout BuildModule(string title, ref bool open, params SerializedProperty[] props) =>
            BuildModule(title, ref open, null, props);

        /// <summary>
        /// A section for a SINGLE SerializeReference property (Path, Emission, Payload). Its drawer already
        /// shows a foldout named after the field plus the subclass dropdown, so wrapping it in another
        /// module foldout of the same name gave the "Path → Path" double section. Here the property's own
        /// foldout IS the section; we only frame it as a panel and bold its header to match the others.
        /// </summary>
        private VisualElement BuildRefModule(SerializedProperty prop, string hint)
        {
            var panel = new VisualElement();
            panel.style.marginLeft = 6;
            panel.style.marginRight = 6;
            panel.style.marginBottom = 3;
            SetPadding(panel, 4, 4, 2, 4);
            panel.style.backgroundColor = new Color(1f, 1f, 1f, 0.035f);
            SetRadius(panel, 4);

            var pf = new PropertyField(prop);
            // The drawer builds its foldout lazily; bold its header once it exists so it reads as a module.
            pf.RegisterCallback<GeometryChangedEvent>(_ =>
            {
                Toggle t = pf.Q<Foldout>()?.Q<Toggle>();
                if (t != null) t.style.unityFontStyleAndWeight = FontStyle.Bold;
            });
            panel.Add(pf);

            if (!string.IsNullOrEmpty(hint)) panel.Add(Hint(hint));
            return panel;
        }

        private Foldout BuildModule(string title, ref bool open, string hint, params SerializedProperty[] props)
        {
            var f = MakeFoldout(title, open, v => SetFold(title, v));
            foreach (SerializedProperty p in props)
                if (p != null) f.Add(new PropertyField(p));
            if (!string.IsNullOrEmpty(hint)) f.Add(Hint(hint));
            return f;
        }

        // The ref-bool modules persist their fold state by name; a tiny switch keeps it declarative.
        private void SetFold(string title, bool v)
        {
            switch (title)
            {
                case "Timing": _mTiming = v; break;
                case "Path": _mPath = v; break;
                case "Emission": _mEmission = v; break;
                case "Scale over Life": _mScale = v; break;
                case "Colour & Orientation": _mAppearance = v; break;
                case "Payload": _mPayload = v; break;
                case "Advanced": _mAdvanced = v; break;
                case "Endpoints": _mEndpoints = v; break;
                case "Feedbacks": _mFeedbacks = v; break;
                case "Trail": _mTrail = v; break;
            }
        }

        // ---- Transport (UITK) ------------------------------------------------------------------

        private VisualElement BuildTransport()
        {
            var box = MakeFoldout("Preview", true, null);

            _playRow = new VisualElement { style = { flexDirection = FlexDirection.Row, marginBottom = 4 } };
            box.Add(_playRow);
            RebuildPlayRow();

            _scrub = new Slider("Time", 0f, Mathf.Max(0.0001f, SpanFromAsset()));
            _scrub.RegisterValueChangedCallback(e =>
            {
                if (!IsOurSession) StartPreview();
                A2BEffectPreview.Scrub(e.newValue);
            });
            box.Add(_scrub);

            _timeLabel = new Label { style = { unityFontStyleAndWeight = FontStyle.Normal, opacity = 0.7f, marginBottom = 4 } };
            box.Add(_timeLabel);

            var loop = new Toggle("Loop") { value = _loop };
            loop.RegisterValueChangedCallback(e => { _loop = e.newValue; A2BEffectPreview.Loop = _loop; });
            box.Add(loop);

            var speed = new Slider("Speed", 0.1f, 3f) { value = _speed };
            speed.RegisterValueChangedCallback(e => { _speed = e.newValue; A2BEffectPreview.Speed = _speed; });
            box.Add(speed);

            var payload = new Toggle("Show payload visuals (real sprites/meshes in Scene + Game)") { value = _showPayloadVisuals };
            payload.RegisterValueChangedCallback(e => { _showPayloadVisuals = e.newValue; if (IsOurSession) StartPreview(); });
            box.Add(payload);

            return box;
        }

        private void RebuildPlayRow()
        {
            if (_playRow == null) return;
            _playRow.Clear();

            if (!IsOurSession)
            {
                _playRow.Add(FlexButton("▶ Play", StartPreview));
            }
            else
            {
                _playRow.Add(FlexButton("⟲ Restart", StartPreview));
                _playRow.Add(FlexButton(A2BEffectPreview.Paused ? "▶ Resume" : "❚❚ Pause",
                    () => { A2BEffectPreview.SetPaused(!A2BEffectPreview.Paused); RebuildPlayRow(); }));
                _playRow.Add(FlexButton("■ Stop", () => { A2BEffectPreview.Stop(); RebuildPlayRow(); }));
            }
        }

        /// <summary>Ticks a few times a second: live scrub/time readouts, play-row refresh, trail silhouette.</summary>
        private void TickTransport()
        {
            if (_asset == null) return;

            bool session = IsOurSession;
            if (session != _lastSession)
            {
                _lastSession = session;
                RebuildPlayRow();
            }

            float span = Mathf.Max(0.0001f, A2BEffectPreview.Span > 0f ? A2BEffectPreview.Span : SpanFromAsset());
            float elapsed = session ? Mathf.Clamp(A2BEffectPreview.Elapsed, 0f, span) : 0f;

            if (_scrub != null)
            {
                _scrub.highValue = span;
                if (session && !A2BEffectPreview.Paused) _scrub.SetValueWithoutNotify(elapsed);
            }
            if (_timeLabel != null)
                _timeLabel.text = session
                    ? $"{elapsed:0.00}s / {span:0.00}s     Items in flight: {A2BEffectPreview.ActiveItemCount}"
                    : $"Length: {span:0.00}s";

            if (_trailImage != null)
            {
                A2BTrailFeedback trail = FindTrail();
                if (trail != null) _trailImage.image = TrailPreviewTexture(trail);
            }

            ApplyPreviewDirty();
        }

        // ---- Endpoints (UITK, window state not on the SerializedObject) -------------------------

        private VisualElement BuildEndpointsModule()
        {
            var f = MakeFoldout("Endpoints", _mEndpoints, v => SetFold("Endpoints", v));
            f.Add(BuildEndpoint("Origin", () => _originMode, m => _originMode = m, () => _originObject, o => _originObject = o, () => _originPoint, p => _originPoint = p));
            f.Add(BuildEndpoint("Destination", () => _destinationMode, m => _destinationMode = m, () => _destinationObject, o => _destinationObject = o, () => _destinationPoint, p => _destinationPoint = p));
            return f;
        }

        private VisualElement BuildEndpoint(
            string label,
            Func<EndpointMode> getMode, Action<EndpointMode> setMode,
            Func<Transform> getObj, Action<Transform> setObj,
            Func<Vector3> getPoint, Action<Vector3> setPoint)
        {
            var wrap = new VisualElement { style = { marginBottom = 4 } };

            var mode = new EnumField(label, getMode());
            wrap.Add(mode);

            var body = new VisualElement();
            wrap.Add(body);

            void Rebuild()
            {
                body.Clear();
                if (getMode() == EndpointMode.SceneObject)
                {
                    var of = new ObjectField { objectType = typeof(Transform), allowSceneObjects = true, value = getObj() };
                    of.RegisterValueChangedCallback(e => { setObj(e.newValue as Transform); RebuildEndpointsAndRestart(); });
                    body.Add(of);
                    var use = new Button(() => { if (Selection.activeTransform != null) { setObj(Selection.activeTransform); of.SetValueWithoutNotify(Selection.activeTransform); RebuildEndpointsAndRestart(); } }) { text = "Use Selection" };
                    body.Add(use);
                }
                else
                {
                    var vf = new Vector3Field { value = getPoint() };
                    vf.RegisterValueChangedCallback(e => { setPoint(e.newValue); RebuildEndpointsAndRestart(); });
                    body.Add(vf);
                }
            }

            mode.RegisterValueChangedCallback(e => { setMode((EndpointMode)e.newValue); Rebuild(); RebuildEndpointsAndRestart(); });
            Rebuild();
            return wrap;
        }

        // ---- Trail & Feedbacks modules (UITK) --------------------------------------------------

        private VisualElement BuildTrailModule()
        {
            var f = MakeFoldout("Trail", _mTrail, v => SetFold("Trail", v));

            A2BTrailFeedback trail = FindTrail();
            var enable = new Toggle("Enable trail") { value = trail != null };
            enable.RegisterValueChangedCallback(e =>
            {
                Undo.RecordObject(_asset, e.newValue ? "Add A2B Trail" : "Remove A2B Trail");
                if (e.newValue) _asset.Feedbacks.Add(NewTrailForAsset());
                else _asset.Feedbacks.Remove(FindTrail());
                CommitEdit();
                RebuildBody();
            });
            f.Add(enable);

            if (trail == null)
            {
                f.Add(Hint("Enable to add a comet/streak behind each item."));
                return f;
            }

            _trailImage = new Image { scaleMode = ScaleMode.StretchToFill, image = TrailPreviewTexture(trail) };
            _trailImage.style.height = 46;
            _trailImage.style.marginBottom = 4;
            SetRadius(_trailImage, 3);
            f.Add(_trailImage);

            SerializedProperty tp = TrailProperty();
            if (tp != null)
            {
                f.Add(TrailField(tp, "Time", "Length (s)"));
                f.Add(TrailField(tp, "StartWidth", "Head Width"));
                f.Add(TrailField(tp, "EndWidth", "Tail Width"));
                f.Add(TrailField(tp, "Color", "Colour"));
                f.Add(TrailField(tp, "ClearMode", null));
                f.Add(TrailField(tp, "ClearDuration", null));
                f.Add(TrailField(tp, "SoftGlow", null));
                var adv = MakeFoldout("Advanced", _trailAdvanced, v => _trailAdvanced = v);
                adv.style.marginLeft = 0;
                adv.Add(TrailField(tp, "MinVertexDistance", null));
                adv.Add(TrailField(tp, "CornerVertices", null));
                adv.Add(TrailField(tp, "CapVertices", null));
                f.Add(adv);
            }
            return f;
        }

        private PropertyField TrailField(SerializedProperty trailProp, string relative, string label)
        {
            SerializedProperty p = trailProp.FindPropertyRelative(relative);
            // PropertyField carries the property's path; the container's body.Bind resolves it. No explicit
            // BindProperty — that would bind the field to the SerializedObject a SECOND time.
            return label != null ? new PropertyField(p, label) : new PropertyField(p);
        }

        private VisualElement BuildFeedbacksModule()
        {
            var f = MakeFoldout("Feedbacks", _mFeedbacks, v => SetFold("Feedbacks", v));

            SerializedProperty list = _serialized.FindProperty("Feedbacks");
            int shown = 0;
            for (int i = 0; i < list.arraySize; i++)
            {
                SerializedProperty el = list.GetArrayElementAtIndex(i);
                if (el.managedReferenceValue is A2BTrailFeedback) continue;

                int index = i;
                var row = new VisualElement { style = { flexDirection = FlexDirection.Row, alignItems = Align.FlexStart } };
                var pf = new PropertyField(el) { style = { flexGrow = 1 } };
                row.Add(pf);
                var del = new Button(() => { list.DeleteArrayElementAtIndex(index); _serialized.ApplyModifiedProperties(); AfterEdit(); RebuildBody(); }) { text = "−" };
                del.style.width = 22;
                row.Add(del);
                f.Add(row);
                shown++;
            }
            if (shown == 0) f.Add(Hint("Impact, audio, spawn-pop… reactions that fire when items land."));

            var add = new Button(() => { list.arraySize++; _serialized.ApplyModifiedProperties(); AfterEdit(); RebuildBody(); }) { text = "Add Feedback" };
            f.Add(add);
            return f;
        }

        /// <summary>The trail feedback's SerializedProperty (its array element), or null.</summary>
        private SerializedProperty TrailProperty()
        {
            SerializedProperty list = _serialized.FindProperty("Feedbacks");
            for (int i = 0; i < list.arraySize; i++)
                if (list.GetArrayElementAtIndex(i).managedReferenceValue is A2BTrailFeedback)
                    return list.GetArrayElementAtIndex(i);
            return null;
        }

        // ---- Small UITK helpers ----------------------------------------------------------------

        private static Button FlexButton(string text, Action onClick)
        {
            var b = new Button(onClick) { text = text };
            b.style.flexGrow = 1;
            b.style.height = 24;
            return b;
        }

        private static Label Hint(string text)
        {
            var l = new Label(text) { style = { whiteSpace = WhiteSpace.Normal, opacity = 0.6f, marginTop = 2, fontSize = 11 } };
            return l;
        }

        private static VisualElement HelpBox(string text)
        {
            var v = new VisualElement();
            SetPadding(v, 8, 8, 8, 8);
            v.style.marginTop = 8;
            v.style.backgroundColor = new Color(1f, 1f, 1f, 0.05f);
            SetRadius(v, 4);
            v.Add(new Label(text) { style = { whiteSpace = WhiteSpace.Normal } });
            return v;
        }

        private static void SetPadding(VisualElement v, float l, float r, float t, float b)
        {
            v.style.paddingLeft = l; v.style.paddingRight = r; v.style.paddingTop = t; v.style.paddingBottom = b;
        }

        private static void SetRadius(VisualElement v, float radius)
        {
            v.style.borderTopLeftRadius = radius; v.style.borderTopRightRadius = radius;
            v.style.borderBottomLeftRadius = radius; v.style.borderBottomRightRadius = radius;
        }

        // ---- Modules (Particle-System-style panels) --------------------------------------------

        // ---- Module chrome ---------------------------------------------------------------------

        private SerializedProperty DefProp(string relative) =>
            _serialized.FindProperty("Definition").FindPropertyRelative(relative);

        private SerializedProperty AssetProp(string name) => _serialized.FindProperty(name);

        /// <summary>The first trail feedback on the asset, or null.</summary>
        private A2BTrailFeedback FindTrail()
        {
            if (_asset == null || _asset.Feedbacks == null) return null;
            for (int i = 0; i < _asset.Feedbacks.Count; i++)
                if (_asset.Feedbacks[i] is A2BTrailFeedback t) return t;
            return null;
        }

        /// <summary>A trail with defaults that are actually visible for the asset's space (canvas = pixels).</summary>
        private A2BTrailFeedback NewTrailForAsset()
        {
            bool canvas = _asset != null && _asset.Space == A2BSpaceKind.Canvas;
            return new A2BTrailFeedback
            {
                Time = 0.6f,
                StartWidth = canvas ? 20f : 0.15f,
                EndWidth = 0f,
                Color = DefaultTrailGradient(),
            };
        }

        /// <summary>
        /// Builds (and caches) the silhouette texture. Each column is a point along the trail, head at the
        /// left; its half-height is the width there (Head→Tail lerp, normalised) and its colour is the
        /// gradient sampled at that point. Rebuilt only when a shape/colour field actually changes.
        /// </summary>
        private Texture2D TrailPreviewTexture(A2BTrailFeedback trail)
        {
            int hash = TrailPreviewHash(trail);
            if (_trailPreview != null && hash == _trailPreviewHash) return _trailPreview;

            const int w = 256, h = 46;
            if (_trailPreview == null)
                _trailPreview = new Texture2D(w, h, TextureFormat.RGBA32, mipChain: false)
                {
                    hideFlags = HideFlags.HideAndDontSave,
                    wrapMode = TextureWrapMode.Clamp,
                };

            Gradient g = trail.Color ?? DefaultTrailGradient();
            float maxW = Mathf.Max(0.0001f, Mathf.Max(trail.StartWidth, trail.EndWidth));
            var px = new Color32[w * h];
            float centre = (h - 1) * 0.5f;

            for (int x = 0; x < w; x++)
            {
                float t = x / (w - 1f);                                  // 0 head (left) → 1 tail (right)
                float widthNorm = Mathf.Lerp(trail.StartWidth, trail.EndWidth, t) / maxW;
                float half = widthNorm * (centre - 1f);
                Color c = g.Evaluate(t);
                for (int y = 0; y < h; y++)
                {
                    float d = Mathf.Abs(y - centre);
                    px[y * w + x] = d <= half ? (Color32)c : new Color32(0, 0, 0, 0);
                }
            }

            _trailPreview.SetPixels32(px);
            _trailPreview.Apply(updateMipmaps: false);
            _trailPreviewHash = hash;
            return _trailPreview;
        }

        private static int TrailPreviewHash(A2BTrailFeedback trail)
        {
            int hash = trail.StartWidth.GetHashCode();
            hash = hash * 397 ^ trail.EndWidth.GetHashCode();
            Gradient g = trail.Color ?? DefaultTrailGradient();
            for (int i = 0; i < 8; i++)
                hash = hash * 31 ^ g.Evaluate(i / 7f).GetHashCode();
            return hash;
        }

        private static Gradient DefaultTrailGradient()
        {
            if (s_defaultTrailGradient != null) return s_defaultTrailGradient;
            s_defaultTrailGradient = new Gradient();
            s_defaultTrailGradient.SetKeys(
                new[]
                {
                    new GradientColorKey(new Color(0.55f, 0.9f, 1f), 0f),
                    new GradientColorKey(new Color(0.2f, 0.5f, 1f), 1f),
                },
                new[]
                {
                    new GradientAlphaKey(1f, 0f),
                    new GradientAlphaKey(0f, 1f),
                });
            return s_defaultTrailGradient;
        }


        // ---- Scene overlay ---------------------------------------------------------------------

        private void OnSceneGUI(SceneView sceneView)
        {
            if (_asset == null) return;

            Vector3 origin = GetOrigin();
            Vector3 destination = GetDestination();

            DrawPathOverlay(origin, destination);
            DrawEndpointMarker(origin, A2BEffectGizmos.OriginColor, "Origin");
            DrawEndpointMarker(destination, A2BEffectGizmos.DestinationColor, "Destination");
            DrawBurstArea(origin, sceneView);
            DrawPathHandles(origin, destination);
            DrawVirtualHandles();

            // A static preview of the scale/colour envelope along the path — a row of dots, each sized by
            // ScaleOverProgress and tinted by ColorOverProgress at its point. Only when not playing, so it
            // does not fight the live items the running preview already draws.
            if (_showLifeEnvelope && !IsOurSession) DrawLifeEnvelope(origin, destination);

            DrawLiveItems();
        }

        private static readonly Color ScatterAreaColor = new Color(1f, 0.85f, 0.3f, 1f);
        private static readonly Color BurstAreaColor = new Color(1f, 0.5f, 0.25f, 1f);

        /// <summary>
        /// Draws the burst footprint at the origin — the spawn SCATTER radius (where items are born) and,
        /// for a burst-gather path, the BURST radius (how far they spray before turning for the target).
        /// Both are shown as an editable radius handle: drag the ring to resize. Radii are in working
        /// units — on a Canvas that is pixels, which in these scenes matches the pixel-scale world the
        /// endpoints already live in, so the ring sits at the right size next to the path.
        /// </summary>
        private void DrawBurstArea(Vector3 origin, SceneView sceneView)
        {
            A2BEffectDefinition def = _asset.Definition;
            if (def == null) return;

            Vector3 normal = sceneView != null && sceneView.camera != null
                ? -sceneView.camera.transform.forward
                : Vector3.forward;
            Quaternion rot = Quaternion.LookRotation(normal);

            if (def.Emission is A2BBurstEmission burst)
            {
                float r = DrawRadiusHandle(origin, burst.ScatterRadius, normal, rot, ScatterAreaColor, "Scatter");
                if (!Mathf.Approximately(r, burst.ScatterRadius))
                {
                    Undo.RecordObject(_asset, "Edit A2B Scatter Radius");
                    burst.ScatterRadius = Mathf.Max(0f, r);
                    CommitEdit();
                }
            }
            else if ((def.Emission?.ScatterRadius ?? 0f) > 0.001f)
            {
                DrawRing(origin, def.Emission.ScatterRadius, normal, ScatterAreaColor, "Scatter");
            }

            if (def.Path is A2BBurstGatherPath bg)
            {
                float r = DrawRadiusHandle(origin, bg.BurstRadius, normal, rot, BurstAreaColor, "Burst");
                if (!Mathf.Approximately(r, bg.BurstRadius))
                {
                    Undo.RecordObject(_asset, "Edit A2B Burst Radius");
                    bg.BurstRadius = Mathf.Max(0f, r);
                    CommitEdit();
                }
            }
        }

        private static float DrawRadiusHandle(
            Vector3 center, float radius, Vector3 normal, Quaternion rot, Color color, string label)
        {
            DrawRing(center, radius, normal, color, label);
            Handles.color = color;
            return Handles.RadiusHandle(rot, center, radius);
        }

        private static void DrawRing(Vector3 center, float radius, Vector3 normal, Color color, string label)
        {
            if (radius <= 0.0001f) return;
            Handles.color = new Color(color.r, color.g, color.b, 0.06f);
            Handles.DrawSolidDisc(center, normal, radius);
            Handles.color = color;
            Handles.DrawWireDisc(center, normal, radius);
            Handles.Label(center + Vector3.up * radius, $"{label} r={radius:0.#}");
        }

        /// <summary>
        /// Interactive handles that reshape the path in the Scene — the arc, dragged rather than typed.
        /// Only shapes that map cleanly to a scene handle get one; a path whose form is per-item and
        /// seed-driven (procedural spiral, burst spray) has no single control point to grab, so it is
        /// left to the inspector rather than given a handle that lies about where the curve goes.
        /// </summary>
        private void DrawPathHandles(Vector3 origin, Vector3 destination)
        {
            if (_asset.Definition == null) return;

            if (_asset.Definition.Path is A2BBezierPath bezier)
                DrawBezierArcHandle(bezier, origin, destination);
            else if (_asset.Definition.Path is A2BSplinePath spline)
                DrawSplineHandles(spline, origin, destination);
        }

        /// <summary>
        /// Scene handles for a multi-point Bézier: one draggable handle per control point, a "−" beside
        /// each to delete it, and a "+" on every segment of the control polygon to insert a new point
        /// there. Offsets are stored as fractions of the chord, so a drag is decomposed back into
        /// (Along, Offset/len) — the same exact round trip as the single-arc handle, generalized.
        /// Structural edits (add/remove) are deferred until after the draw loop so the list is not
        /// mutated mid-iteration.
        /// </summary>
        private void DrawSplineHandles(A2BSplinePath spline, Vector3 origin, Vector3 destination)
        {
            spline.ControlPoints ??= new List<A2BSplineControlPoint>();
            List<A2BSplineControlPoint> points = spline.ControlPoints;

            Vector3 chord = destination - origin;
            float len = chord.magnitude;
            if (len < 1e-4f) len = 1f;

            if (_selectedPathPoint >= points.Count) _selectedPathPoint = -1;
            if (_selectedPathPoint < 0 && points.Count > 0) _selectedPathPoint = 0;   // always one live gizmo

            int removeAt = -1;
            int insertAfter = -2;   // -2 = none; -1 = before first (origin→cp0)

            for (int i = 0; i < points.Count; i++)
            {
                A2BSplineControlPoint cp = points[i];
                Vector3 world = Vector3.LerpUnclamped(origin, destination, cp.Along) + cp.Offset * len;
                float hs = HandleUtility.GetHandleSize(world);

                // Lead line from the chord to the control point, so its pull is legible.
                Handles.color = new Color(A2BEffectGizmos.PathColor.r, A2BEffectGizmos.PathColor.g,
                    A2BEffectGizmos.PathColor.b, 0.4f);
                Handles.DrawDottedLine(Vector3.LerpUnclamped(origin, destination, cp.Along), world, 2f);
                Handles.Label(world + Vector3.up * hs * 0.2f, $"P{i + 1}");

                if (i == _selectedPathPoint)
                {
                    // Selected: a full 3D move gizmo (X/Y/Z arrows + planes), like editing a Transform.
                    using (var change = new EditorGUI.ChangeCheckScope())
                    {
                        Vector3 moved = Handles.PositionHandle(world, Quaternion.identity);
                        if (change.changed) ApplySplineControl(spline, i, origin, destination, moved);
                    }

                    // Delete affordance sits by the selected point only, to keep the scene uncluttered.
                    Vector3 delPos = world + (Vector3.right + Vector3.up) * hs * 0.28f;
                    Handles.color = A2BEffectGizmos.WarningColor;
                    if (Handles.Button(delPos, Quaternion.identity, hs * 0.06f, hs * 0.08f, Handles.DotHandleCap))
                        removeAt = i;
                }
                else
                {
                    // Unselected: a click-to-select dot. Repaint the SCENE (not the window) so the 3D gizmo
                    // swaps in immediately — repainting only the window is why the point stayed a dead dot.
                    Handles.color = A2BEffectGizmos.PathColor;
                    if (Handles.Button(world, Quaternion.identity, hs * 0.11f, hs * 0.14f, Handles.SphereHandleCap))
                    {
                        _selectedPathPoint = i;
                        SceneView.RepaintAll();
                    }
                }
            }

            // Insert buttons at the midpoint of each control-polygon segment.
            insertAfter = DrawSplineInsertButtons(points, origin, destination, len);

            if (removeAt >= 0)
            {
                Undo.RecordObject(_asset, "Remove A2B Bézier Point");
                points.RemoveAt(removeAt);
                if (_selectedPathPoint >= points.Count) _selectedPathPoint = points.Count - 1;
                CommitEdit();
            }
            else if (insertAfter >= -1)
            {
                InsertSplinePoint(spline, insertAfter, origin, destination, len);
                _selectedPathPoint = Mathf.Clamp(insertAfter + 1, 0, points.Count - 1);   // select the new point
            }
        }

        /// <summary>Draws a "+" at each segment midpoint; returns the polygon index to insert after, or -2.</summary>
        private int DrawSplineInsertButtons(
            List<A2BSplineControlPoint> points, Vector3 origin, Vector3 destination, float len)
        {
            int segments = points.Count + 1;   // origin → p0 → … → pN → destination
            int insertAfter = -2;

            for (int s = 0; s < segments; s++)
            {
                Vector3 a = s == 0 ? origin
                    : Vector3.LerpUnclamped(origin, destination, points[s - 1].Along) + points[s - 1].Offset * len;
                Vector3 b = s == points.Count ? destination
                    : Vector3.LerpUnclamped(origin, destination, points[s].Along) + points[s].Offset * len;

                Vector3 mid = (a + b) * 0.5f;
                float hs = HandleUtility.GetHandleSize(mid);
                Handles.color = A2BEffectGizmos.OriginColor;
                if (Handles.Button(mid, Quaternion.identity, hs * 0.06f, hs * 0.08f, Handles.SphereHandleCap))
                    insertAfter = s - 1;   // s==0 inserts before the first point (index -1)
            }

            return insertAfter;
        }

        private void InsertSplinePoint(
            A2BSplinePath spline, int insertAfter, Vector3 origin, Vector3 destination, float len)
        {
            List<A2BSplineControlPoint> points = spline.ControlPoints;

            // New point's Along/Offset are the average of its neighbours (origin = Along 0/Offset 0,
            // destination = Along 1/Offset 0), so it lands on the current control polygon and the curve
            // barely moves until the user drags it.
            float alongA = insertAfter < 0 ? 0f : points[insertAfter].Along;
            Vector3 offA = insertAfter < 0 ? Vector3.zero : points[insertAfter].Offset;
            int nextIndex = insertAfter + 1;
            float alongB = nextIndex >= points.Count ? 1f : points[nextIndex].Along;
            Vector3 offB = nextIndex >= points.Count ? Vector3.zero : points[nextIndex].Offset;

            var cp = new A2BSplineControlPoint((alongA + alongB) * 0.5f, (offA + offB) * 0.5f);

            Undo.RecordObject(_asset, "Add A2B Bézier Point");
            points.Insert(nextIndex, cp);
            CommitEdit();
        }

        /// <summary>Decomposes a dragged spline handle back into (Along, Offset as a fraction of chord).</summary>
        private void ApplySplineControl(
            A2BSplinePath spline, int index, Vector3 origin, Vector3 destination, Vector3 world)
        {
            Undo.RecordObject(_asset, "Move A2B Bézier Point");

            Vector3 chord = destination - origin;
            float chordSqr = chord.sqrMagnitude;
            A2BSplineControlPoint cp = spline.ControlPoints[index];

            if (chordSqr > 1e-8f)
            {
                float along = Mathf.Clamp01(Vector3.Dot(world - origin, chord) / chordSqr);
                cp.Along = along;
                Vector3 basePoint = origin + chord * along;
                cp.Offset = (world - basePoint) / Mathf.Sqrt(chordSqr);
            }

            CommitEdit();
        }

        private void CommitEdit()
        {
            EditorUtility.SetDirty(_asset);
            _serialized?.Update();   // keep the module SerializedObject in step with a direct/handle edit.
            AfterEdit();
            Repaint();
            SceneView.RepaintAll();   // keep the scene handles in step after a structural or handle edit.
        }

        /// <summary>
        /// Reflects an edit into the preview: a paused frame re-simulates in place (immediate), and a
        /// PLAYING preview is flagged to rebuild — deferred to <see cref="ApplyPreviewDirty"/> at drag-end
        /// so a slider does not restart the effect on every frame.
        /// </summary>
        private void AfterEdit()
        {
            // While a control (scene handle or field) is actively dragged, DEFER the re-simulation to
            // release — re-simulating the whole effect on every drag frame is what made dragging a path
            // "stick". The path curve itself still updates live, because the scene overlay reads the asset.
            if (GUIUtility.hotControl != 0)
            {
                _previewDirty = true;
                return;
            }

            ResimulateIfPaused();
            if (IsOurSession && !A2BEffectPreview.Paused) _previewDirty = true;
        }

        /// <summary>Once the mouse releases the control, reflects a deferred edit into the preview: a playing session rebuilds, a paused one re-simulates its held frame.</summary>
        private void ApplyPreviewDirty()
        {
            if (!_previewDirty || GUIUtility.hotControl != 0) return;   // still dragging — wait for release.
            _previewDirty = false;
            if (IsOurSession && !A2BEffectPreview.Paused) StartPreview();
            else ResimulateIfPaused();
        }

        /// <summary>
        /// A single control-point handle for the quadratic arc. Its position IS the bezier control
        /// point (chord-at-bias + direction * height), so dragging it edits ArcBias, ArcHeight and
        /// ArcDirection together and the arc bulges to follow. A dotted lead from the chord shows what
        /// the handle controls. Jitter is held at zero here so the handle tracks the authored arc, the
        /// same curve <see cref="A2BEffectGizmos"/> draws.
        /// </summary>
        private void DrawBezierArcHandle(A2BBezierPath bezier, Vector3 origin, Vector3 destination)
        {
            Vector3 dir = bezier.ArcDirection.sqrMagnitude < 1e-6f ? Vector3.up : bezier.ArcDirection.normalized;
            Vector3 basePoint = Vector3.LerpUnclamped(origin, destination, bezier.ArcBias);
            Vector3 control = basePoint + dir * bezier.ArcHeight;

            // The lead line: this handle pulls the arc up off the chord at that point.
            Handles.color = new Color(A2BEffectGizmos.PathColor.r, A2BEffectGizmos.PathColor.g,
                A2BEffectGizmos.PathColor.b, 0.5f);
            Handles.DrawDottedLine(basePoint, control, 3f);
            Handles.SphereHandleCap(0, basePoint, Quaternion.identity,
                HandleUtility.GetHandleSize(basePoint) * 0.05f, EventType.Repaint);

            Handles.Label(control + Vector3.up * HandleUtility.GetHandleSize(control) * 0.22f,
                $"Arc {bezier.ArcHeight:0.##}");

            // The arc's single control point gets the full 3D move gizmo (X/Y/Z), like a Transform handle.
            using (var change = new EditorGUI.ChangeCheckScope())
            {
                Vector3 moved = Handles.PositionHandle(control, Quaternion.identity);
                if (change.changed) ApplyBezierControl(bezier, origin, destination, moved);
            }
        }

        /// <summary>
        /// Decomposes a dragged control point back into the arc's authored parameters, so the round
        /// trip is exact: setting the params from position P puts the control point right back at P.
        /// Records Undo against the asset and re-simulates a held frame so the change is immediate.
        /// </summary>
        private void ApplyBezierControl(A2BBezierPath bezier, Vector3 origin, Vector3 destination, Vector3 control)
        {
            Undo.RecordObject(_asset, "Edit A2B Arc");

            Vector3 chord = destination - origin;
            float chordLen = chord.magnitude;

            Vector3 basePoint;
            if (chordLen > 1e-4f)
            {
                Vector3 chordDir = chord / chordLen;
                float along = Vector3.Dot(control - origin, chordDir);
                bezier.ArcBias = Mathf.Clamp(along / chordLen, 0.05f, 0.95f);
                basePoint = origin + chordDir * (bezier.ArcBias * chordLen);
            }
            else
            {
                basePoint = origin;
            }

            Vector3 offset = control - basePoint;
            float height = offset.magnitude;
            bezier.ArcHeight = height;
            if (height > 1e-4f) bezier.ArcDirection = offset / height;

            EditorUtility.SetDirty(_asset);
            _serialized?.Update();
            AfterEdit();
            Repaint();
        }

        private void DrawPathOverlay(Vector3 origin, Vector3 destination)
        {
            IA2BPath path = _asset.Definition != null ? _asset.Definition.Path : null;
            if (path == null)
            {
                Handles.color = A2BEffectGizmos.WarningColor;
                Handles.DrawDottedLine(origin, destination, 4f);
                return;
            }

            var ctx = new A2BPathContext(origin, destination, 0, 1, A2BEffectGizmos.GizmoSeed);
            Vector3[] points = A2BEffectGizmos.SamplePath(path, in ctx);

            Handles.color = A2BEffectGizmos.PathColor;
            Handles.DrawAAPolyLine(3f, A2BEffectGizmos.SampleCount, points);
        }

        private void DrawVirtualHandles()
        {
            if (_originMode == EndpointMode.Virtual)
            {
                using (var change = new EditorGUI.ChangeCheckScope())
                {
                    Vector3 moved = Handles.PositionHandle(_originPoint, Quaternion.identity);
                    if (change.changed)
                    {
                        _originPoint = moved;
                        AfterEdit();
                        Repaint();
                    }
                }
            }

            if (_destinationMode == EndpointMode.Virtual)
            {
                using (var change = new EditorGUI.ChangeCheckScope())
                {
                    Vector3 moved = Handles.PositionHandle(_destinationPoint, Quaternion.identity);
                    if (change.changed)
                    {
                        _destinationPoint = moved;
                        AfterEdit();
                        Repaint();
                    }
                }
            }
        }

        private void DrawLiveItems()
        {
            if (!IsOurSession) return;

            // In real-payload mode the actual renderers draw the items in both views; sphere markers on
            // top would just be noise. Only the motion-dot mode needs this overlay.
            if (A2BEffectPreview.IsRenderingRealPayload) return;

            A2BEffectPreview.CopyActiveItems(_liveItems);

            for (int i = 0; i < _liveItems.Count; i++)
            {
                A2BVisualState state = _liveItems[i];
                Handles.color = state.Color;
                float size = HandleUtility.GetHandleSize(state.Position) * 0.05f * Mathf.Max(0.05f, state.Scale.x);
                Handles.SphereHandleCap(0, state.Position, Quaternion.identity, size, EventType.Repaint);
            }
        }

        /// <summary>
        /// Dots along the path, each sized by <see cref="A2BEffectDefinition.ScaleOverProgress"/> and
        /// tinted by <see cref="A2BEffectDefinition.ColorOverProgress"/> at its progress — a read-at-a-glance
        /// preview of how an item grows/shrinks and recolours on its way, without pressing Play.
        /// </summary>
        private void DrawLifeEnvelope(Vector3 origin, Vector3 destination)
        {
            A2BEffectDefinition def = _asset.Definition;
            if (def == null || def.Path == null) return;

            var ctx = new A2BPathContext(origin, destination, 0, 1, A2BEffectGizmos.GizmoSeed);
            Vector3[] pts = A2BEffectGizmos.SamplePath(def.Path, in ctx);
            if (pts == null || pts.Length == 0) return;

            const int dots = 14;
            for (int i = 0; i <= dots; i++)
            {
                float t = i / (float)dots;
                int idx = Mathf.Clamp(Mathf.RoundToInt(t * (pts.Length - 1)), 0, pts.Length - 1);
                Vector3 p = pts[idx];

                float scale = def.ScaleOverProgress != null ? Mathf.Max(0.02f, def.ScaleOverProgress.Evaluate(t)) : 1f;
                Color c = def.ColorOverProgress != null ? def.ColorOverProgress.Evaluate(t) : Color.white;

                Handles.color = c;
                float size = HandleUtility.GetHandleSize(p) * 0.045f * scale;
                Handles.SphereHandleCap(0, p, Quaternion.identity, size, EventType.Repaint);
            }
        }

        private static void DrawEndpointMarker(Vector3 position, Color color, string label)
        {
            float size = HandleUtility.GetHandleSize(position);
            Handles.color = color;
            Handles.SphereHandleCap(0, position, Quaternion.identity, size * 0.12f, EventType.Repaint);
            Handles.Label(position + Vector3.up * size * 0.18f, label);
        }

        // ---- Preview control -------------------------------------------------------------------

        private void StartPreview()
        {
            if (_asset == null) return;

            BuildProviders();
            A2BEffectPreview.Loop = _loop;
            A2BEffectPreview.Speed = _speed;
            A2BEffectPreview.Begin(_asset, _originProvider, _destinationProvider, this, _showPayloadVisuals);
        }

        /// <summary>
        /// Re-runs the simulation up to the current elapsed time, but only when the preview is paused
        /// or scrubbed — a live edit should update a held frame in place, without secretly starting
        /// playback that the designer did not ask for.
        /// </summary>
        private void ResimulateIfPaused()
        {
            if (IsOurSession && A2BEffectPreview.Paused)
                A2BEffectPreview.Scrub(A2BEffectPreview.Elapsed);
        }

        /// <summary>
        /// Rebuilds the endpoint providers and restarts the running preview, holding its place: if it
        /// was paused/scrubbed we scrub back to the same time, so switching an endpoint's mode does not
        /// silently resume playback the designer had stopped.
        /// </summary>
        private void RebuildEndpointsAndRestart()
        {
            if (!IsOurSession) return;

            float t = A2BEffectPreview.Elapsed;
            bool wasPaused = A2BEffectPreview.Paused;
            StartPreview();
            if (wasPaused) A2BEffectPreview.Scrub(t);
        }

        private float SpanFromAsset() =>
            _asset != null && _asset.Definition != null
                ? _asset.Definition.ResolveSpan(A2BEffectGizmos.GizmoSeed)
                : 1f;

        // ---- Helpers ---------------------------------------------------------------------------

        private A2BEffectAsset CreateAsset()
        {
            string path = EditorUtility.SaveFilePanelInProject(
                "Create A2B Effect", "A2BEffect", "asset", "Where should the new effect asset live?");
            if (string.IsNullOrEmpty(path)) return null;

            var asset = CreateInstance<A2BEffectAsset>();
            AssetDatabase.CreateAsset(asset, path);
            AssetDatabase.SaveAssets();
            return asset;
        }

        /// <summary>
        /// An endpoint provider that resolves live from a delegate — see <see cref="BuildProviders"/>.
        /// When <see cref="AsScreen"/> is set (a virtual point on a Canvas effect) the handle's XY is
        /// reported as a screen position, so the canvas adapter places it without camera projection.
        /// </summary>
        private sealed class LivePointProvider : IA2BEndpointProvider
        {
            public Func<Vector3> Get;
            public bool AsScreen;

            public A2BEndpointSample Resolve()
            {
                if (Get == null) return A2BEndpointSample.Invalid;
                Vector3 p = Get();
                return AsScreen
                    ? A2BEndpointSample.AtScreen(new Vector3(p.x, p.y, 0f))
                    : A2BEndpointSample.At(p);
            }
        }
    }
}
