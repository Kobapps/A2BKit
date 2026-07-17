using System.Collections.Generic;
using A2BKit.Core;
using UnityEngine;
using UnityEngine.LowLevel;
using UnityEngine.PlayerLoop;
using Object = UnityEngine.Object;

namespace A2BKit.Unity
{
    /// <summary>
    /// The scraps several feedbacks would otherwise each re-invent: a material resolver that cannot
    /// NRE, and a play/edit-mode-correct Destroy.
    ///
    /// Deliberately hands back a *new* material rather than caching a shared static one. A static
    /// cache would be cheaper, but it would also outlive every <c>Dispose()</c> — and NFR-5 asks that
    /// disposing a presenter leave nothing behind. Owning the material makes destruction the owner's
    /// obligation, which is a rule a reader can check.
    /// </summary>
    internal static class A2BFeedbackKit
    {
        /// <summary>
        /// A default unlit material for a project whose author did not assign one (AD-8: a missing
        /// material is a degraded visual, never an exception).
        ///
        /// The fallback chain exists because <see cref="Shader.Find"/> answers for the *installed*
        /// pipeline, not the intended one: "Universal Render Pipeline/Unlit" is absent in a built-in
        /// project, and — the trap that actually bites — URP shaders are stripped from a player build
        /// unless something references them, so a URP editor session that works can ship as magenta.
        /// "Sprites/Default" is always present and is the honest last resort. Every link may return
        /// null, so every link is checked; a null shader here degrades to no material rather than
        /// throwing inside <c>new Material(null)</c>.
        /// </summary>
        /// <returns>A material the caller now owns and must destroy, or null if no shader resolved.</returns>
        internal static Material CreateDefaultUnlitMaterial(Object context, string forWhat)
        {
            Shader shader = Shader.Find("Universal Render Pipeline/Unlit");
            if (shader == null) shader = Shader.Find("Sprites/Default");
            if (shader == null) shader = Shader.Find("Unlit/Color");

            if (shader == null)
            {
                // One actionable message naming what went without, not one per instance (AD-8).
                A2BLog.Error(context, forWhat + " found no fallback shader; it will draw with no material. " +
                                      "Assign a Material, or add an Unlit shader to Always Included Shaders.");
                return null;
            }

            // DontSave: this material is created at runtime and destroyed by its owner. Without the
            // flag an edit-mode preview (FR-21) leaks it into the scene file.
            return new Material(shader) { hideFlags = HideFlags.DontSave, name = "A2B Default Unlit" };
        }

        /// <summary>
        /// Destroy that also works outside play mode. Edit-mode preview (FR-21) tears down where
        /// <c>Destroy</c> defers to a frame that never arrives and the object survives the reload.
        /// </summary>
        internal static void Destroy(Object target)
        {
            if (target == null) return;

            if (Application.isPlaying) Object.Destroy(target);
            else Object.DestroyImmediate(target);
        }
    }

    /// <summary>
    /// A feedback that owns something with a lifetime of its own — an impact that must retire, a
    /// one-shot AudioSource that must come back — and therefore needs a heartbeat that does not
    /// depend on an item being alive.
    /// </summary>
    internal interface IA2BFeedbackPumped
    {
        /// <summary>
        /// One frame. Deliberately carries no delta, for AD-6's reason: the pump cannot know whether
        /// a given feedback wants scaled or unscaled time, so each implementer reads the one it was
        /// configured for.
        /// </summary>
        void PumpTick();
    }

