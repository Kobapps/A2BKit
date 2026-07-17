using System;
using System.Collections.Generic;
using A2BKit.Core;
using UnityEngine;
using UnityEngine.Pool;

namespace A2BKit.Unity
{
    /// <summary>
    /// The pooling substrate every payload renderer shares.
    ///
    /// This class exists so that AD-14's "parenting happens at exactly two call sites and nowhere
    /// else" is a structural fact rather than a rule each payload author is trusted to remember.
    /// Those two call sites are <see cref="OnGetInstance"/> (parent to the adapter Root) and
    /// <see cref="OnReleaseInstance"/> (parent back to this renderer's own pool root). A subclass
    /// never sees them: it is handed a Transform and may touch drawable properties only (AD-15).
    ///
    /// Subclasses cache their components at pool-create time and look them up through a
    /// Dictionary keyed on the item Transform, because <see cref="UpdateItem"/> runs every frame
    /// for every item and <c>GetComponent</c> there would be a per-item interop call (AD-3).
    ///
    /// Serializable with a parameterless constructor and public config fields, so the same type is
    /// authorable via [SerializeReference] and constructible in code. Everything runtime-owned is
    /// created in <see cref="Initialize"/>, never in a field initializer — deserialization is not
    /// obliged to run those.
    /// </summary>
    [Serializable]
    public abstract class A2BPooledPayloadRenderer : IA2BPayloadRenderer
    {
        /// <summary>Instances kept alive when released. Past this, released items are destroyed.</summary>
        public int MaxPoolSize = 256;

        /// <summary>Initial capacity hint. The prewarm count from Initialize wins when it is larger.</summary>
        public int DefaultCapacity = 16;

        private ObjectPool<Transform> _pool;
        private Transform _root;
        private Transform _poolRoot;

        /// <summary>Every instance this renderer created, active or pooled. Dispose needs both halves.</summary>
        private List<Transform> _live;

        private Transform[] _prewarmScratch;
        private bool _initialized;
        private bool _disposed;

        /// <summary>AD-8 asks for one actionable error, not one per item per frame.</summary>
        private bool _loggedNotInitialized;

        /// <summary>Distinguishes pools. Combined with definition and space to form pool identity (AD-14).</summary>
        public abstract string PayloadKey { get; }

        /// <summary>The adapter Root items live under while active. Subclasses must not write to it.</summary>
        protected Transform Root => _root;

        /// <summary>
        /// Clones the configuration and drops every scrap of runtime state, so the clone builds its
        /// own pool over its own root (see IA2BPayloadRenderer.CreateRuntimeInstance for why).
        ///
        /// MemberwiseClone also copies subclass fields by reference, which would be a sharing bug —
        /// except that every subclass builds its runtime state in OnInitialized(), and resetting
        /// _initialized here forces that to re-run and overwrite the copied references. A subclass
        /// that instead built state in a field initializer would silently share it; that is why
        /// OnInitialized() exists and why the base comment says state goes there, never in a field
        /// initializer.
        /// </summary>
        public IA2BPayloadRenderer CreateRuntimeInstance()
        {
            var clone = (A2BPooledPayloadRenderer)MemberwiseClone();
            clone._pool = null;
            clone._root = null;
            clone._poolRoot = null;
            clone._live = null;
            clone._prewarmScratch = null;
            clone._initialized = false;
            clone._disposed = false;
            clone._loggedNotInitialized = false;
            return clone;
        }

        public void Initialize(Transform root, int prewarmCount)
        {
            // Idempotent as a backstop. The real protection is that presenters clone via
            // CreateRuntimeInstance, so a prototype is never initialized twice in the first place.
            if (_initialized) return;

            if (root == null)
            {
                A2BLog.Error(null, "Payload renderer initialized with a null root; it will spawn nothing.");
                return;
            }

            _root = root;
            _disposed = false;
            _loggedNotInitialized = false;
            _live = new List<Transform>(Mathf.Max(8, DefaultCapacity));

            // Inactive holding pen. Parented under the adapter Root so teardown of the effect's
            // hierarchy takes the pool with it rather than leaking a stray scene object.
            var poolRootObject = new GameObject(PayloadKey + " Pool");
            poolRootObject.SetActive(false);
            _poolRoot = poolRootObject.transform;
            _poolRoot.SetParent(root, false);

            int capacity = Mathf.Max(1, prewarmCount > 0 ? prewarmCount : DefaultCapacity);
            int maxSize = Mathf.Max(capacity, MaxPoolSize);

            _pool = new ObjectPool<Transform>(
                createFunc: CreatePooledInstance,
                actionOnGet: OnGetInstance,
                actionOnRelease: OnReleaseInstance,
                actionOnDestroy: OnDestroyPooledInstance,
                // Double-release and foreign-release are authoring defects, not runtime faults. The
                // check costs a HashSet probe per Get/Release, so it earns its keep in the editor
                // and nowhere else.
#if UNITY_EDITOR
                collectionCheck: true,
#else
                collectionCheck: false,
#endif
                defaultCapacity: capacity,
                maxSize: maxSize);

            _initialized = true;

            // Config resolution before the first instance exists — subclasses fold shader ids and
            // fallbacks here so create and update stay branch-light.
            OnInitialized();

            Prewarm(prewarmCount);
        }

