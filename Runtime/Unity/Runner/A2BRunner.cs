using A2BKit.Core;
using UnityEngine;

namespace A2BKit.Unity
{
    /// <summary>
    /// The one MonoBehaviour in the package that ticks (AD-6). Everything else is driven from here:
    /// no item, effect, payload or provider has an Update, a coroutine or a timer.
    ///
    /// Self-bootstraps on first use, so a caller never has to remember to drop a manager into the
    /// scene — the five-minute-first-effect requirement (SM-3) dies on setup ceremony.
    /// </summary>
    [DefaultExecutionOrder(10000)] // After gameplay: endpoints should be at their final position for the frame.
    [AddComponentMenu("")]         // Bootstrapped, never added by hand.
    public sealed class A2BRunner : MonoBehaviour
    {
        private static A2BRunner _instance;
        private static bool _quitting;

        private readonly A2BScheduler _scheduler = new A2BScheduler();

        /// <summary>The scheduler every effect runs on. Tests construct their own instead (NFR-4).</summary>
        public static A2BScheduler Scheduler => Instance._scheduler;

        public static A2BRunner Instance
        {
            get
            {
                if (_instance != null) return _instance;
                if (_quitting) return null;

                var go = new GameObject("[A2BKit Runner]");
                _instance = go.AddComponent<A2BRunner>();

                // DontDestroyOnLoad THROWS outside play mode, so an unguarded call makes any
                // edit-time use — an editor tool, a preview, a batch-mode capture — explode on the
                // first Play(): a plain AD-8 violation. Outside play mode there is no scene load to
                // survive anyway, so the guard costs nothing.
                // DontSave is for the edit-mode case only. In play mode DontDestroyOnLoad already
                // provides the persistence, and DontSave on top of it means the runner is not destroyed
                // when the editor tears the play-mode scenes down either — it survives into edit mode
                // outside any scene, invisible in the Hierarchy and impossible to remove (NFR-5).
                if (Application.isPlaying) DontDestroyOnLoad(go);
                else go.hideFlags = HideFlags.DontSave;

                return _instance;
            }
        }

        /// <summary>True when a runner exists. Lets diagnostics avoid bootstrapping one just to look.</summary>
        public static bool Exists => _instance != null;

        private void Awake()
        {
            if (_instance != null && _instance != this)
            {
                Destroy(gameObject);
                return;
            }
            _instance = this;
        }

        private void LateUpdate()
        {
            // LateUpdate, not Update: gameplay has already moved the wallet HUD and the camera this
            // frame, so endpoints resolve to where they actually ended up rather than lagging by one
            // frame (FR-12).
            _scheduler.Tick();
        }

        private void OnDestroy()
        {
            if (_instance == this)
            {
                _scheduler.CancelAll();
                _instance = null;
            }
        }

        private void OnApplicationQuit() => _quitting = true;

        /// <summary>
        /// Resets the static bootstrap across domain reloads and "Enter Play Mode without reload"
        /// (NFR-5). Without this, a stale runner reference survives and the scheduler silently
        /// stops ticking.
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            _instance = null;
            _quitting = false;
        }
    }
}
