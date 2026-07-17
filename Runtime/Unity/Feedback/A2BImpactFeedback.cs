using System.Collections.Generic;
using A2BKit.Core;
using UnityEngine;
using UnityEngine.Pool;

namespace A2BKit.Unity
{
    /// <summary>
    /// The "effect on hit": a pooled spark, puff or ring left behind at the point an item LANDED.
    ///
    /// Fires from <see cref="Arrived"/> only, which the presenter raises solely for
    /// <see cref="A2BReleaseReason.Arrived"/>. That gate is the whole reason
    /// <see cref="A2BReleaseReason"/> exists: without it, cancelling a 50-coin burst — a scene
    /// unload, a lost endpoint — would detonate 50 impacts at once, at the position each coin
    /// happened to have reached. A cancelled item must vanish quietly.
    ///
    /// The impact is parented under <see cref="A2BFeedbackBase.Root"/>, never under the item, and
    /// this is not a style choice. The item is released to the payload pool microseconds later; a
    /// child of it would be dragged into the pool root, deactivated with it, and then reactivated
    /// under the *next* coin. Under Root, the impact outlives its item — which is the point of an
    /// impact — and Root is also the working space the arrival position is expressed in, so
    /// <c>finalState.Position</c> can be assigned as a local position with no conversion (AD-4: this
    /// type is not permitted to convert anything).
    ///
    /// **The lifetime problem, and why this type is pumped (AD-9 vs AD-6).** An impact must retire
    /// itself, and nothing in this package self-updates (AD-6). The tempting fix — age impacts from
    /// <see cref="A2BFeedbackBase.Updated"/>, which already runs every frame — is wrong in exactly
    /// the case that matters: the *last* coin's impact is spawned as its item is released, after
    /// which no item is live, no hook ever runs again, and that impact hangs on screen forever. That
    /// is a leak (AD-9), and it is the leak users would report, because the final impact is the one
    /// they are looking at. So this registers with <see cref="A2BFeedbackPump"/> — a single shared
    /// player-loop callback for the whole process, not a per-object <c>Update</c>. See the pump's
    /// remarks for the full AD-6 argument.
    /// </summary>
    [System.Serializable]
    public sealed class A2BImpactFeedback : A2BFeedbackBase, IA2BFeedbackPumped
    {
        /// <summary>Optional impact prefab. When set it wins and Sprite/Color/Size are ignored.</summary>
        public GameObject Prefab;

        /// <summary>Sprite drawn when <see cref="Prefab"/> is null.</summary>
        public Sprite Sprite;

        /// <summary>Tint for the sprite fallback.</summary>
        public Color Color = Color.white;

        /// <summary>Uniform scale the sprite fallback peaks at, in working-space units.</summary>
        public float Size = 1f;

        /// <summary>Seconds an impact lives before it returns to the pool. Must be &gt; 0.</summary>
        public float Lifetime = 0.25f;

        /// <summary>Scale over normalized lifetime. The overshoot-and-vanish shape lives here.</summary>
        public AnimationCurve ScaleCurve = new AnimationCurve(
            new Keyframe(0f, 0f), new Keyframe(0.35f, 1.15f), new Keyframe(1f, 0f));

        /// <summary>Random Z rotation per impact, so twenty coins landing do not stamp one shape twenty times.</summary>
        public bool RotateRandomly = true;

        /// <summary>
        /// Age impacts on unscaled time. On by default because an impact is UI-adjacent juice and a
        /// pause menu freezing a half-grown spark mid-air looks broken — but exposed, because an
        /// impact on a hit-stop effect wants to freeze with the game.
        /// </summary>
        public bool UseUnscaledTime = true;

        /// <summary>Impacts kept alive when released. Past this, released impacts are destroyed.</summary>
        public int MaxPoolSize = 128;

        /// <summary>Initial pool capacity.</summary>
        public int DefaultCapacity = 8;

        /// <summary>
        /// One live impact. A struct in a reused <see cref="List{T}"/> indexed with a plain for: the
        /// pump walks this every frame, and a class per impact would allocate per arrival (AD-3).
        /// </summary>
        private struct LiveImpact
        {
            public Transform Root;
            public Vector3 BaseScale;
            public float Elapsed;
        }

