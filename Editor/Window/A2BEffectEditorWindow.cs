using System;
using System.Collections.Generic;
using A2BKit.Core;
using A2BKit.Unity;
using UnityEditor;
using UnityEngine;

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
        [SerializeField] private bool _showDefinition = true;

        private Vector2 _scroll;
        private UnityEditor.Editor _definitionEditor;

        // Reused across scene repaints so the overlay does not allocate a fresh list every frame.
        private readonly List<A2BVisualState> _liveItems = new List<A2BVisualState>(64);

        // Live providers that resolve the CURRENT endpoint each frame by delegating back to the window
        // (GetOrigin/GetDestination). Because they read live, a restart never invalidates them, a moved
        // scene object is followed, and a dragged virtual point is followed — one code path for all of
        // it. World space matches what the scene view draws (AD-16 for scatter).
        private IA2BEndpointProvider _originProvider;
        private IA2BEndpointProvider _destinationProvider;

        private GUIStyle _header;

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

            if (_definitionEditor != null)
            {
                DestroyImmediate(_definitionEditor);
                _definitionEditor = null;
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

        private void OnGUI()
        {
            EnsureStyles();

            _scroll = EditorGUILayout.BeginScrollView(_scroll);

            DrawAssetSection();

            if (_asset == null)
            {
                EditorGUILayout.HelpBox(
                    "Assign an A2B Effect asset to preview and edit it here, or create a new one.",
                    MessageType.Info);
                EditorGUILayout.EndScrollView();
                return;
            }

            EditorGUILayout.Space();
            DrawEndpointsSection();

            EditorGUILayout.Space();
            DrawTransportSection();

            EditorGUILayout.Space();
            DrawDefinitionSection();

            EditorGUILayout.EndScrollView();
        }

        private void DrawAssetSection()
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                EditorGUILayout.LabelField("Effect", _header);

                using (var change = new EditorGUI.ChangeCheckScope())
                using (new EditorGUILayout.HorizontalScope())
                {
                    var picked = (A2BEffectAsset)EditorGUILayout.ObjectField(
                        _asset, typeof(A2BEffectAsset), allowSceneObjects: false);

                    if (GUILayout.Button("New", GUILayout.Width(50f)))
                    {
                        var created = CreateAsset();
                        if (created != null) picked = created;
                    }

                    if (change.changed && picked != _asset)
                    {
                        // Switching assets under a live preview would keep animating the old one; stop
                        // first so the new asset starts from a clean session on the next Play.
                        if (IsOurSession) A2BEffectPreview.Stop();
                        _asset = picked;
                        RebuildDefinitionEditor();
                    }
                }
            }
        }

        private void DrawEndpointsSection()
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                EditorGUILayout.LabelField("Endpoints", _header);
                DrawEndpoint("Origin", ref _originMode, ref _originObject, ref _originPoint);
                EditorGUILayout.Space(2f);
                DrawEndpoint("Destination", ref _destinationMode, ref _destinationObject, ref _destinationPoint);
            }
        }

        private void DrawEndpoint(string label, ref EndpointMode mode, ref Transform obj, ref Vector3 point)
        {
            using (var change = new EditorGUI.ChangeCheckScope())
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.LabelField(label, GUILayout.Width(78f));
                    mode = (EndpointMode)EditorGUILayout.EnumPopup(mode);
                }

                if (mode == EndpointMode.SceneObject)
                {
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        obj = (Transform)EditorGUILayout.ObjectField(
                            obj, typeof(Transform), allowSceneObjects: true);

                        using (new EditorGUI.DisabledScope(Selection.activeTransform == null))
                        {
                            if (GUILayout.Button("Use Selection", GUILayout.Width(100f)))
                                obj = Selection.activeTransform;
                        }
                    }
                }
                else
                {
                    point = EditorGUILayout.Vector3Field(GUIContent.none, point);
                }

                // A mode/object/point change can flip which endpoint TYPE is used (world vs screen), so
                // rebuild the providers and restart — a live delegate swap is not enough. Scene-handle
                // dragging of a virtual point takes the smooth path elsewhere and does not come through
                // here, so this never fires per-frame.
                if (change.changed) RebuildEndpointsAndRestart();
            }
        }

        private void DrawTransportSection()
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                EditorGUILayout.LabelField("Preview", _header);

                using (new EditorGUILayout.HorizontalScope())
                {
                    if (!IsOurSession)
                    {
                        if (GUILayout.Button("▶ Play", GUILayout.Height(24f))) StartPreview();
                    }
                    else
                    {
                        if (GUILayout.Button("⟲ Restart", GUILayout.Height(24f))) StartPreview();

                        bool paused = A2BEffectPreview.Paused;
                        if (GUILayout.Button(paused ? "▶ Resume" : "❚❚ Pause", GUILayout.Height(24f)))
                            A2BEffectPreview.SetPaused(!paused);

                        if (GUILayout.Button("■ Stop", GUILayout.Height(24f))) A2BEffectPreview.Stop();
                    }
                }

                // Scrub bar. Span is the true end-to-end length (stagger + jitter tail), not Duration.
                float span = Mathf.Max(0.0001f, A2BEffectPreview.Span > 0f ? A2BEffectPreview.Span : SpanFromAsset());
                float elapsed = IsOurSession ? Mathf.Clamp(A2BEffectPreview.Elapsed, 0f, span) : 0f;

                using (var change = new EditorGUI.ChangeCheckScope())
                {
                    float scrubbed = EditorGUILayout.Slider("Time", elapsed, 0f, span);
                    if (change.changed)
                    {
                        if (!IsOurSession) StartPreview();
                        A2BEffectPreview.Scrub(scrubbed);
                    }
                }

                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.LabelField(
                        IsOurSession
                            ? $"{elapsed:0.00}s / {span:0.00}s"
                            : $"Length: {span:0.00}s",
                        EditorStyles.miniLabel);
                    GUILayout.FlexibleSpace();
                    EditorGUILayout.LabelField(
                        IsOurSession ? $"Items in flight: {A2BEffectPreview.ActiveItemCount}" : "",
                        EditorStyles.miniLabel, GUILayout.Width(130f));
                }

                using (var change = new EditorGUI.ChangeCheckScope())
                {
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        _loop = EditorGUILayout.ToggleLeft("Loop", _loop, GUILayout.Width(60f));
                        _speed = EditorGUILayout.Slider("Speed", _speed, 0.1f, 3f);
                    }

                    if (change.changed)
                    {
                        A2BEffectPreview.Loop = _loop;
                        A2BEffectPreview.Speed = _speed;
                    }
                }

                using (var change = new EditorGUI.ChangeCheckScope())
                {
                    _showPayloadVisuals = EditorGUILayout.ToggleLeft(
                        "Show payload visuals (real sprites/meshes in Scene + Game view)", _showPayloadVisuals);

                    // The mode is chosen when a session starts, so a live toggle only takes effect on a
                    // restart. Restarting immediately makes the switch feel direct rather than deferred.
                    if (change.changed && IsOurSession) StartPreview();
                }

                if (_showPayloadVisuals && (_asset == null || _asset.Payload == null))
                    EditorGUILayout.HelpBox(
                        "This effect has no payload, so the preview falls back to motion dots. Assign a " +
                        "Payload in the definition below to see the real visual.",
                        MessageType.None);
            }
        }

        private void DrawDefinitionSection()
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                _showDefinition = EditorGUILayout.Foldout(_showDefinition, "Effect Definition", true);
                if (!_showDefinition) return;

                if (_definitionEditor == null || _definitionEditor.target != _asset)
                    RebuildDefinitionEditor();

                if (_definitionEditor == null) return;

                using (var change = new EditorGUI.ChangeCheckScope())
                {
                    _definitionEditor.OnInspectorGUI();

                    // A live edit shows immediately: a looping preview picks it up on its next restart;
                    // a held scrub frame re-simulates so the change is visible without touching Play.
                    if (change.changed) ResimulateIfPaused();
                }
            }
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

            int removeAt = -1;
            int insertAfter = -2;   // -2 = none; -1 = before first (origin→cp0)

            // Move + delete handles, one per control point.
            for (int i = 0; i < points.Count; i++)
            {
                A2BSplineControlPoint cp = points[i];
                Vector3 world = Vector3.LerpUnclamped(origin, destination, cp.Along) + cp.Offset * len;

                Handles.color = new Color(A2BEffectGizmos.PathColor.r, A2BEffectGizmos.PathColor.g,
                    A2BEffectGizmos.PathColor.b, 0.4f);
                Handles.DrawDottedLine(Vector3.LerpUnclamped(origin, destination, cp.Along), world, 2f);

                using (var change = new EditorGUI.ChangeCheckScope())
                {
                    float size = HandleUtility.GetHandleSize(world) * 0.09f;
                    Handles.color = A2BEffectGizmos.PathColor;
                    Vector3 moved = Handles.FreeMoveHandle(world, size, Vector3.zero, Handles.SphereHandleCap);
                    Handles.Label(world + Vector3.up * HandleUtility.GetHandleSize(world) * 0.18f, $"P{i + 1}");
                    if (change.changed) ApplySplineControl(spline, i, origin, destination, moved);
                }

                // Delete button — only when a point would remain, so the curve never becomes empty here.
                float hs = HandleUtility.GetHandleSize(world);
                Vector3 delPos = world + (Vector3.right + Vector3.up) * hs * 0.16f;
                Handles.color = A2BEffectGizmos.WarningColor;
                if (Handles.Button(delPos, Quaternion.identity, hs * 0.05f, hs * 0.07f, Handles.DotHandleCap))
                    removeAt = i;
            }

            // Insert buttons at the midpoint of each control-polygon segment.
            insertAfter = DrawSplineInsertButtons(points, origin, destination, len);

            if (removeAt >= 0)
            {
                Undo.RecordObject(_asset, "Remove A2B Bézier Point");
                points.RemoveAt(removeAt);
                CommitEdit();
            }
            else if (insertAfter >= -1)
            {
                InsertSplinePoint(spline, insertAfter, origin, destination, len);
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
            if (_definitionEditor != null) _definitionEditor.Repaint();
            ResimulateIfPaused();
            Repaint();
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

            using (var change = new EditorGUI.ChangeCheckScope())
            {
                float size = HandleUtility.GetHandleSize(control) * 0.1f;
                Handles.color = A2BEffectGizmos.PathColor;
                Vector3 moved = Handles.FreeMoveHandle(control, size, Vector3.zero, Handles.SphereHandleCap);
                Handles.Label(control + Vector3.up * HandleUtility.GetHandleSize(control) * 0.2f,
                    $"Arc {bezier.ArcHeight:0.##}");

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
            if (_definitionEditor != null) _definitionEditor.Repaint();
            ResimulateIfPaused();
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
                        ResimulateIfPaused();
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
                        ResimulateIfPaused();
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

        private void RebuildDefinitionEditor()
        {
            if (_definitionEditor != null)
            {
                DestroyImmediate(_definitionEditor);
                _definitionEditor = null;
            }

            if (_asset != null)
                _definitionEditor = UnityEditor.Editor.CreateEditor(_asset);
        }

        private void EnsureStyles()
        {
            _header ??= new GUIStyle(EditorStyles.boldLabel);
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
