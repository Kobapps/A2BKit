using System.Collections.Generic;
using A2BKit.Core;
using UnityEngine;

namespace A2BKit.Unity
{
    /// <summary>
    /// The designer-facing authoring surface (FR-1): an effect configured in the inspector with no code.
    ///
    /// Deliberately a thin wrapper around A2BEffectDefinition rather than a re-declaration of it —
    /// Core owns the model, this owns the Unity asset identity. It holds no scene references, so one
    /// asset drives many scenes and many concurrent effects (FR-1, FR-3).
    /// </summary>
    [CreateAssetMenu(fileName = "A2BEffect", menuName = "A2BKit/Effect", order = 0)]
    public sealed class A2BEffectAsset : ScriptableObject
    {
        [Tooltip("Which coordinate space this effect plays in. Canvas covers all three render modes.")]
        public A2BSpaceKind Space = A2BSpaceKind.Canvas;

        [SerializeReference, A2BSubclassSelector, Tooltip("What each item looks like.")]
        public IA2BPayloadRenderer Payload;

        [SerializeReference, A2BSubclassSelector,
         Tooltip("Optional embellishments: trails, impact flashes, sounds. Order does not matter.")]
        public List<IA2BFeedback> Feedbacks = new List<IA2BFeedback>();

        [SerializeReference, A2BSubclassSelector,
         Tooltip("Optional: build the space adapter yourself. Leave empty to use the built-in adapter " +
                 "for the chosen Space. This is the seam for supporting a space A2BKit does not ship.")]
        public IA2BSpaceAdapterFactory SpaceOverride;

        [Tooltip("Motion, emission and appearance.")]
        public A2BEffectDefinition Definition = new A2BEffectDefinition();

        private A2BEffectSpec _spec;

        /// <summary>
        /// This asset as a playable spec — the shape both authoring surfaces share (FR-1/FR-2).
        ///
        /// Cached and rebuilt only when the asset changes, so it is safe to call per play. Sharing one
        /// spec instance also means every play of this asset shares its pools, since the spec is the
        /// pool-cache owner (AD-14) — a fresh spec per play would quietly build a fresh pool per play.
        /// </summary>
        public A2BEffectSpec ToSpec()
        {
            if (_spec != null) return _spec;

            _spec = new A2BEffectSpec
            {
                Space = Space,
                Payload = Payload,
                Definition = Definition,
                Feedbacks = Feedbacks,
                SpaceOverride = SpaceOverride,
                Owner = this,
            };
            return _spec;
        }

        /// <summary>
        /// Validates everything that can be checked without a scene, for the inspector to surface
        /// inline rather than as a runtime exception (FR-19).
        /// </summary>
        public bool Validate(out string error)
        {
            if (Definition == null) { error = "Definition is missing."; return false; }
            if (!Definition.Validate(out error)) return false;
            if (Payload == null) { error = "No payload is assigned."; return false; }
            error = null;
            return true;
        }

        private void OnValidate()
        {
            // Edited in the inspector: drop the cached spec so the next play picks up the change
            // instead of replaying the values this asset had when it was first played.
            _spec = null;

            // Fresh assets must be playable with no further configuration (FR-1).
            Definition ??= new A2BEffectDefinition();
            Definition.Path ??= new A2BBezierPath();
            Definition.Easing ??= new A2BStandardEasing(A2BEaseKind.InOutCubic);
            Definition.Emission ??= new A2BBurstEmission();
        }
    }
}
