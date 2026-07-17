using System.Collections.Generic;
using A2BKit.Core;
using UnityEngine;
using UnityEngine.Pool;

namespace A2BKit.Unity
{
    /// <summary>
    /// A pooled <see cref="TrailRenderer"/> attached as a CHILD of each item — the streak behind a
    /// coin, the comet tail behind a soul.
    ///
    /// A child, not a component added to the item, for two reasons that are both AD-15. First, the
    /// item belongs to the payload renderer's pool: adding a component to it would mutate an object
    /// this feedback does not own, and the component would still be there for the next payload that
    /// pulls that item — a text item would silently sprout a trail. Second, a TrailRenderer's own
    /// transform must be free to detach, and detaching the item is exactly what AD-15 forbids. A
    /// child we created is our own business, and reparenting it is not a Transform write on the item.
    ///
    /// **Two Clear() calls, both load-bearing, and neither optional.** A pooled TrailRenderer
    /// remembers its vertices across a Get/Release round-trip. The release-side Clear stops the next
    /// coin inheriting the last coin's tail; the spawn-side Clear stops a tail being drawn from the
    /// pool root to the spawn point. Skip either and the symptom is the same famous one: a streak
    /// across the screen from wherever the previous item died.
    ///
    /// **The first frame is armed, not emitted.** An item is handed to <see cref="Spawned"/> still
    /// carrying the local position of whatever it did last — the adapter does not place it until the
    /// first <c>Apply</c>. Emitting from spawn would therefore lay down a segment from the stale
    /// position to the real one, which is that same screen-crossing streak by a subtler route. So the
    /// trail spawns silent and starts emitting from the first <see cref="Updated"/>, which the
    /// presenter runs *after* the adapter has placed the item for the frame.
    /// </summary>
    [System.Serializable]
    public sealed class A2BTrailFeedback : A2BFeedbackBase
    {
        /// <summary>Seconds a point on the trail persists. Effectively the tail's length in time.</summary>
        public float Time = 0.25f;

        /// <summary>Width at the item end.</summary>
        public float StartWidth = 0.15f;

        /// <summary>Width at the tail end. Taper to zero for the classic comet.</summary>
        public float EndWidth = 0f;

        /// <summary>Colour along the trail. Null falls back to the TrailRenderer's own default.</summary>
        public Gradient Color;

        /// <summary>Optional material. Null resolves an unlit fallback rather than drawing magenta (AD-8).</summary>
        public Material Material;

        /// <summary>Distance the item must travel before a new point is laid down. Higher is cheaper.</summary>
        public float MinVertexDistance = 0.05f;

        /// <summary>Trails kept alive when released. Past this, released trails are destroyed.</summary>
        public int MaxPoolSize = 256;

        /// <summary>Initial pool capacity.</summary>
        public int DefaultCapacity = 16;

        /// <summary>
        /// Resolved once per trail at pool-create time. <see cref="Updated"/> runs per item per frame
        /// and must never call GetComponent (AD-3).
        /// </summary>
        private sealed class Binding
        {
            public Transform Root;
            public TrailRenderer Trail;

            /// <summary>False until the adapter has placed the item once. See the class remarks.</summary>
            public bool Emitting;
        }

        // All runtime state. Built in OnInitialized, never in a field initializer: CreateRuntimeInstance
        // MemberwiseClones, so a field initializer would leave two clones sharing one pool (AD-14).
        private ObjectPool<Transform> _pool;
        private Transform _poolRoot;
        private List<Binding> _all;
        private Dictionary<Transform, Binding> _byTrail;
        private Dictionary<Transform, Binding> _byItem;
        private Material _ownedMaterial;
        private bool _disposed;

        public override string FeedbackKey => "Trail";

        protected override void OnInitialized()
        {
            _disposed = false;
            _all = new List<Binding>(Mathf.Max(8, DefaultCapacity));
            _byTrail = new Dictionary<Transform, Binding>(Mathf.Max(8, DefaultCapacity), A2BTransformComparer.Instance);
            _byItem = new Dictionary<Transform, Binding>(Mathf.Max(8, DefaultCapacity), A2BTransformComparer.Instance);

            if (Material == null)
                _ownedMaterial = A2BFeedbackKit.CreateDefaultUnlitMaterial(null, "A2BTrailFeedback");

            if (Root == null)
            {
                A2BLog.Error(null, "A2BTrailFeedback initialized with a null root; it will draw no trails.");
                return;
            }

            // Inactive holding pen under the adapter Root, so tearing the effect's hierarchy down
            // takes the pool with it instead of leaving stray trails in the scene.
            var poolRootObject = new GameObject("A2B Trail Pool");
            poolRootObject.SetActive(false);
            _poolRoot = poolRootObject.transform;
            _poolRoot.SetParent(Root, false);

            int capacity = Mathf.Max(1, DefaultCapacity);

            _pool = new ObjectPool<Transform>(
                createFunc: CreateTrail,
                actionOnGet: null,
                actionOnRelease: null,
                actionOnDestroy: DestroyTrail,
                // Double-release is an authoring defect, not a runtime fault, and the check costs a
                // HashSet probe per Get/Release — so it earns its keep in the editor and nowhere else.
#if UNITY_EDITOR
                collectionCheck: true,
#else
                collectionCheck: false,
#endif
                defaultCapacity: capacity,
                maxSize: Mathf.Max(capacity, MaxPoolSize));
        }

