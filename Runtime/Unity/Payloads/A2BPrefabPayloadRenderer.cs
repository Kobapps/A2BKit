using System;
using System.Collections.Generic;
using A2BKit.Core;
using UnityEngine;
using UnityEngine.UI;

namespace A2BKit.Unity
{
    /// <summary>
    /// Pools instances of any prefab — the escape hatch payload (FR-8).
    ///
    /// The other five renderers each know what they are drawing. This one does not, so it resolves a
    /// tint target once per instance and remembers which kind it found: a uGUI <see cref="Graphic"/>,
    /// a <see cref="SpriteRenderer"/>, or any other <see cref="Renderer"/> tinted through a
    /// MaterialPropertyBlock. Resolving the kind at create time rather than branching on null every
    /// frame is what keeps the generic path as cheap as the specific ones (AD-3).
    ///
    /// The search order is not arbitrary. SpriteRenderer *is* a Renderer, so a generic Renderer probe
    /// first would find it and route a sprite through a property block — where the sprite shader's
    /// tint lives under a different name and the item would silently never tint.
    ///
    /// Being the escape hatch does not relax AD-15: a prefab's own animation may do as it likes, but
    /// this renderer writes drawable properties only. The adapter still owns the item's Transform.
    /// </summary>
    [Serializable]
    public sealed class A2BPrefabPayloadRenderer : A2BPooledPayloadRenderer
    {
        /// <summary>The prefab to pool. Null is fatal to this payload and logged once (AD-8).</summary>
        public GameObject Prefab;

        /// <summary>Drive the resolved tint target from the item's visual state. Off leaves the prefab as authored.</summary>
        public bool ApplyColor = true;

        /// <summary>Shader colour property for the MaterialPropertyBlock path. Empty auto-detects _BaseColor then _Color.</summary>
        public string ColorProperty = "";

        private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
        private static readonly int LegacyColorId = Shader.PropertyToID("_Color");

        /// <summary>Which drawable the tint lands on, decided once per instance.</summary>
        private enum TintTarget
        {
            None,
            Graphic,
            Sprite,
            PropertyBlock
        }

        /// <summary>Component refs resolved once at pool-create time; UpdateItem never calls GetComponent (AD-3).</summary>
        private sealed class Binding
        {
            public TintTarget Target;
            public Graphic Graphic;
            public SpriteRenderer Sprite;
            public Renderer Renderer;
            public Color LastColor;
        }

        private Dictionary<Transform, Binding> _bindings;
        private MaterialPropertyBlock _block;
        private int _colorId;
        private bool _canTintBlock;
        private bool _colorResolved;

        public override string PayloadKey => "Prefab";

        protected override void OnInitialized()
        {
            _bindings = new Dictionary<Transform, Binding>(Mathf.Max(8, DefaultCapacity), A2BTransformComparer.Instance);
            _block = new MaterialPropertyBlock();
            _colorResolved = false;
            _canTintBlock = false;

            if (Prefab == null)
                A2BLog.Error(null, "A2BPrefabPayloadRenderer has no Prefab assigned; it will spawn nothing.");
        }

        protected override Transform CreateInstance()
        {
            if (Prefab == null) return null;

            var instance = UnityEngine.Object.Instantiate(Prefab);
            Transform item = instance.transform;
            var binding = new Binding { LastColor = Color.white, Target = TintTarget.None };

            if (ApplyColor) ResolveTintTarget(instance, binding);

            _bindings[item] = binding;
            return item;
        }

        protected override void OnAcquired(Transform item, in A2BItemSpawnInfo info)
        {
            if (_bindings == null || !_bindings.TryGetValue(item, out Binding binding)) return;

            // A faded-out item must not come back faded out.
            binding.LastColor = Color.white;
            WriteColor(binding, Color.white);
        }

        public override void UpdateItem(Transform item, in A2BVisualState state)
        {
            if (!ApplyColor) return;
            if (_bindings == null || !_bindings.TryGetValue(item, out Binding binding)) return;
            if (binding.Target == TintTarget.None) return;
            if (binding.LastColor == state.Color) return;

            binding.LastColor = state.Color;
            WriteColor(binding, state.Color);
        }

        protected override void OnInstanceDestroyed(Transform item)
        {
            _bindings?.Remove(item);
        }

        protected override void OnDisposed()
        {
            _bindings?.Clear();
            _block = null;
        }

        private void WriteColor(Binding binding, Color color)
        {
            switch (binding.Target)
            {
                case TintTarget.Graphic:
                    if (binding.Graphic != null) binding.Graphic.color = color;
                    break;

                case TintTarget.Sprite:
                    if (binding.Sprite != null) binding.Sprite.color = color;
                    break;

                case TintTarget.PropertyBlock:
                    if (binding.Renderer != null)
                    {
                        _block.SetColor(_colorId, color);
                        binding.Renderer.SetPropertyBlock(_block);
                    }
                    break;
            }
        }

        /// <summary>
        /// Finds this instance's tint target. Every probe here is a create-time cost the tick never
        /// repeats. Not finding one is not an error — a prefab may legitimately be untintable.
        /// </summary>
        private void ResolveTintTarget(GameObject instance, Binding binding)
        {
            Graphic graphic = instance.GetComponentInChildren<Graphic>(true);
            if (graphic != null)
            {
                binding.Graphic = graphic;
                binding.Target = TintTarget.Graphic;
                return;
            }

            SpriteRenderer sprite = instance.GetComponentInChildren<SpriteRenderer>(true);
            if (sprite != null)
            {
                binding.Sprite = sprite;
                binding.Target = TintTarget.Sprite;
                return;
            }

            Renderer renderer = instance.GetComponentInChildren<Renderer>(true);
            if (renderer == null) return;

            ResolveColorProperty(renderer.sharedMaterial);
            if (!_canTintBlock) return;

            binding.Renderer = renderer;
            binding.Target = TintTarget.PropertyBlock;
        }

        /// <summary>
        /// Picks the colour property once, from the material actually in use — URP/HDRP lit shaders
        /// use _BaseColor and built-in uses _Color, and a property block set with the wrong name
        /// fails silently.
        /// </summary>
        private void ResolveColorProperty(Material material)
        {
            if (_colorResolved) return;
            _colorResolved = true;

            if (material == null) return;

            if (!string.IsNullOrEmpty(ColorProperty))
            {
                int requested = Shader.PropertyToID(ColorProperty);
                if (material.HasProperty(requested))
                {
                    _colorId = requested;
                    _canTintBlock = true;
                    return;
                }

                A2BLog.Warn(material, "A2BPrefabPayloadRenderer ColorProperty is not on this shader; falling back to auto-detection.");
            }

            if (material.HasProperty(BaseColorId))
            {
                _colorId = BaseColorId;
                _canTintBlock = true;
                return;
            }

            if (material.HasProperty(LegacyColorId))
            {
                _colorId = LegacyColorId;
                _canTintBlock = true;
                return;
            }

            A2BLog.Warn(material, "A2BPrefabPayloadRenderer found no _BaseColor or _Color on this prefab's shader; items will draw untinted.");
        }
    }
}