        // Runtime state, built in OnInitialized and never in a field initializer — CreateRuntimeInstance
        // MemberwiseClones, so an initializer would have two clones sharing one pool and one live
        // list (AD-14).
        private ObjectPool<Transform> _pool;
        private Transform _poolRoot;
        private List<Transform> _all;
        private Dictionary<Transform, Vector3> _baseScales;
        private List<LiveImpact> _live;
        private AnimationCurve _curve;
        private A2BRandom _random;
        private bool _canSpawn;
        private bool _pumped;
        private bool _disposed;

        public override string FeedbackKey => "Impact";

        protected override void OnInitialized()
        {
            _disposed = false;
            _all = new List<Transform>(Mathf.Max(8, DefaultCapacity));
            _baseScales = new Dictionary<Transform, Vector3>(Mathf.Max(8, DefaultCapacity), A2BTransformComparer.Instance);
            _live = new List<LiveImpact>(Mathf.Max(8, DefaultCapacity));

            // UnityEngine.Random is banned package-wide: it is global mutable state and would make
            // NFR-4 unreachable. A fixed seed here means a recorded session replays identically.
            _random = new A2BRandom(0x1AC7u);

            // Resolved once, with a fallback, because a null or empty curve would evaluate to 0 and
            // every impact would be invisible — a silent failure, which AD-8 asks us not to ship.
            _curve = ScaleCurve != null && ScaleCurve.length > 0
                ? ScaleCurve
                : new AnimationCurve(new Keyframe(0f, 0f), new Keyframe(0.35f, 1.15f), new Keyframe(1f, 0f));

            _canSpawn = Prefab != null || Sprite != null;
            if (!_canSpawn)
            {
                A2BLog.Error(null, "A2BImpactFeedback has neither a Prefab nor a Sprite; no impact will spawn.");
                return;
            }

            if (Root == null)
            {
                A2BLog.Error(null, "A2BImpactFeedback initialized with a null root; no impact will spawn.");
                _canSpawn = false;
                return;
            }

            var poolRootObject = new GameObject("A2B Impact Pool");
            poolRootObject.SetActive(false);
            _poolRoot = poolRootObject.transform;
            _poolRoot.SetParent(Root, false);

            int capacity = Mathf.Max(1, DefaultCapacity);

            _pool = new ObjectPool<Transform>(
                createFunc: CreateImpact,
                actionOnGet: null,
                actionOnRelease: null,
                actionOnDestroy: DestroyImpact,
                // The check costs a HashSet probe per Get/Release; a double-release is an authoring
                // defect rather than a runtime fault, so it earns its keep in the editor only.
#if UNITY_EDITOR
                collectionCheck: true,
#else
                collectionCheck: false,
#endif
                defaultCapacity: capacity,
                maxSize: Mathf.Max(capacity, MaxPoolSize));

            A2BFeedbackPump.Register(this);
            _pumped = true;
        }

        protected override void Arrived(Transform item, in A2BVisualState finalState)
        {
            if (!_canSpawn || _pool == null || _disposed) return;
            if (Lifetime <= 0f) return;

            Transform impact = _pool.Get();
            if (impact == null) return;

            if (!_baseScales.TryGetValue(impact, out Vector3 baseScale)) baseScale = Vector3.one;

            // Our own object under our own root. The item's Transform is read, never written (AD-15).
            impact.SetParent(Root, false);
            impact.localPosition = finalState.Position;
            impact.localRotation = RotateRandomly
                ? Quaternion.Euler(0f, 0f, _random.NextFloat(0f, 360f))
                : Quaternion.identity;
            impact.localScale = baseScale * Mathf.Max(0f, _curve.Evaluate(0f));
            impact.gameObject.SetActive(true);

            _live.Add(new LiveImpact { Root = impact, BaseScale = baseScale, Elapsed = 0f });
        }

