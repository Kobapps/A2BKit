using System;
using A2BKit.Core;
using UnityEditor;
using UnityEngine;

namespace A2BKit.Editor
{
    /// <summary>
    /// Destroys A2BKit scaffolding that outlived a play session.
    ///
    /// The bug this closes: a root created with <see cref="HideFlags.DontSave"/> is, per Unity's own
    /// definition of the flag, "not destroyed when a new scene is loaded" — and leaving play mode is a
    /// scene reload. Anything the package created that way survived into edit mode belonging to no
    /// loaded scene: still rendered by its overlay canvas, absent from the Hierarchy, and therefore
    /// impossible to select or delete. A pile of "A2B Text Item"s stuck on screen between plays was the
    /// visible half of it. The roots themselves no longer carry DontSave in play mode, so nothing should
    /// reach this sweep any more.
    ///
    /// It stays for two reasons: it clears objects already stranded by a version that predates the fix
    /// (a domain reload does not free them — only the editor restarting, or this), and it makes the
    /// guarantee structural rather than a property of five separate call sites staying correct.
    ///
    /// The match is deliberately narrow. Only parentless objects whose name starts with the package's
    /// "[A2B" root prefix are considered, and each must ALSO be either scene-less or flagged
    /// <see cref="HideFlags.DontSaveInEditor"/> — a user's own GameObject named "[A2B ...]" sits in a
    /// real scene with no hide flags and can never match. Assets are excluded outright.
    /// </summary>
    [InitializeOnLoad]
    internal static class A2BStrayObjectCleanup
    {
        /// <summary>Shared by every root the package creates: the Runner, canvases, world roots, overlay.</summary>
        private const string RootPrefix = "[A2B";

        static A2BStrayObjectCleanup()
        {
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
        }

        private static void OnPlayModeStateChanged(PlayModeStateChange change)
        {
            // EnteredEditMode, not ExitingPlayMode: the play-mode scenes are gone by then, so anything
            // still standing is exactly what failed to be torn down.
            if (change != PlayModeStateChange.EnteredEditMode) return;
            Sweep(reportEmpty: false);
        }

        /// <summary>
        /// Destroys every stray root and returns how many went. Stops any live preview first: a
        /// real-payload preview owns a DontSave stage of its own that matches the same criteria, and
        /// tearing it down through <see cref="A2BEffectPreview.Stop"/> disposes its pools instead of
        /// pulling the GameObject out from under them.
        /// </summary>
        internal static int Sweep(bool reportEmpty)
        {
            A2BEffectPreview.Stop();

            // FindObjectsOfTypeAll, not FindObjectsByType: the strays are in no scene and some are
            // hidden, and this is the only call that returns them.
            GameObject[] all = Resources.FindObjectsOfTypeAll<GameObject>();

            int destroyed = 0;
            for (int i = 0; i < all.Length; i++)
            {
                GameObject go = all[i];
                if (go == null || !IsStrayRoot(go)) continue;

                UnityEngine.Object.DestroyImmediate(go);
                destroyed++;
            }

            if (destroyed > 0)
                A2BLog.Info(null, "Removed " + destroyed + " stray A2BKit object(s) left over from play mode.");
            else if (reportEmpty)
                A2BLog.Info(null, "No stray A2BKit objects found.");

            return destroyed;
        }

        private static bool IsStrayRoot(GameObject go)
        {
            // Only roots. Pools and items hang under one and die with it, so walking children would
            // just be a slower way to destroy the same objects.
            if (go.transform.parent != null) return false;

            if (!go.name.StartsWith(RootPrefix, StringComparison.Ordinal)) return false;

            // A prefab loaded in memory also reports no scene. Never touch an asset.
            if (EditorUtility.IsPersistent(go)) return false;

            return !go.scene.IsValid() || (go.hideFlags & HideFlags.DontSaveInEditor) != 0;
        }
    }
}
