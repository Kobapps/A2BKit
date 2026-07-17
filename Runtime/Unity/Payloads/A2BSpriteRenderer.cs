using System;
using System.Collections.Generic;
using A2BKit.Core;
using UnityEngine;

namespace A2BKit.Unity
{
    /// <summary>
    /// A pooled <see cref="SpriteRenderer"/> per item — the World2D and World3D workhorse.
    ///
    /// Depth is expressed through sorting layer and order, never through Z (AD-19). That is why the
    /// sorting fields live on the payload rather than the adapter: the adapter pins Z and has no
    /// opinion about draw order, so the two halves of AD-19 would otherwise have no owner.
    ///
    /// Colour is the only property written per frame, and only when it actually changed — a
    /// SpriteRenderer colour write dirties the batch, so an unconditional assignment would cost
    /// every item a rebuild it did not need.
    /// </summary>
    [Serializable]
    public sealed class A2BSpritePayloadRenderer : A2BPooledPayloadRenderer
    {
        /// <summary>The sprite every item of this payload draws.</summary>
        public Sprite Sprite;

        /// <summary>Optional material override. Null uses Unity's default sprite material.</summary>
        public Material Material;

        /// <summary>Sorting layer name. Unknown names resolve to Default rather than erroring (AD-8).</summary>
        public string SortingLayerName = "Default";

        /// <summary>Draw order within the sorting layer. This, not Z, is how World2D layers (AD-19).</summary>
        public int SortingOrder;

        /// <summary>Component refs resolved once at pool-create time; UpdateItem never calls GetComponent (AD-3).</summary>
        private sealed class Binding
        {
            public SpriteRenderer Renderer;
            public Color LastColor;
        }

        private Dictionary<Transform, Binding> _bindings;
        private int _sortingLayerId;

        public override string PayloadKey => "Sprite";

        protected override void OnInitialized()
        {
            _bindings = new Dictionary<Transform, Binding>(Mathf.Max(8, DefaultCapacity), A2BTransformComparer.Instance);

            // Resolved once: assigning sortingLayerName per instance re-does this string lookup, and
            // an unknown name would log per item instead of once here.
            string layer = string.IsNullOrEmpty(SortingLayerName) ? "Default" : SortingLayerName;
            _sortingLayerId = SortingLayer.NameToID(layer);

            if (Sprite == null)
                A2BLog.Error(null, "A2BSpritePayloadRenderer has no Sprite assigned; items will spawn invisible.");
        }

        protected override Transform CreateInstance()
        {
            var instance = new GameObject("A2B Sprite Item");
            var renderer = instance.AddComponent<SpriteRenderer>();

            renderer.sprite = Sprite;
            if (Material != null) renderer.sharedMaterial = Material;
            renderer.sortingLayerID = _sortingLayerId;
            renderer.sortingOrder = SortingOrder;

            Transform item = instance.transform;
            _bindings[item] = new Binding { Renderer = renderer, LastColor = renderer.color };
            return item;
        }

        protected override void OnAcquired(Transform item, in A2BItemSpawnInfo info)
        {
            if (_bindings == null || !_bindings.TryGetValue(item, out Binding binding)) return;

            // An item released mid-fade carries its faded colour back into the pool. Without this
            // reset the next spawn is invisible for the frame before the first Apply lands.
            binding.Renderer.color = Color.white;
            binding.LastColor = Color.white;
        }

        public override void UpdateItem(Transform item, in A2BVisualState state)
        {
            if (_bindings == null || !_bindings.TryGetValue(item, out Binding binding)) return;
            if (binding.Renderer == null) return;
            if (binding.LastColor == state.Color) return;

            binding.LastColor = state.Color;
            binding.Renderer.color = state.Color;
        }

        protected override void OnInstanceDestroyed(Transform item)
        {
            _bindings?.Remove(item);
        }

        protected override void OnDisposed()
        {
            _bindings?.Clear();
        }
    }
}