        protected override void Spawned(Transform item, in A2BItemSpawnInfo info)
        {
            if (_pool == null || _disposed) return;

            // Defensive: a double-spawn would orphan the first trail, which is a leak (AD-9).
            if (_byItem.ContainsKey(item)) return;

            Transform trail = _pool.Get();
            if (trail == null) return;

            if (!_byTrail.TryGetValue(trail, out Binding binding)) return;

            // Our own object's transform — not the item's. AD-15 governs the item, not what we made.
            trail.SetParent(item, false);
            trail.localPosition = Vector3.zero;
            trail.localRotation = Quaternion.identity;
            trail.localScale = Vector3.one;

            if (binding.Trail != null)
            {
                binding.Trail.emitting = false;
                binding.Trail.Clear();   // Clear #1: drop anything the pool round-trip left behind.
            }

            binding.Emitting = false;
            trail.gameObject.SetActive(true);
            _byItem[item] = binding;
        }

        /// <summary>
        /// Per item, per frame (AD-3): a dictionary probe and a bool test on the steady-state path.
        /// The arming branch runs exactly once per item, on the first frame the item has a real
        /// position — see the class remarks for why emitting cannot simply start at spawn.
        /// </summary>
        protected override void Updated(Transform item, in A2BVisualState state)
        {
            if (_byItem == null) return;
            if (!_byItem.TryGetValue(item, out Binding binding)) return;
            if (binding.Emitting) return;

            binding.Emitting = true;
            if (binding.Trail == null) return;

            binding.Trail.Clear();   // The item just teleported from its stale pool pose to frame 1.
            binding.Trail.emitting = true;
        }

        protected override void Released(Transform item, A2BReleaseReason reason)
        {
            if (_byItem == null || _pool == null || _disposed) return;
            if (!_byItem.TryGetValue(item, out Binding binding)) return;

            _byItem.Remove(item);

            // Detach FIRST. The presenter releases the item to its pool moments after this returns,
            // which reparents and reposes it; a trail still attached would be dragged along for that
            // move and draw the streak this whole class exists to prevent.
            if (binding.Root != null)
            {
                binding.Root.gameObject.SetActive(false);
                binding.Root.SetParent(_poolRoot, false);
                binding.Root.localPosition = Vector3.zero;
            }

            if (binding.Trail != null)
            {
                binding.Trail.emitting = false;
                binding.Trail.Clear();   // Clear #2: the one the next coin depends on.
            }

            binding.Emitting = false;
            _pool.Release(binding.Root);
        }

        protected override void Disposed()
        {
            // Tolerates a second call: teardown and domain reload can both land here (NFR-5).
            if (_disposed) return;
            _disposed = true;

            // Destroys the pooled half through DestroyTrail.
            if (_pool != null)
            {
                _pool.Clear();
                _pool = null;
            }

            // The handed-out half is unknown to ObjectPool — items in flight when the scene died
            // would otherwise survive as orphans.
            if (_all != null)
            {
                for (int i = _all.Count - 1; i >= 0; i--)
                {
                    Binding binding = _all[i];
                    if (binding?.Root != null) A2BFeedbackKit.Destroy(binding.Root.gameObject);
                }
                _all.Clear();
            }

            _byItem?.Clear();
            _byTrail?.Clear();

            if (_poolRoot != null)
            {
                A2BFeedbackKit.Destroy(_poolRoot.gameObject);
                _poolRoot = null;
            }

            if (_ownedMaterial != null)
            {
                A2BFeedbackKit.Destroy(_ownedMaterial);
                _ownedMaterial = null;
            }
        }

        private Transform CreateTrail()
        {
            var instance = new GameObject("A2B Trail");
            var trail = instance.AddComponent<TrailRenderer>();

            trail.time = Mathf.Max(0.001f, Time);
            trail.startWidth = Mathf.Max(0f, StartWidth);
            trail.endWidth = Mathf.Max(0f, EndWidth);
            trail.minVertexDistance = Mathf.Max(0f, MinVertexDistance);

            // autodestruct would Destroy the GameObject when the trail empties — destroying an object
            // this pool still believes it owns, which is a use-after-free, not a convenience.
            trail.autodestruct = false;
            trail.emitting = false;

            if (Color != null) trail.colorGradient = Color;

            Material material = Material != null ? Material : _ownedMaterial;
            if (material != null) trail.sharedMaterial = material;

            Transform root = instance.transform;
            root.gameObject.SetActive(false);
            root.SetParent(_poolRoot, false);

            var binding = new Binding { Root = root, Trail = trail, Emitting = false };
            _all.Add(binding);
            _byTrail[root] = binding;
            return root;
        }

        private void DestroyTrail(Transform trail)
        {
            if (trail == null) return;

            if (_byTrail.TryGetValue(trail, out Binding binding))
            {
                _all.Remove(binding);
                _byTrail.Remove(trail);
            }

            A2BFeedbackKit.Destroy(trail.gameObject);
        }
    }
}
