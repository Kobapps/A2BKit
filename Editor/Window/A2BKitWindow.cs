using A2BKit.Core;
using System.IO;
using A2BKit.Unity;
using UnityEditor;
using PackageInfo = UnityEditor.PackageManager.PackageInfo;
using UnityEngine;

namespace A2BKit.Editor
{
    /// <summary>
    /// The A2BKit dashboard: live runtime diagnostics, the common editor tools in one place, and the
    /// button that installs the AI skill.
    ///
    /// It exists so the package has a front door. The scattered menu items (Create Effect, Toggle
    /// Overlay) still work, but a window is where someone looks first, and it is the only place the
    /// runtime pool/effect counts are visible without entering Play and squinting at the overlay.
    /// </summary>
    public sealed class A2BKitWindow : EditorWindow
    {
        private const string DocsUrl = "https://github.com/Kobapps/A2BKit#readme";

        private Vector2 _scroll;
        private GUIStyle _header;
        private GUIStyle _mono;

        [MenuItem("Tools/A2BKit/A2BKit Window", false, -10)]
        public static void Open()
        {
            var window = GetWindow<A2BKitWindow>();
            window.titleContent = new GUIContent("A2BKit");
            window.minSize = new Vector2(320f, 360f);
            window.Show();
        }

        /// <summary>Repaint every frame ONLY while playing, so the live counters tick without a
        /// constant-repaint tax on the editor at rest.</summary>
        private void OnInspectorUpdate()
        {
            if (Application.isPlaying) Repaint();
        }

        private void EnsureStyles()
        {
            _header ??= new GUIStyle(EditorStyles.boldLabel) { fontSize = 12 };
            _mono ??= new GUIStyle(EditorStyles.label)
            {
                font = EditorStyles.miniFont,
                richText = false,
                wordWrap = false,
            };
        }

        private void OnGUI()
        {
            EnsureStyles();
            _scroll = EditorGUILayout.BeginScrollView(_scroll);

            DrawDebugSection();
            EditorGUILayout.Space(6);
            DrawToolsSection();
            EditorGUILayout.Space(6);
            DrawSkillSection();
            EditorGUILayout.Space(6);
            DrawDocsSection();

            EditorGUILayout.EndScrollView();
        }

        // ---- Debug -----------------------------------------------------------------------------

        private void DrawDebugSection()
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                EditorGUILayout.LabelField("Runtime", _header);

                if (!Application.isPlaying)
                {
                    EditorGUILayout.HelpBox("Enter Play mode to see live effects and pool occupancy.",
                        MessageType.None);
                    return;
                }

                if (!A2BRunner.Exists)
                {
                    // Do NOT touch A2BRunner.Scheduler here — the getter bootstraps a runner, and
                    // spawning one just to look at it would make observing the system change it.
                    EditorGUILayout.HelpBox("No effect has played yet this session.", MessageType.None);
                    return;
                }

                A2BScheduler scheduler = A2BRunner.Scheduler;
                Row("Active effects", scheduler.ActiveEffectCount.ToString());
                Row("Items in flight", scheduler.ActiveItemCount.ToString());
                Row("Effect slots (pooled)", scheduler.SlotCapacity.ToString());

