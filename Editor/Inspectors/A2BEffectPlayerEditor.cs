using A2BKit.Core;
using A2BKit.Unity;
using UnityEditor;
using UnityEngine;

namespace A2BKit.Editor
{
    /// <summary>
    /// The inspector for the drop-on player (FR-19, FR-21).
    ///
    /// Two jobs. First, hide what the chosen Space cannot use: Canvas Root and Source Camera are
    /// meaningless for a World3D effect, and leaving them visible invites someone to fill them in and
    /// then wonder for an afternoon why they do nothing.
    ///
    /// Second, tell the truth about Play. Play() needs a scheduler tick to advance it, and the tick
    /// comes from A2BRunner's LateUpdate (AD-6) — which does not run in edit mode. A Play button that
    /// is live outside play mode would appear to do nothing at all. Rather than let the user conclude
    /// the package is broken, the button is disabled and says why, and the edit-mode path is the
    /// preview instead (FR-21), which drives the same simulation off a manual clock (AD-12).
    /// </summary>
    [CustomEditor(typeof(A2BEffectPlayer))]
    public sealed class A2BEffectPlayerEditor : UnityEditor.Editor
    {
        private SerializedProperty _effect;
        private SerializedProperty _origin;
        private SerializedProperty _destination;
        private SerializedProperty _canvasRoot;
        private SerializedProperty _sourceCamera;
        private SerializedProperty _playOnEnable;
        private SerializedProperty _onStarted;
        private SerializedProperty _onFirstItemArrived;
        private SerializedProperty _onItemArrived;
        private SerializedProperty _onCompleted;
        private SerializedProperty _onCancelled;

        private bool _eventsExpanded;
        private string _playText = string.Empty;

        private void OnEnable()
        {
            _effect = serializedObject.FindProperty("Effect");
            _origin = serializedObject.FindProperty("Origin");
            _destination = serializedObject.FindProperty("Destination");
            _canvasRoot = serializedObject.FindProperty("CanvasRoot");
            _sourceCamera = serializedObject.FindProperty("SourceCamera");
            _playOnEnable = serializedObject.FindProperty("PlayOnEnable");
            _onStarted = serializedObject.FindProperty("OnStartedEvent");
            _onFirstItemArrived = serializedObject.FindProperty("OnFirstItemArrivedEvent");
            _onItemArrived = serializedObject.FindProperty("OnItemArrivedEvent");
            _onCompleted = serializedObject.FindProperty("OnCompletedEvent");
            _onCancelled = serializedObject.FindProperty("OnCancelledEvent");
        }

        /// <summary>
        /// Live counters are worthless if they only refresh when the mouse moves over the inspector —
        /// the whole point is watching ArrivedCount climb. Constant repaint costs editor CPU, so it is
        /// asked for only while there is something moving to watch.
        /// </summary>
        public override bool RequiresConstantRepaint()
        {
            var player = (A2BEffectPlayer)target;
            if (player == null) return false;
            return (Application.isPlaying && player.IsPlaying) || A2BEffectPreview.IsPlaying;
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            var player = (A2BEffectPlayer)target;

            EditorGUI.BeginChangeCheck();

            DrawEffect(player);
            DrawEndpoints(player);
            DrawSpaceSpecific(player);

            EditorGUILayout.PropertyField(_playOnEnable);
            EditorGUILayout.Space();

            DrawEvents();

            if (EditorGUI.EndChangeCheck())
                SceneView.RepaintAll();

            serializedObject.ApplyModifiedProperties();

            EditorGUILayout.Space();
            DrawPlayControls(player);
            DrawPreviewControls(player);
            DrawLiveState(player);
        }

        private void DrawEffect(A2BEffectPlayer player)
        {
            EditorGUILayout.PropertyField(_effect);

            if (player.Effect == null)
            {
                EditorGUILayout.HelpBox("Assign an A2B Effect Asset. Play does nothing without one.", MessageType.Warning);
                return;
            }

            // The asset's own rules, asked of the asset — never re-implemented here (AD-8: the
            // inspector reports what Play would report, it does not form a second opinion).
            if (!player.Effect.Validate(out string error))
                EditorGUILayout.HelpBox(player.Effect.name + ": " + error, MessageType.Error);
        }

        private void DrawEndpoints(A2BEffectPlayer player)
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Endpoints", EditorStyles.boldLabel);

            EditorGUILayout.PropertyField(_origin);
            if (player.Origin == null)
            {
                EditorGUILayout.HelpBox(
                    "Origin is empty, so items spawn from this GameObject's transform.",
                    MessageType.None);
            }

            EditorGUILayout.PropertyField(_destination);

            // Destination has no fallback: without it Play logs an error and returns an invalid
            // handle (AD-8). Say so here rather than at the first press in a build.
            if (player.Destination == null)
            {
                EditorGUILayout.HelpBox(
                    "No destination assigned. Play will refuse and return an invalid handle — an effect " +
                    "with nowhere to arrive has no ItemArrived to raise.",
                    MessageType.Warning);
            }
        }

