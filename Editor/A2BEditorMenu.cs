using A2BKit.Unity;
using UnityEditor;
using UnityEngine;

namespace A2BKit.Editor
{
    /// <summary>
    /// The Tools/A2BKit menu.
    ///
    /// Everything here is reachable another way — the asset via Create/A2BKit/Effect, the overlay via
    /// its F3 key or by adding the component. That is the point: a discoverable menu is how someone
    /// who has not read the docs finds the package at all, and SM-3's five-minutes-to-first-effect is
    /// spent on whatever the user has to hunt for.
    /// </summary>
    internal static class A2BEditorMenu
    {
        /// <summary>
        /// Creates an effect asset in the selected folder, named and ready to rename.
        ///
        /// Deliberately goes through ProjectWindowUtil rather than AssetDatabase.CreateAsset at a
        /// hard-coded path: it lands where the user is actually looking and opens the rename field,
        /// which is what every other Create flow in the editor does. A fresh asset is playable as-is
        /// apart from its payload — the definition's field initialisers supply path, easing and
        /// emission (FR-1).
        /// </summary>
        [MenuItem("Tools/A2BKit/Create Effect Asset", false, 0)]
        private static void CreateEffectAsset()
        {
            var asset = ScriptableObject.CreateInstance<A2BEffectAsset>();
            ProjectWindowUtil.CreateAsset(asset, "A2BEffect.asset");
        }

        /// <summary>
        /// Shows or hides the runtime overlay (FR-22).
        ///
        /// Toggles an existing instance rather than calling Show() blindly, because Show() creates one
        /// if absent — so a user toggling twice would otherwise be unable to turn it off.
        /// </summary>
        [MenuItem("Tools/A2BKit/Toggle Debug Overlay", false, 20)]
        private static void ToggleDebugOverlay()
        {
            A2BDebugOverlay overlay = Object.FindAnyObjectByType<A2BDebugOverlay>(FindObjectsInactive.Include);
            if (overlay == null)
            {
                A2BDebugOverlay.Show();
                return;
            }

            overlay.Visible = !overlay.Visible;
        }

        /// <summary>
        /// The overlay reports live scheduler state and is a runtime object; spawning one in edit mode
        /// would leak a DontDestroyOnLoad GameObject into the user's scene to display nothing (NFR-5).
        /// </summary>
        [MenuItem("Tools/A2BKit/Toggle Debug Overlay", true)]
        private static bool ValidateToggleDebugOverlay() => Application.isPlaying;

        [MenuItem("Tools/A2BKit/Stop Effect Preview", false, 21)]
        private static void StopEffectPreview() => A2BEffectPreview.Stop();

        [MenuItem("Tools/A2BKit/Stop Effect Preview", true)]
        private static bool ValidateStopEffectPreview() => A2BEffectPreview.IsPlaying;

        /// <summary>
        /// Installs the AI skill (see <see cref="A2BSkillInstaller"/>). Also a button in the window;
        /// the menu item is here so it is findable without opening the window first.
        /// </summary>
        [MenuItem("Tools/A2BKit/Install AI Skill", false, 40)]
        private static void InstallSkill() => A2BSkillInstaller.Install();
    }
}