                EditorGUILayout.Space(4);
                using (new EditorGUILayout.HorizontalScope())
                {
                    A2BDebugOverlay overlay = Object.FindAnyObjectByType<A2BDebugOverlay>(FindObjectsInactive.Include);
                    bool on = overlay != null && overlay.Visible;
                    if (GUILayout.Button(on ? "Hide In-Game Overlay" : "Show In-Game Overlay"))
                    {
                        if (overlay == null) A2BDebugOverlay.Show();
                        else overlay.Visible = !overlay.Visible;
                    }

                    using (new EditorGUI.DisabledScope(!Application.isPlaying))
                    {
                        if (GUILayout.Button("Cancel All Effects"))
                            A2BRunner.Scheduler.CancelAll();
                    }
                }
            }
        }

        private void Row(string label, string value)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField(label, GUILayout.Width(180f));
                EditorGUILayout.LabelField(value, EditorStyles.boldLabel);
            }
        }

        // ---- Tools -----------------------------------------------------------------------------

        private void DrawToolsSection()
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                EditorGUILayout.LabelField("Tools", _header);

                if (GUILayout.Button("Create Effect Asset"))
                {
                    var asset = ScriptableObject.CreateInstance<A2BEffectAsset>();
                    ProjectWindowUtil.CreateAsset(asset, "A2BEffect.asset");
                }

                using (new EditorGUI.DisabledScope(!A2BEffectPreview.IsPlaying))
                {
                    if (GUILayout.Button("Stop Effect Preview"))
                        A2BEffectPreview.Stop();
                }
            }
        }

        // ---- AI Skill --------------------------------------------------------------------------

        private void DrawSkillSection()
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                EditorGUILayout.LabelField("AI Skill", _header);
                EditorGUILayout.LabelField(
                    "Teach an AI assistant this package's API, patterns and gotchas. Installs to " +
                    ".claude/skills/a2bkit so Claude Code picks it up in this project.",
                    EditorStyles.wordWrappedMiniLabel);

                A2BSkillInstaller.State state = A2BSkillInstaller.GetState();
                string version = A2BSkillInstaller.PackageVersion();

                switch (state)
                {
                    case A2BSkillInstaller.State.SourceMissing:
                        EditorGUILayout.HelpBox(
                            "The skill file is missing from the package (Documentation~/Skill/SKILL.md).",
                            MessageType.Error);
                        break;
                    case A2BSkillInstaller.State.UpToDate:
                        EditorGUILayout.HelpBox("Installed and up to date (v" + version + ").", MessageType.Info);
                        break;
                    case A2BSkillInstaller.State.UpdateAvailable:
                        EditorGUILayout.HelpBox(
                            "Installed, but the package ships a newer version (v" + version + "). Reinstall to update.",
                            MessageType.Warning);
                        break;
                    case A2BSkillInstaller.State.NotInstalled:
                        EditorGUILayout.HelpBox("Not installed in this project.", MessageType.None);
                        break;
                }

                using (new EditorGUI.DisabledScope(state == A2BSkillInstaller.State.SourceMissing))
                using (new EditorGUILayout.HorizontalScope())
                {
                    string label = state == A2BSkillInstaller.State.NotInstalled ? "Install AI Skill"
                        : state == A2BSkillInstaller.State.UpdateAvailable ? "Update AI Skill"
                        : "Reinstall AI Skill";

                    if (GUILayout.Button(label))
                    {
                        if (A2BSkillInstaller.Install())
                            ShowNotification(new GUIContent("A2BKit skill installed"));
                    }

                    using (new EditorGUI.DisabledScope(state == A2BSkillInstaller.State.NotInstalled))
                    {
                        if (GUILayout.Button("Reveal", GUILayout.Width(70f)))
                            A2BSkillInstaller.RevealInstalled();
                    }
                }
            }
        }

        // ---- Docs ------------------------------------------------------------------------------

        private void DrawDocsSection()
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                EditorGUILayout.LabelField("Docs", _header);
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("README (online)")) Application.OpenURL(DocsUrl);
                    if (GUILayout.Button("Extending guide")) RevealPackagedDoc("extending.md");
                }
            }
        }

        /// <summary>Reveals a file under the package's Documentation~ folder (not an imported asset).</summary>
        private static void RevealPackagedDoc(string fileName)
        {
            PackageInfo package = PackageInfo.FindForAssembly(typeof(A2BKitWindow).Assembly);
            if (package == null) return;

            string path = Path.Combine(package.resolvedPath, "Documentation~", fileName);
            if (File.Exists(path)) EditorUtility.RevealInFinder(path);
            else Application.OpenURL(DocsUrl);
        }
    }
}
