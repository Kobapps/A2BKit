using System.Collections.Generic;
using A2BKit.Core;
using UnityEngine;

namespace A2BKit.Unity
{
    /// <summary>
    /// Core's IA2BPresenter, implemented by composing a space adapter, a payload renderer and any
    /// number of feedbacks (AD-18).
    ///
    /// This is where AD-15's ownership split is actually enforced: the renderer supplies and styles
    /// the visual, the adapter places it, feedbacks decorate around it, and the call order is fixed
    /// here — not left to whoever implements a payload next. Core cannot enforce it, because Core
    /// cannot see any of these types.
    ///
    /// Item ids are indices into a reused free-list, so translating an id to a Transform costs an
    /// array read and never allocates (AD-3).
    /// </summary>
    public sealed class A2BPresenter : IA2BPresenter
    {
        private static readonly IA2BFeedback[] NoFeedbacks = new IA2BFeedback[0];

        private readonly IA2BSpaceAdapter _adapter;
        private readonly IA2BPayloadRenderer _renderer;

        // A concrete array, indexed with a plain for: a foreach over an interface-typed collection
        // would box its enumerator every frame (AD-3).
        private readonly IA2BFeedback[] _feedbacks;

        private Transform[] _items = new Transform[32];
        private readonly Stack<int> _freeIds = new Stack<int>(32);
        private int _nextId;
        private int _activeCount;

        public A2BPresenter(IA2BSpaceAdapter adapter, IA2BPayloadRenderer renderer, int prewarmCount = 0)
            : this(adapter, renderer, null, prewarmCount) { }

        public A2BPresenter(
            IA2BSpaceAdapter adapter,
            IA2BPayloadRenderer renderer,
            IList<IA2BFeedback> feedbacks,
            int prewarmCount = 0)
        {
            _adapter = adapter;

            // Clone, never use the authored instance directly: [SerializeReference] gives an asset one
            // payload instance, so two presenters over two canvases would share a pool and a root —
            // and the second one's items would silently fly on the first one's canvas. The prototype
            // stays pristine; this presenter owns its clone (AD-14 pool identity).
            _renderer = renderer != null ? renderer.CreateRuntimeInstance() : null;
            _renderer?.Initialize(_adapter?.Root, prewarmCount);

            _feedbacks = BuildFeedbacks(feedbacks, _adapter?.Root);
        }

        private static IA2BFeedback[] BuildFeedbacks(IList<IA2BFeedback> authored, Transform root)
        {
            if (authored == null || authored.Count == 0) return NoFeedbacks;

            var live = new List<IA2BFeedback>(authored.Count);
            for (int i = 0; i < authored.Count; i++)
            {
                IA2BFeedback prototype = authored[i];
                if (prototype == null) continue;   // an empty [SerializeReference] slot is not an error

                // Same prototype rule as the payload: an asset's feedback instance is shared, so each
                // presenter needs its own or two effects fight over one trail pool.
                IA2BFeedback instance = prototype.CreateRuntimeInstance();
                instance.Initialize(root);
                live.Add(instance);
            }
            return live.Count == 0 ? NoFeedbacks : live.ToArray();
        }

        public IA2BSpaceAdapter Adapter => _adapter;
        public IA2BPayloadRenderer Renderer => _renderer;
        public int ActiveItemCount => _activeCount;

        /// <summary>Live feedback instances (clones, not the authored prototypes).</summary>
        public IReadOnlyList<IA2BFeedback> Feedbacks => _feedbacks;

        public int Acquire(in A2BItemSpawnInfo info)
        {
            Transform item = _renderer.Acquire(in info);
            if (item == null) return -1;

            int id;
            if (_freeIds.Count > 0)
            {
                id = _freeIds.Pop();
            }
            else
            {
                id = _nextId++;
                if (id >= _items.Length)
                {
                    // Grows once to the concurrency high-water mark, then never again.
                    var grown = new Transform[_items.Length * 2];
                    System.Array.Copy(_items, grown, _items.Length);
                    _items = grown;
                }
            }

            _items[id] = item;
            _activeCount++;

            for (int i = 0; i < _feedbacks.Length; i++)
                _feedbacks[i].OnItemSpawned(item, in info);

            return id;
        }

        public void Apply(int itemId, in A2BVisualState state)
        {
            if (itemId < 0 || itemId >= _items.Length) return;
            Transform item = _items[itemId];
            if (item == null) return;

            // Order is fixed and load-bearing (AD-15): drawables first, transform second, feedbacks
            // last — so a trail reads the item's final position for this frame, not the previous one.
            _renderer.UpdateItem(item, in state);
            _adapter.ApplyToTransform(item, in state);

            for (int i = 0; i < _feedbacks.Length; i++)
                _feedbacks[i].OnItemUpdated(item, in state);
        }

        public void Release(int itemId, A2BReleaseReason reason)
        {
            if (itemId < 0 || itemId >= _items.Length) return;
            Transform item = _items[itemId];
            if (item == null) return;

            // The "effect on hit" moment. Only a real arrival sparks — a cancelled 50-coin burst
            // must not emit 50 impacts as it tears down.
            if (reason == A2BReleaseReason.Arrived)
            {
                var landed = new A2BVisualState(item.localPosition, item.localRotation, item.localScale, Color.white, 1f);
                for (int i = 0; i < _feedbacks.Length; i++)
                    _feedbacks[i].OnItemArrived(item, in landed);
            }

            // Always: whatever a feedback attached must come off before the item is reused.
            for (int i = 0; i < _feedbacks.Length; i++)
                _feedbacks[i].OnItemReleased(item, reason);

            _renderer.Release(item);
            _items[itemId] = null;
            _freeIds.Push(itemId);
            _activeCount--;
        }

        public Vector3 ScaleScatter(in Vector3 unitOffset, float radius) => _adapter.ScaleScatter(in unitOffset, radius);

        public Vector3 ToWorkingSpace(in A2BEndpointSample sample) => _adapter.ToWorkingSpace(in sample);

        public void GetPoolStats(out int active, out int available) => _renderer.GetPoolStats(out active, out available);

        public void Dispose()
        {
            for (int i = 0; i < _feedbacks.Length; i++) _feedbacks[i].Dispose();

            _renderer.Dispose();
            _freeIds.Clear();
            _activeCount = 0;
            _nextId = 0;
            for (int i = 0; i < _items.Length; i++) _items[i] = null;
        }
    }
}
