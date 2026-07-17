using System;
using System.Collections.Generic;
using A2BKit.Core;
using UnityEngine;
using UnityEngine.UI;

namespace A2BKit.Unity
{
    /// <summary>
    /// A pooled uGUI <see cref="Image"/> per item — the Canvas payload.
    ///
    /// Anchors, pivot and size are written once at pool-create time. They are geometry, not
    /// placement: AD-15 reserves position/rotation/scale/parent for the adapter, and this type never
    /// touches those. Centring the anchors is what makes the adapter's <c>localPosition</c> write
    /// mean "offset from the root's centre" rather than "offset from an arbitrary corner".
    ///
    /// Items land on an A2BKit-owned canvas by default (AD-14) — nothing here assumes otherwise, but
    /// that is the reason 200 of these do not trigger a rebuild storm on the host HUD.
    /// </summary>
    [Serializable]
    public sealed class A2BImagePayloadRenderer : A2BPooledPayloadRenderer
    {
        /// <summary>The sprite every item of this payload draws.</summary>
        public Sprite Sprite;

        /// <summary>Optional material override. Null uses uGUI's default UI material.</summary>
        public Material Material;

        /// <summary>Item size in canvas units, written once at create time.</summary>
        public Vector2 Size = new Vector2(64f, 64f);

        /// <summary>Letterbox the sprite inside <see cref="Size"/> instead of stretching it.</summary>
        public bool PreserveAspect = true;

        /// <summary>Off by default: a cosmetic item must never eat a click meant for the HUD beneath it.</summary>
        public bool RaycastTarget;

        /// <summary>Component refs resolved once at pool-create time; UpdateItem never calls GetComponent (AD-3).</summary>
        private sealed class Binding
        {
            public Image Image;
            public Color LastColor;
        }

        private Dictionary<Transform, Binding> _bindings;

        public override string PayloadKey => "Image";

        protected override void OnInitialized()
        {
            _bindings = new Dictionary<Transform, Binding>(Mathf.Max(8, DefaultCapacity), A2BTransformComparer.Instance);

            if (Sprite == null)
                A2BLog.Error(null, "A2BImagePayloadRenderer has no Sprite assigned; items will spawn invisible.");
        }

        protected override Transform CreateInstance()
        {
            // Naming the components on the constructor is what gets a RectTransform instead of the
            // plain Transform a bare `new GameObject()` always builds.
            var instance = new GameObject("A2B Image Item", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            var rect = (RectTransform)instance.transform;

            // Geometry, not placement (AD-15). A centred anchor and pivot make the adapter's
            // localPosition write land the item's centre exactly on the path point.
            rect.anchorMin = new Vector2(0.5f, 0.5f);
            rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.sizeDelta = Size;

            var image = instance.GetComponent<Image>();
            image.sprite = Sprite;
            if (Material != null) image.material = Material;
            image.preserveAspect = PreserveAspect;
            image.raycastTarget = RaycastTarget;

            _bindings[rect] = new Binding { Image = image, LastColor = image.color };
            return rect;
        }

        protected override void OnAcquired(Transform item, in A2BItemSpawnInfo info)
        {
            if (_bindings == null || !_bindings.TryGetValue(item, out Binding binding)) return;

            // See A2BSpritePayloadRenderer: a faded-out item must not come back faded out.
            binding.Image.color = Color.white;
            binding.LastColor = Color.white;
        }

        public override void UpdateItem(Transform item, in A2BVisualState state)
        {
            if (_bindings == null || !_bindings.TryGetValue(item, out Binding binding)) return;
            if (binding.Image == null) return;
            if (binding.LastColor == state.Color) return;

            // Guarded because a Graphic colour write schedules a vertex rebuild. Unity guards this
            // too, but the guard is on the far side of a managed-to-native property call per item.
            binding.LastColor = state.Color;
            binding.Image.color = state.Color;
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
