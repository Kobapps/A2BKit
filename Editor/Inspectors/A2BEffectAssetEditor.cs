using A2BKit.Core;
using A2BKit.Unity;
using UnityEditor;
using UnityEngine;

namespace A2BKit.Editor
{
    /// <summary>
    /// The context-aware inspector for an effect asset (FR-19).
    ///
    /// "Context-aware" here means the inspector tells the designer the things the *chosen combination*
    /// makes true and the default inspector cannot: that an arc height is pixels on a Canvas and
    /// metres in World3D (AD-16), that a Z-bearing arc is invisible in World2D (AD-19), that
    /// AlignToVelocity turns a Canvas sprite edge-on. Each of those is a real trap that produces a
    /// working-but-wrong effect — the failure mode this package exists to remove, and one no amount
    /// of tooltips on individual fields can catch, because none of them is wrong on its own.
    ///
    /// Every problem surfaces as a HelpBox. Nothing here throws and nothing here is silently hidden:
    /// bad config is a normal authoring state that must be visible and fixable (AD-8).
    /// </summary>
    [CustomEditor(typeof(A2BEffectAsset))]
    public sealed class A2BEffectAssetEditor : UnityEditor.Editor
    {
        private const int PreviewHeight = 132;
        private const float PreviewPadding = 14f;

        // Reused across repaints for the same reason the gizmo buffer is (AD-3 does not bind editor
        // code, but this runs on every inspector repaint and the garbage is free to avoid).
        private static readonly Vector3[] GuiPoints = new Vector3[A2BEffectGizmos.SampleCount];

        private static readonly Color PreviewBackground = new Color(0.16f, 0.16f, 0.16f, 1f);
        private static readonly Color PreviewChordColor = new Color(1f, 1f, 1f, 0.14f);
        private static readonly Color PreviewCurveColor = new Color(0.35f, 0.85f, 1f, 1f);
        private static readonly Color PreviewOriginColor = new Color(0.4f, 1f, 0.5f, 1f);
        private static readonly Color PreviewDestinationColor = new Color(1f, 0.45f, 0.75f, 1f);

        private SerializedProperty _space;
        private SerializedProperty _payload;
        private SerializedProperty _duration;
        private SerializedProperty _durationJitter;
        private SerializedProperty _path;
        private SerializedProperty _easing;
        private SerializedProperty _emission;
        private SerializedProperty _scaleOverProgress;
        private SerializedProperty _colorOverProgress;
        private SerializedProperty _alignToVelocity;
        private SerializedProperty _useUnscaledTime;
        private SerializedProperty _endpointLostPolicy;
        private SerializedProperty _prewarmCount;

        private bool _previewExpanded = true;

        /// <summary>
        /// Chord length the preview draws against. Exposed rather than fixed because arc height is in
        /// working-space units (AD-16) and an asset holds no endpoints: "ArcHeight 2" is a gentle lob
        /// over a 20-unit chord and a near-vertical loop over a 1-unit one. A preview that silently
        /// picked one chord would misrepresent the other.
        /// </summary>
        private float _previewChord = 5f;

        private void OnEnable()
        {
            _space = serializedObject.FindProperty("Space");
            _payload = serializedObject.FindProperty("Payload");
            _duration = serializedObject.FindProperty("Definition.Duration");
            _durationJitter = serializedObject.FindProperty("Definition.DurationJitter");
            _path = serializedObject.FindProperty("Definition.Path");
            _easing = serializedObject.FindProperty("Definition.Easing");
            _emission = serializedObject.FindProperty("Definition.Emission");
            _scaleOverProgress = serializedObject.FindProperty("Definition.ScaleOverProgress");
            _colorOverProgress = serializedObject.FindProperty("Definition.ColorOverProgress");
            _alignToVelocity = serializedObject.FindProperty("Definition.AlignToVelocity");
            _useUnscaledTime = serializedObject.FindProperty("Definition.UseUnscaledTime");
            _endpointLostPolicy = serializedObject.FindProperty("Definition.EndpointLostPolicy");
            _prewarmCount = serializedObject.FindProperty("Definition.PrewarmCount");
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            var asset = (A2BEffectAsset)target;

            EditorGUI.BeginChangeCheck();

            DrawValidation(asset);
            DrawSpaceAndPayload(asset);
            DrawMotion(asset);
            DrawAppearance(asset);
            DrawBehaviour();
            DrawPreview(asset);

            if (EditorGUI.EndChangeCheck())
            {
                // Every scene view showing a player that references this asset is now stale. The
                // gizmo holds no cached curve, so a repaint is all it takes for the arc to follow
                // the slider live (FR-20).
                SceneView.RepaintAll();
            }

            serializedObject.ApplyModifiedProperties();
        }