        /// <summary>
        /// One frame of every live impact. Walks a concrete List with an index and mutates structs in
        /// place, so the whole path allocates nothing regardless of impact count (AD-3).
        ///
        /// Reads <c>Time</c> directly, which AD-12 forbids *in the tick path* — and this is not it.
        /// AD-12 protects the simulation, whose deltas must come from each effect's
        /// <c>IA2BTimeSource</c> so a paused-menu effect and a gameplay effect can disagree. An
        /// impact has already outlived its effect and has no time source to ask; inventing a port to
        /// carry one to a spark would be ceremony, not correctness. <see cref="UseUnscaledTime"/> is
        /// the honest knob instead.
        /// </summary>
        void IA2BFeedbackPumped.PumpTick()
        {
            if (_live == null || _live.Count == 0 || _pool == null || _disposed) return;

            float delta = UseUnscaledTime ? UnityEngine.Time.unscaledDeltaTime : UnityEngine.Time.deltaTime;
            float lifetime = Mathf.Max(0.0001f, Lifetime);

            // Backwards, so retiring an impact by swapping the tail into its slot cannot skip anyone.
            for (int i = _live.Count - 1; i >= 0; i--)
            {
                LiveImpact live = _live[i];
                live.Elapsed += delta;

                if (live.Elapsed >= lifetime || live.Root == null)
                {
                    RemoveAt(i);
                    Retire(live.Root);
                    continue;
                }

                float t = live.Elapsed / lifetime;
                live.Root.localScale = live.BaseScale * Mathf.Max(0f, _curve.Evaluate(t));
                _live[i] = live;
            }
        }

        protected override void Disposed()
        {
            // Tolerates a second call: teardown and domain reload can both land here (NFR-5).
            if (_disposed) return;
            _disposed = true;

            if (_pumped)
            {
                A2BFeedbackPump.Unregister(this);
                _pumped = false;
            }

            // Impacts still in flight are handed out, so the pool does not know about them; the _all
            // list does, which is why it exists.
            _live?.Clear();

            if (_pool != null)
            {
                _pool.Clear();
                _pool = null;
            }

            if (_all != null)
            {
                for (int i = _all.Count - 1; i >= 0; i--)
                {
                    Transform impact = _all[i];
                    if (impact != null) A2BFeedbackKit.Destroy(impact.gameObject);
                }
                _all.Clear();
            }

            _baseScales?.Clear();

            if (_poolRoot != null)
            {
                A2BFeedbackKit.Destroy(_poolRoot.gameObject);
                _poolRoot = null;
            }
        }

        private void RemoveAt(int index)
        {
            // Swap-back: List.RemoveAt shuffles the tail, and order among impacts means nothing.
            int last = _live.Count - 1;
            _live[index] = _live[last];
            _live.RemoveAt(last);
        }

        private void Retire(Transform impact)
        {
            if (impact == null || _pool == null) return;

            impact.gameObject.SetActive(false);
            impact.SetParent(_poolRoot, false);
            _pool.Release(impact);
        }

        private Transform CreateImpact()
        {
            GameObject instance;

            if (Prefab != null)
            {
                instance = Object.Instantiate(Prefab);
                instance.name = "A2B Impact";
            }
            else
            {
                instance = new GameObject("A2B Impact");
                var renderer = instance.AddComponent<SpriteRenderer>();
                renderer.sprite = Sprite;
                renderer.color = Color;
            }

            Transform root = instance.transform;

            // A prefab authored at 0.5 scale means it; Size only sizes the sprite fallback, which has
            // no authored scale of its own to respect.
            Vector3 baseScale = Prefab != null ? root.localScale : Vector3.one * Mathf.Max(0f, Size);

            root.gameObject.SetActive(false);
            root.SetParent(_poolRoot, false);

            _all.Add(root);
            _baseScales[root] = baseScale;
            return root;
        }

        private void DestroyImpact(Transform impact)
        {
            if (impact == null) return;

            _all?.Remove(impact);
            _baseScales?.Remove(impact);
            A2BFeedbackKit.Destroy(impact.gameObject);
        }
    }
}