    /// <summary>
    /// One player-loop callback, shared by every feedback in the process, that retires spawned
    /// decorations whose owning effect has already finished.
    ///
    /// **Why this exists, and the AD-6 tension it resolves.** AD-6 says nothing self-updates, and the
    /// feedback port hands out no heartbeat: <see cref="IA2BFeedback.OnItemUpdated"/> runs only while
    /// items are live. But the interesting case is exactly the one where nothing is live — the last
    /// coin lands, the presenter releases it, the effect completes, and the impact spark it just
    /// spawned is left mid-animation with no caller ever coming back for it. Ticking from the
    /// feedback's own item hooks retires 199 of 200 impacts and leaks the last one until some
    /// unrelated effect happens to play, which is a leak (AD-9) dressed as a working feature. The
    /// same is true of the AudioSource playing the final "ting".
    ///
    /// So a heartbeat is unavoidable, and the only real choice is its shape. AD-6's rule bans
    /// <c>Update</c>/<c>LateUpdate</c>/coroutines on items, effects, payloads and providers, and its
    /// stated purpose is to prevent N per-object interop callbacks and nondeterministic ordering.
    /// This is one static callback for the whole process regardless of how many impacts are in
    /// flight — the same shape as <see cref="A2BRunner"/>, honouring AD-6's purpose. A hidden
    /// MonoBehaviour would have been equivalent in every respect except that it would violate AD-6's
    /// literal text; the player loop is the same idea without the lie.
    ///
    /// It runs in <see cref="PostLateUpdate"/> — after A2BRunner's LateUpdate, so an impact spawned
    /// by an arrival this frame is ticked from the next frame rather than being aged before it is
    /// drawn once.
    ///
    /// **Known limit, stated rather than hidden:** the player loop does not run in edit mode, so an
    /// edit-mode preview's impacts do not animate or retire on their own. They are still destroyed by
    /// <c>Dispose()</c>, so the leak is bounded by the preview's own teardown.
    /// </summary>
    internal static class A2BFeedbackPump
    {
        // A concrete List indexed with a plain for: a foreach over an interface-typed collection
        // boxes its enumerator, which would allocate every frame (AD-3).
        private static readonly List<IA2BFeedbackPumped> Pumped = new List<IA2BFeedbackPumped>(4);

        private static bool _installed;

        internal static void Register(IA2BFeedbackPumped pumped)
        {
            if (pumped is null || Pumped.Contains(pumped)) return;

            Install();
            Pumped.Add(pumped);
        }

        internal static void Unregister(IA2BFeedbackPumped pumped)
        {
            if (pumped is null) return;
            Pumped.Remove(pumped);
        }

        private static void Tick()
        {
            // Backwards, so a feedback that unregisters itself mid-tick (Dispose from a callback)
            // cannot make the loop skip its neighbour.
            for (int i = Pumped.Count - 1; i >= 0; i--)
            {
                if (i >= Pumped.Count) continue;

                IA2BFeedbackPumped pumped = Pumped[i];
                if (pumped is null) continue;

                try { pumped.PumpTick(); }
                catch (System.Exception e) { A2BLog.Exception(null, e); }   // AD-8: never escapes into the loop
            }
        }

        private static void Install()
        {
            if (_installed) return;
            _installed = true;

            PlayerLoopSystem loop = PlayerLoop.GetCurrentPlayerLoop();
            var pump = new PlayerLoopSystem
            {
                type = typeof(A2BFeedbackPump),
                updateDelegate = Tick
            };

            if (!TryInsertInto(ref loop, typeof(PostLateUpdate), pump))
            {
                // The player loop is user-replaceable; if PostLateUpdate is gone, appending at the
                // root still ticks once a frame, which is all this needs (AD-8: degrade, don't throw).
                Append(ref loop, pump);
            }

            PlayerLoop.SetPlayerLoop(loop);
        }

        private static bool TryInsertInto(ref PlayerLoopSystem loop, System.Type stage, in PlayerLoopSystem pump)
        {
            if (loop.subSystemList == null) return false;

            for (int i = 0; i < loop.subSystemList.Length; i++)
            {
                if (loop.subSystemList[i].type != stage) continue;

                Append(ref loop.subSystemList[i], pump);
                return true;
            }
            return false;
        }

        private static void Append(ref PlayerLoopSystem parent, in PlayerLoopSystem pump)
        {
            PlayerLoopSystem[] existing = parent.subSystemList;
            int count = existing?.Length ?? 0;

            var grown = new PlayerLoopSystem[count + 1];
            if (count > 0) System.Array.Copy(existing, grown, count);
            grown[count] = pump;

            parent.subSystemList = grown;
        }

        /// <summary>
        /// Unity rebuilds the player loop on every play-mode entry, so an install from a previous
        /// session is gone while <c>_installed</c> — a static — survives "Enter Play Mode without
        /// domain reload" and would report a callback that no longer exists (NFR-5). Registrations
        /// from the old session are stale by the same token and must not be ticked.
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            Pumped.Clear();
            _installed = false;
        }
    }
}