        /// <summary>Only the Canvas space reads these; for World2D/World3D they are dead controls.</summary>
        private void DrawSpaceSpecific(A2BEffectPlayer player)
        {
            if (player.Effect == null || player.Effect.Space != A2BSpaceKind.Canvas)
                return;

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Canvas Space", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(_canvasRoot);

            if (player.CanvasRoot == null)
            {
                // AD-14 is a deliberate, and surprising, default. A designer who does not know items
                // land on an A2BKit-owned canvas will go looking for them under their HUD.
                EditorGUILayout.HelpBox(
                    "Empty: items go on a dedicated A2BKit canvas instead of your HUD's, so moving items " +
                    "don't force a batch rebuild of every element on it. Override only if you've profiled " +
                    "the other way.",
                    MessageType.None);
            }

            EditorGUILayout.PropertyField(_sourceCamera);
            if (player.SourceCamera == null)
                EditorGUILayout.HelpBox("Empty: world origins project through Camera.main.", MessageType.None);
        }

        private void DrawEvents()
        {
            _eventsExpanded = EditorGUILayout.Foldout(_eventsExpanded, "Events", true);
            if (!_eventsExpanded) return;

            EditorGUILayout.HelpBox(
                "UnityEvent dispatch allocates. That's the honest cost of the designer path — code that " +
                "needs the zero-allocation guarantee registers an IA2BEffectListener on the handle.",
                MessageType.None);

            EditorGUILayout.PropertyField(_onStarted);
            EditorGUILayout.PropertyField(_onFirstItemArrived);
            EditorGUILayout.PropertyField(_onItemArrived);
            EditorGUILayout.PropertyField(_onCompleted);
            EditorGUILayout.PropertyField(_onCancelled);
        }

        private void DrawPlayControls(A2BEffectPlayer player)
        {
            EditorGUILayout.LabelField("Play", EditorStyles.boldLabel);

            bool canPlay = Application.isPlaying && player.Effect != null && player.Destination != null;

            _playText = EditorGUILayout.TextField(
                new GUIContent("Text", "Optional per-item text passed to Play, e.g. \"+250\"."), _playText);

            using (new EditorGUI.DisabledScope(!canPlay))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("Play"))
                        player.Play(string.IsNullOrEmpty(_playText) ? null : _playText);

                    using (new EditorGUI.DisabledScope(!player.IsPlaying))
                    {
                        if (GUILayout.Button("Cancel"))
                            player.Cancel();
                    }
                }
            }

            if (!Application.isPlaying)
            {
                EditorGUILayout.HelpBox(
                    "Play needs play mode: effects advance from the runner's LateUpdate tick, which only " +
                    "runs while the game is playing. Use Preview below to see the motion in edit mode.",
                    MessageType.Info);
            }
        }

        /// <summary>
        /// The edit-mode answer (FR-21). It runs the real scheduler off a manual clock (AD-12), so it
        /// is a rehearsal of the effect rather than an impression of it.
        /// </summary>
        private void DrawPreviewControls(A2BEffectPlayer player)
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Preview", EditorStyles.boldLabel);

            bool previewingThis = A2BEffectPreview.IsPlaying && ReferenceEquals(A2BEffectPreview.Context, player);
            bool canPreview = !Application.isPlaying && player.Effect != null && player.Destination != null;

            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledScope(!canPreview))
                {
                    if (GUILayout.Button(previewingThis ? "Restart Preview" : "Preview In Scene View"))
                    {
                        A2BEffectPreview.Play(
                            player.Effect,
                            player.Origin != null ? player.Origin : player.transform,
                            player.Destination,
                            player);
                    }
                }

                using (new EditorGUI.DisabledScope(!A2BEffectPreview.IsPlaying))
                {
                    if (GUILayout.Button("Stop Preview"))
                        A2BEffectPreview.Stop();
                }
            }

            if (previewingThis)
            {
                EditorGUILayout.LabelField("Preview time", A2BEffectPreview.Elapsed.ToString("0.00") + " s");
                EditorGUILayout.LabelField("Preview items", A2BEffectPreview.ActiveItemCount.ToString());
            }
            else if (canPreview)
            {
                EditorGUILayout.HelpBox(
                    "Previews in the scene view without entering play mode. It drives the same scheduler the " +
                    "game does, off a manual clock — and creates no GameObjects, so there is nothing to leak.",
                    MessageType.None);
            }
        }

        /// <summary>
        /// The running effect, read straight off the handle. ItemCount and ArrivedCount answer the two
        /// questions a stuck effect raises — did it spawn, and is anything landing — without a
        /// profiler or a breakpoint (UJ-4).
        /// </summary>
        private void DrawLiveState(A2BEffectPlayer player)
        {
            if (!Application.isPlaying) return;

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Live State", EditorStyles.boldLabel);

            A2BEffectHandle handle = player.Handle;

            using (new EditorGUI.DisabledScope(true))
            {
                EditorGUILayout.Toggle("Handle Valid", handle.IsValid);
                EditorGUILayout.IntField("Item Count", handle.ItemCount);
                EditorGUILayout.IntField("Arrived Count", handle.ArrivedCount);
            }

            if (!handle.IsValid)
            {
                // A stale handle is the designed state after completion, not a fault (AD-7). Saying
                // so stops it being read as one.
                EditorGUILayout.HelpBox("Nothing playing. The handle goes invalid the moment the effect ends.",
                    MessageType.None);
            }
        }
    }
}