        /// <summary>
        /// Surfaces the exact error Play would log, before the designer ever presses Play.
        /// Deliberately calls the asset's own <see cref="A2BEffectAsset.Validate"/> rather than
        /// re-checking the fields here: a second copy of the rules would drift, and the inspector
        /// would start certifying configs the runtime rejects.
        /// </summary>
        private void DrawValidation(A2BEffectAsset asset)
        {
            if (!asset.Validate(out string error))
                EditorGUILayout.HelpBox(error, MessageType.Error);
        }

        private void DrawSpaceAndPayload(A2BEffectAsset asset)
        {
            EditorGUILayout.PropertyField(_space);
            EditorGUILayout.PropertyField(_payload);

            // Payload is a [SerializeReference] port with no built-in implementations yet; an empty
            // picker is not the designer's mistake and must not read as one.
            if (_payload.managedReferenceValue == null)
            {
                EditorGUILayout.HelpBox(
                    "Choose a payload to decide what each item looks like. Motion previews without one, " +
                    "but Play needs it.",
                    MessageType.Info);
            }

            EditorGUILayout.Space();
        }

        private void DrawMotion(A2BEffectAsset asset)
        {
            EditorGUILayout.LabelField("Motion", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(_duration);
            EditorGUILayout.PropertyField(_durationJitter);
            EditorGUILayout.PropertyField(_path);
            EditorGUILayout.PropertyField(_easing);
            EditorGUILayout.PropertyField(_emission);

            DrawSpaceUnitsHint(asset);
            DrawWorld2DDepthWarnings(asset);

            EditorGUILayout.Space();
        }

        /// <summary>
        /// The AD-16 hint. Arc height, scatter radius and amplitude are unitless numbers on the asset;
        /// only the Space decides what they mean. Without this line "ArcHeight = 2" looks identical
        /// for a Canvas effect (2 pixels — invisible) and a World3D one (2 metres), and the designer
        /// concludes the arc is broken.
        /// </summary>
        private void DrawSpaceUnitsHint(A2BEffectAsset asset)
        {
            string units;
            switch (asset.Space)
            {
                case A2BSpaceKind.Canvas:
                    units = "canvas units (pixels for Overlay and Screen-Space-Camera canvases). " +
                            "An arc of 2 is two pixels — Canvas effects usually want tens or hundreds.";
                    break;
                case A2BSpaceKind.World2D:
                    units = "world units.";
                    break;
                default:
                    units = "world units (metres).";
                    break;
            }

            EditorGUILayout.HelpBox("Arc height, scatter radius and amplitude above are in " + units, MessageType.None);
        }

        /// <summary>
        /// World2D pins every item's Z to the root's (AD-19), so any Z the path or scatter produces is
        /// discarded. The effect still plays — it just silently ignores the parameter the designer is
        /// adjusting, which is the hardest kind of bug to spot: the slider moves and nothing happens.
        /// </summary>
        private void DrawWorld2DDepthWarnings(A2BEffectAsset asset)
        {
            if (asset.Space != A2BSpaceKind.World2D || asset.Definition == null) return;

            if (asset.Definition.Path is A2BBezierPath bezier)
            {
                Vector3 dir = bezier.ArcDirection;
                bool zDominant = Mathf.Abs(dir.z) > 0.001f &&
                                 Mathf.Abs(dir.z) >= Mathf.Max(Mathf.Abs(dir.x), Mathf.Abs(dir.y));
                if (zDominant)
                {
                    EditorGUILayout.HelpBox(
                        "World2D pins Z, so an arc pointing along Z is discarded and the path will look flat. " +
                        "Arc along X or Y instead.",
                        MessageType.Warning);
                }
            }

            if (asset.Definition.Emission is A2BBurstEmission burst &&
                burst.ScatterRadius > 0f &&
                Mathf.Abs(burst.ScatterAxisWeights.z) > 0.001f)
            {
                EditorGUILayout.HelpBox(
                    "World2D discards Z, so the Z component of Scatter Axis Weights does nothing. " +
                    "Set it to (1, 1, 0) to scatter in the plane.",
                    MessageType.Warning);
            }
        }

        private void DrawAppearance(A2BEffectAsset asset)
        {
            EditorGUILayout.LabelField("Appearance", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(_scaleOverProgress);
            EditorGUILayout.PropertyField(_colorOverProgress);
            EditorGUILayout.PropertyField(_alignToVelocity);

            // Velocity alignment builds a LookRotation along travel, which points the item's forward
            // (+Z) down the path. On a Canvas the path lies in the canvas plane, so forward ends up
            // parallel to it and the sprite turns edge-on — it vanishes. Correct code, invisible
            // result, and nothing in the field itself hints at it.
            if (asset.Space == A2BSpaceKind.Canvas && _alignToVelocity.boolValue)
            {
                EditorGUILayout.HelpBox(
                    "Align To Velocity rotates items to face along travel. In Canvas space that turns flat " +
                    "items edge-on to the camera and they disappear. Leave it off unless the payload is a mesh.",
                    MessageType.Warning);
            }

            EditorGUILayout.Space();
        }

        private void DrawBehaviour()
        {
            EditorGUILayout.LabelField("Behaviour", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(_useUnscaledTime);
            EditorGUILayout.PropertyField(_endpointLostPolicy);
            EditorGUILayout.PropertyField(_prewarmCount);
            EditorGUILayout.Space();
        }

        private void DrawPreview(A2BEffectAsset asset)
        {
            _previewExpanded = EditorGUILayout.Foldout(_previewExpanded, "Preview", true);
            if (!_previewExpanded) return;

            A2BEffectDefinition definition = asset.Definition;
            if (definition == null || definition.Path == null)
            {
                EditorGUILayout.HelpBox("Assign a path to preview the trajectory.", MessageType.Info);
                return;
            }

            _previewChord = EditorGUILayout.Slider(
                new GUIContent("Preview Chord", "Origin-to-destination distance the preview draws against, " +
                                                "in working-space units. Arc height is relative to this."),
                _previewChord, 0.1f, 50f);

            DrawPathPreview(definition);

            EditorGUILayout.HelpBox(
                "Drawn by sampling the same IA2BPath.Evaluate the runtime calls, so it cannot disagree with " +
                "what plays. Select an A2B Effect Player to preview it animating against real endpoints.",
                MessageType.None);
        }

        /// <summary>
        /// Draws the curve into an inspector rect by sampling the path itself (AD-13/AD-20) — the same
        /// buffer and the same walk the scene-view gizmo uses, so the two pictures are one picture.
        /// </summary>
        private void DrawPathPreview(A2BEffectDefinition definition)
        {
            Rect rect = GUILayoutUtility.GetRect(0f, PreviewHeight, GUILayout.ExpandWidth(true));
            if (Event.current.type != EventType.Repaint) return;

            EditorGUI.DrawRect(rect, PreviewBackground);

            var origin = new Vector3(0f, 0f, 0f);
            var destination = new Vector3(_previewChord, 0f, 0f);
            var ctx = new A2BPathContext(origin, destination, 0, 1, A2BEffectGizmos.GizmoSeed);
            Vector3[] samples = A2BEffectGizmos.SamplePath(definition.Path, in ctx);

            // Auto-fit, so a 200-unit canvas arc and a 2-unit world arc are both framed. Uniform
            // scale on both axes: a per-axis stretch would fit the curve while changing its shape,
            // which is the one thing this preview exists to show.
            float minX = float.MaxValue, maxX = float.MinValue, minY = float.MaxValue, maxY = float.MinValue;
            for (int i = 0; i < samples.Length; i++)
            {
                minX = Mathf.Min(minX, samples[i].x);
                maxX = Mathf.Max(maxX, samples[i].x);
                minY = Mathf.Min(minY, samples[i].y);
                maxY = Mathf.Max(maxY, samples[i].y);
            }

            float spanX = Mathf.Max(1e-4f, maxX - minX);
            float spanY = Mathf.Max(1e-4f, maxY - minY);
            float scale = Mathf.Min((rect.width - PreviewPadding * 2f) / spanX, (rect.height - PreviewPadding * 2f) / spanY);
            float centreX = (minX + maxX) * 0.5f;
            float centreY = (minY + maxY) * 0.5f;

            for (int i = 0; i < samples.Length; i++)
            {
                // GUI y grows downward while the path's y grows upward: without the negation an
                // upward arc would draw as a downward sag.
                GuiPoints[i] = new Vector3(
                    rect.center.x + (samples[i].x - centreX) * scale,
                    rect.center.y - (samples[i].y - centreY) * scale,
                    0f);
            }

            Vector3 first = GuiPoints[0];
            Vector3 last = GuiPoints[samples.Length - 1];

            Handles.color = PreviewChordColor;
            Handles.DrawAAPolyLine(1f, first, last);

            Handles.color = PreviewCurveColor;
            Handles.DrawAAPolyLine(2.5f, A2BEffectGizmos.SampleCount, GuiPoints);

            DrawPreviewEndpoint(first, PreviewOriginColor);
            DrawPreviewEndpoint(last, PreviewDestinationColor);
        }

        private static void DrawPreviewEndpoint(Vector3 guiPoint, Color color)
        {
            const float size = 6f;
            EditorGUI.DrawRect(new Rect(guiPoint.x - size * 0.5f, guiPoint.y - size * 0.5f, size, size), color);
        }
    }
}
