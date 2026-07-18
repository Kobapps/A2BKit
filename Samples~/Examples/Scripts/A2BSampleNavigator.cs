using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace A2BKit.Samples
{
    /// <summary>
    /// An in-game menu to hop between the A2BKit example scenes in one play session — press Play in any
    /// sample and a small panel appears top-left with a button per example plus Prev/Next.
    ///
    /// It bootstraps itself: nothing is wired into the sample scenes. A <see cref="RuntimeInitializeOnLoadMethod"/>
    /// runs once at startup, and only spawns the panel when the active scene is one of the examples — so
    /// importing the samples into a real project never injects this menu into the game's own scenes. The
    /// panel is <c>DontDestroyOnLoad</c>, so it survives the single-mode scene loads it triggers and one
    /// instance drives the whole tour.
    ///
    /// Runtime scene loading needs the scenes in Build Settings. In the Editor the navigator adds them for
    /// you the first time you switch (and via Tools ▸ A2BKit ▸ Samples ▸ Add Example Scenes to Build
    /// Settings); a standalone build must include them like any other scene.
    /// </summary>
    public sealed class A2BSampleNavigator : MonoBehaviour
    {
        // The examples, in order. Names must match the scene asset file names.
        private static readonly string[] Scenes =
        {
            "1 - Coin To Wallet",
            "2 - Coin Burst To Wallet",
            "3 - Floating Score Text",
            "4 - XP Orbs",
            "5 - Mesh Collect 3D",
            "6 - Particle Burst",
            "7 - Moving Target",
            "8 - Cross Space",
            "9 - Multiple Effects",
            "10 - UI Particles",
            "11 - UI Trails",
        };

        private static A2BSampleNavigator _instance;
        private bool _expanded;   // collapsed by default — a slim bar, so it never covers the scene's caption
        private Vector2 _scroll;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            // Only ever appears in a sample scene — never in a consumer's own game.
            if (System.Array.IndexOf(Scenes, SceneManager.GetActiveScene().name) < 0) return;

            // Unity's fake-null: a destroyed instance from a previous play session compares == null here,
            // so this correctly re-creates the panel even when domain reload on play is disabled.
            if (_instance != null) return;

            var go = new GameObject("[A2BKit Sample Navigator]");
            _instance = go.AddComponent<A2BSampleNavigator>();
            DontDestroyOnLoad(go);
        }

        private static int CurrentIndex => System.Array.IndexOf(Scenes, SceneManager.GetActiveScene().name);

        private void OnGUI()
        {
            // Anchored to the BOTTOM-left, out of the way of the scene captions (which sit top-left).
            // Default state is a slim one-row bar; the full list only appears — above the bar — when
            // the designer opens it, and is dismissed again with the same click.
            const float width = 300f;
            const float barHeight = 30f;
            int current = CurrentIndex;

            float barY = Screen.height - barHeight - 10f;

            if (_expanded)
            {
                float listHeight = Mathf.Min(Scenes.Length * 24f + 10f, Screen.height * 0.6f);
                var listRect = new Rect(10f, barY - listHeight - 4f, width, listHeight);
                GUILayout.BeginArea(listRect, GUI.skin.box);
                _scroll = GUILayout.BeginScrollView(_scroll);
                for (int i = 0; i < Scenes.Length; i++)
                {
                    bool isCurrent = i == current;
                    GUI.enabled = !isCurrent;                 // can't jump to where you already are
                    Color prev = GUI.color;
                    if (isCurrent) GUI.color = new Color(0.6f, 1f, 0.7f);
                    if (GUILayout.Button((isCurrent ? "▸ " : "   ") + Scenes[i], Left))
                        Go(i);
                    GUI.color = prev;
                    GUI.enabled = true;
                }
                GUILayout.EndScrollView();
                GUILayout.EndArea();
            }

            GUILayout.BeginArea(new Rect(10f, barY, width, barHeight), GUI.skin.box);
            using (new GUILayout.HorizontalScope())
            {
                if (GUILayout.Button("◀", Arrow)) Go(current - 1);

                string label = current >= 0 ? $"{current + 1}/{Scenes.Length}  {Short(Scenes[current])}" : "A2BKit Examples";
                if (GUILayout.Button((_expanded ? "▼ " : "▲ ") + label, Bar))
                    _expanded = !_expanded;

                if (GUILayout.Button("▶", Arrow)) Go(current + 1);
            }
            GUILayout.EndArea();
        }

        /// <summary>Drops the leading "N - " so the bar reads "3/9 Floating Score Text", not "3/9 3 - …".</summary>
        private static string Short(string sceneName)
        {
            int dash = sceneName.IndexOf(" - ", System.StringComparison.Ordinal);
            return dash >= 0 ? sceneName.Substring(dash + 3) : sceneName;
        }

        private void Go(int index)
        {
            index = (index % Scenes.Length + Scenes.Length) % Scenes.Length;   // wrap both ways
            string sceneName = Scenes[index];

#if UNITY_EDITOR
            EnsureInBuildSettings();
#endif
            if (Application.CanStreamedLevelBeLoaded(sceneName))
                SceneManager.LoadScene(sceneName);
            else
                Debug.LogWarning($"[A2BKit] Example scene '{sceneName}' is not in Build Settings, so it " +
                                 "cannot be loaded at runtime. Add it (Tools ▸ A2BKit ▸ Samples ▸ Add " +
                                 "Example Scenes to Build Settings), or it may not be imported.");
        }

        // ---- lazily-built GUI styles ----------------------------------------------------------

        private static GUIStyle _left, _bar, _arrow;
        private static GUIStyle Left => _left ??= new GUIStyle(GUI.skin.button) { alignment = TextAnchor.MiddleLeft };
        private static GUIStyle Bar => _bar ??= new GUIStyle(GUI.skin.button) { alignment = TextAnchor.MiddleLeft };
        private static GUIStyle Arrow => _arrow ??= new GUIStyle(GUI.skin.button) { fixedWidth = 30f };

#if UNITY_EDITOR
        [UnityEditor.MenuItem("Tools/A2BKit/Samples/Add Example Scenes to Build Settings")]
        private static void AddToBuildSettingsMenu()
        {
            EnsureInBuildSettings();
            Debug.Log("[A2BKit] Example scenes added to Build Settings — the in-game navigator can now " +
                      "switch between them at runtime.");
        }

        /// <summary>Adds any example scene assets that exist in the project but are missing from Build Settings.</summary>
        private static void EnsureInBuildSettings()
        {
            var scenes = new List<UnityEditor.EditorBuildSettingsScene>(UnityEditor.EditorBuildSettings.scenes);

            var present = new HashSet<string>();
            foreach (UnityEditor.EditorBuildSettingsScene s in scenes)
                present.Add(System.IO.Path.GetFileNameWithoutExtension(s.path));

            // One pass over the project's scene assets, matched by file name to our ordered list.
            var byName = new Dictionary<string, string>();
            foreach (string guid in UnityEditor.AssetDatabase.FindAssets("t:Scene"))
            {
                string path = UnityEditor.AssetDatabase.GUIDToAssetPath(guid);
                byName[System.IO.Path.GetFileNameWithoutExtension(path)] = path;
            }

            bool changed = false;
            foreach (string name in Scenes)
            {
                if (present.Contains(name)) continue;
                if (!byName.TryGetValue(name, out string path)) continue;   // not imported — skip quietly
                scenes.Add(new UnityEditor.EditorBuildSettingsScene(path, true));
                changed = true;
            }

            if (changed) UnityEditor.EditorBuildSettings.scenes = scenes.ToArray();
        }
#endif
    }
}