        public Transform Acquire(in A2BItemSpawnInfo info)
        {
            if (!_initialized || _pool == null)
            {
                if (!_loggedNotInitialized)
                {
                    _loggedNotInitialized = true;
                    A2BLog.Error(null, "Payload renderer acquired before a successful Initialize; it will spawn no items.");
                }
                return null;
            }

            Transform item = _pool.Get();
            if (item == null) return null;

            OnAcquired(item, in info);
            return item;
        }

        public void Release(Transform item)
        {
            if (item == null || _pool == null || _disposed) return;

            OnReleasing(item);
            _pool.Release(item);
        }

        public abstract void UpdateItem(Transform item, in A2BVisualState state);

        public void GetPoolStats(out int active, out int available)
        {
            if (_pool == null)
            {
                active = 0;
                available = 0;
                return;
            }

            active = _pool.CountActive;
            available = _pool.CountInactive;
        }

        public void Dispose()
        {
            // Tolerates a second call: teardown and domain reload can both land here (NFR-5).
            if (_disposed) return;
            _disposed = true;

            // Destroys the pooled half through OnDestroyPooledInstance, which unregisters bindings.
            if (_pool != null)
            {
                _pool.Clear();
                _pool = null;
            }

            // The active half is unknown to ObjectPool — items handed out and never released (a
            // scene torn down mid-flight) would otherwise survive as orphans.
            if (_live != null)
            {
                for (int i = _live.Count - 1; i >= 0; i--)
                {
                    Transform item = _live[i];
                    if (item != null) DestroyInstance(item);
                }
                _live.Clear();
            }

            if (_poolRoot != null)
            {
                DestroyObject(_poolRoot.gameObject);
                _poolRoot = null;
            }

            _root = null;
            _prewarmScratch = null;
            _initialized = false;

            OnDisposed();
        }

        /// <summary>Builds one instance, parented nowhere and styled from config. Called off the tick.</summary>
        protected abstract Transform CreateInstance();

        /// <summary>Config resolution once the pool exists and before the first instance is built.</summary>
        protected virtual void OnInitialized() { }

        /// <summary>An instance is about to be destroyed — drop its cached component binding.</summary>
        protected virtual void OnInstanceDestroyed(Transform item) { }

        /// <summary>Per-item drawable setup at spawn: text content, reset tint, play a system.</summary>
        protected virtual void OnAcquired(Transform item, in A2BItemSpawnInfo info) { }

        /// <summary>Per-item drawable teardown before the item goes back to the pool.</summary>
        protected virtual void OnReleasing(Transform item) { }

        /// <summary>Final cleanup after every instance is gone.</summary>
        protected virtual void OnDisposed() { }

        private void Prewarm(int count)
        {
            if (count <= 0 || _pool == null) return;

            // Get-then-release is the only way to seed an ObjectPool. It runs once at Initialize, so
            // the scratch array it needs is a startup cost, not a per-frame one (AD-3).
            if (_prewarmScratch == null || _prewarmScratch.Length < count)
                _prewarmScratch = new Transform[count];

            for (int i = 0; i < count; i++)
                _prewarmScratch[i] = _pool.Get();

            for (int i = 0; i < count; i++)
            {
                Transform item = _prewarmScratch[i];
                _prewarmScratch[i] = null;
                if (item != null) _pool.Release(item);
            }
        }

        private Transform CreatePooledInstance()
        {
            Transform item = CreateInstance();
            if (item == null) return null;

            item.gameObject.SetActive(false);
            item.SetParent(_poolRoot, false);
            _live.Add(item);
            return item;
        }

        private void OnGetInstance(Transform item)
        {
            // ObjectPool invokes this even when createFunc returned null.
            if (item == null) return;

            // AD-14 call site 1 of 2. worldPositionStays:false leaves the local values alone — the
            // adapter writes them a moment later and is the only type allowed to (AD-15).
            item.SetParent(_root, false);
            item.gameObject.SetActive(true);
        }

        private void OnReleaseInstance(Transform item)
        {
            if (item == null) return;

            // AD-14 call site 2 of 2.
            item.gameObject.SetActive(false);
            item.SetParent(_poolRoot, false);
        }

        private void OnDestroyPooledInstance(Transform item)
        {
            if (item == null) return;

            _live?.Remove(item);
            DestroyInstance(item);
        }

        private void DestroyInstance(Transform item)
        {
            OnInstanceDestroyed(item);
            DestroyObject(item.gameObject);
        }

        private static void DestroyObject(GameObject target)
        {
            if (target == null) return;

            // Edit-mode preview (FR-21) tears down outside play mode, where Destroy defers to a frame
            // that never comes and the instance survives the reload.
            if (Application.isPlaying) UnityEngine.Object.Destroy(target);
            else UnityEngine.Object.DestroyImmediate(target);
        }
    }

    /// <summary>
    /// Identity comparer for Transform dictionary keys.
    ///
    /// The default comparer routes through UnityEngine.Object.Equals, which is a native liveness
    /// probe — correct, but paid once per item per frame on the binding lookup. Pool bindings are
    /// keyed by the exact instance we created, so reference identity is both cheaper and stricter.
    /// </summary>
    internal sealed class A2BTransformComparer : IEqualityComparer<Transform>
    {
        internal static readonly A2BTransformComparer Instance = new A2BTransformComparer();

        public bool Equals(Transform a, Transform b) => ReferenceEquals(a, b);

        public int GetHashCode(Transform item)
            => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(item);
    }
}
