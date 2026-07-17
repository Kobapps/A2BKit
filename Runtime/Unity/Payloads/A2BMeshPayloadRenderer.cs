using System;
using System.Collections.Generic;
using A2BKit.Core;
using UnityEngine;
using UnityEngine.Rendering;

namespace A2BKit.Unity
{
    /// <summary>
    /// A pooled MeshFilter + MeshRenderer per item, built either from a <see cref="Mesh"/> plus
    /// <see cref="Material"/> or by instantiating a <see cref="Prefab"/>.
    ///
    /// Tinting goes through a single reused <see cref="MaterialPropertyBlock"/>. Writing
    /// <c>renderer.material.color</c> instead would instantiate a material per item on first touch —
    /// a leak that scales with item count and only shows up as an ever-growing memory graph, never
    /// as a visible bug.
    ///
    /// The colour property name is resolved once against the real material rather than assumed:
    /// URP/HDRP lit shaders use <c>_BaseColor</c> and the built-in pipeline uses <c>_Color</c>, and a
    /// MaterialPropertyBlock set with the wrong name fails silently — items simply never tint.
    /// </summary>
    [Serializable]
    public sealed class A2BMeshPayloadRenderer : A2BPooledPayloadRenderer
    {
        /// <summary>Mesh drawn when <see cref="Prefab"/> is null.</summary>
        public Mesh Mesh;

        /// <summary>Material drawn when <see cref="Prefab"/> is null.</summary>
        public Material Material;

        /// <summary>Optional prefab. When set it wins, and Mesh/Material are ignored.</summary>
        public GameObject Prefab;

        /// <summary>Shader colour property. Empty auto-detects _BaseColor then _Color.</summary>
        public string ColorProperty = "";

        /// <summary>Off by default: a cosmetic coin casting shadows is a cost nobody asked for.</summary>
        public ShadowCastingMode ShadowCasting = ShadowCastingMode.Off;

        /// <summary>Off by default, for the same reason.</summary>
        public bool ReceiveShadows;

        private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
        private static readonly int LegacyColorId = Shader.PropertyToID("_Color");

        /// <summary>Component refs resolved once at pool-create time; UpdateItem never calls GetComponent (AD-3).</summary>
        private sealed class Binding
        {
            public MeshRenderer Renderer;
            public Color LastColor;
        }

        private Dictionary<Transform, Binding> _bindings;
        private MaterialPropertyBlock _block;
        private int _colorId;
        private bool _canTint;
        private bool _colorResolved;

        public override string PayloadKey => "Mesh";

        protected override void OnInitialized()
        {
            _bindings = new Dictionary<Transform, Binding>(Mathf.Max(8, DefaultCapacity), A2BTransformComparer.Instance);
            _block = new MaterialPropertyBlock();
            _colorResolved = false;
            _canTint = false;

            if (Prefab == null && (Mesh == null || Material == null))
                A2BLog.Error(null, "A2BMeshPayloadRenderer needs either a Prefab or both a Mesh and a Material; items will spawn invisible.");
        }

        protected override Transform CreateInstance()
        {
            GameObject instance;
            MeshRenderer renderer;

            if (Prefab != null)
            {
                instance = UnityEngine.Object.Instantiate(Prefab);

                // GetComponentInChildren is a create-time cost, not a tick-time one — the ref it
                // finds is what UpdateItem reads for the rest of the instance's life (AD-3).
                renderer = instance.GetComponentInChildren<MeshRenderer>(true);
                if (renderer == null)
                    A2BLog.Error(Prefab, "A2BMeshPayloadRenderer prefab has no MeshRenderer; items will spawn invisible.");
            }
            else
            {
                instance = new GameObject("A2B Mesh Item", typeof(MeshFilter), typeof(MeshRenderer));
                instance.GetComponent<MeshFilter>().sharedMesh = Mesh;
                renderer = instance.GetComponent<MeshRenderer>();
                renderer.sharedMaterial = Material;
            }

            if (renderer != null)
            {
                renderer.shadowCastingMode = ShadowCasting;
                renderer.receiveShadows = ReceiveShadows;
                ResolveColorProperty(renderer.sharedMaterial);
            }

            Transform item = instance.transform;
            _bindings[item] = new Binding { Renderer = renderer, LastColor = Color.white };
            return item;
        }

        protected override void OnAcquired(Transform item, in A2BItemSpawnInfo info)
        {
            if (!_canTint || _bindings == null || !_bindings.TryGetValue(item, out Binding binding)) return;
            if (binding.Renderer == null) return;

            // Clears the faded tint the item carried into the pool.
            binding.LastColor = Color.white;
            _block.SetColor(_colorId, Color.white);
            binding.Renderer.SetPropertyBlock(_block);
        }

        public override void UpdateItem(Transform item, in A2BVisualState state)
        {
            if (!_canTint || _bindings == null || !_bindings.TryGetValue(item, out Binding binding)) return;
            if (binding.Renderer == null) return;
            if (binding.LastColor == state.Color) return;

            binding.LastColor = state.Color;

            // The block is reused and carries one key, so SetColor overwrites in place and
            // SetPropertyBlock copies it out — no allocation on either side.
            _block.SetColor(_colorId, state.Color);
            binding.Renderer.SetPropertyBlock(_block);
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

        /// <summary>
        /// Picks the colour property once, from the material actually in use. An explicit
        /// ColorProperty that the shader does not have degrades to auto-detection rather than to a
        /// silently dead tint (AD-8).
        /// </summary>
        private void ResolveColorProperty(Material material)
        {
            if (_colorResolved) return;
            _colorResolved = true;

            if (material == null)
            {
                A2BLog.Error(null, "A2BMeshPayloadRenderer found no material to tint through; items will draw untinted.");
                return;
            }

            if (!string.IsNullOrEmpty(ColorProperty))
            {
                int requested = Shader.PropertyToID(ColorProperty);
                if (material.HasProperty(requested))
                {
                    _colorId = requested;
                    _canTint = true;
                    return;
                }

                A2BLog.Warn(material, "A2BMeshPayloadRenderer ColorProperty is not on this shader; falling back to auto-detection.");
            }

            if (material.HasProperty(BaseColorId))
            {
                _colorId = BaseColorId;
                _canTint = true;
                return;
            }

            if (material.HasProperty(LegacyColorId))
            {
                _colorId = LegacyColorId;
                _canTint = true;
                return;
            }

            A2BLog.Warn(material, "A2BMeshPayloadRenderer found no _BaseColor or _Color on this shader; items will draw untinted.");
        }
    }
}
