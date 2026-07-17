using System;
using System.Collections.Generic;
using A2BKit.Core;
using UnityEngine;

namespace A2BKit.Unity
{
    /// <summary>
    /// A pooled <see cref="ParticleSystem"/> per item, played on spawn and stopped on release.
    ///
    /// The item itself is still a CPU-side Transform the scheduler drives and the adapter places —
    /// the particle system rides along as decoration. That distinction is the whole point: a payload
    /// that handed motion to the VFX system would put the item's position on the GPU, where the
    /// scheduler cannot see it, and <c>ItemArrived</c> (AD-13, and the package's reason to exist)
    /// would have nothing to fire from.
    ///
    /// <c>playOnAwake</c> is forced off on every instance. Left on, pooling breaks in a way that
    /// looks like a physics bug: the system replays on every SetActive(true) *and* on the prewarm
    /// pass, so items emit inside the pool root before they are ever spawned.
    /// </summary>
    [Serializable]
    public sealed class A2BParticlePayloadRenderer : A2BPooledPayloadRenderer
    {
        /// <summary>The system to pool. Null degrades to an empty default system rather than throwing (AD-8).</summary>
        public ParticleSystem ParticlePrefab;

        /// <summary>Drive the system's start colour from the item's visual state.</summary>
        public bool TintStartColor = true;

        /// <summary>Component refs resolved once at pool-create time; UpdateItem never calls GetComponent (AD-3).</summary>
        private sealed class Binding
        {
            public ParticleSystem System;

            /// <summary>MainModule is a struct wrapper over a native pointer — caching it skips a property fetch per write.</summary>
            public ParticleSystem.MainModule Main;

            public Color LastColor;
        }

        private Dictionary<Transform, Binding> _bindings;

        public override string PayloadKey => "Particle";

        protected override void OnInitialized()
        {
            _bindings = new Dictionary<Transform, Binding>(Mathf.Max(8, DefaultCapacity), A2BTransformComparer.Instance);

            if (ParticlePrefab == null)
                A2BLog.Error(null, "A2BParticlePayloadRenderer has no ParticlePrefab assigned; items will spawn an empty default system.");
        }

        protected override Transform CreateInstance()
        {
            ParticleSystem system;

            if (ParticlePrefab != null)
            {
                system = UnityEngine.Object.Instantiate(ParticlePrefab);
            }
            else
            {
                var instance = new GameObject("A2B Particle Item", typeof(ParticleSystem));
                system = instance.GetComponent<ParticleSystem>();
            }

            var main = system.main;

            // Non-negotiable for pooling; see the type comment.
            main.playOnAwake = false;

            // simulationSpace is deliberately left as authored. Local keeps a burst welded to the
            // item, World leaves a trail behind it — both are legitimate looks, and a payload that
            // overrode the choice would silently break every trail prefab a user brings.

            Transform item = system.transform;
            _bindings[item] = new Binding { System = system, Main = main, LastColor = Color.white };
            return item;
        }

        protected override void OnAcquired(Transform item, in A2BItemSpawnInfo info)
        {
            if (_bindings == null || !_bindings.TryGetValue(item, out Binding binding)) return;
            if (binding.System == null) return;

            if (TintStartColor)
            {
                binding.Main.startColor = Color.white;
                binding.LastColor = Color.white;
            }

            // Clear before Play so the item does not inherit particles from its previous life.
            binding.System.Clear(true);
            binding.System.Play(true);
        }

        public override void UpdateItem(Transform item, in A2BVisualState state)
        {
            if (!TintStartColor) return;
            if (_bindings == null || !_bindings.TryGetValue(item, out Binding binding)) return;
            if (binding.System == null) return;
            if (binding.LastColor == state.Color) return;

            binding.LastColor = state.Color;

            // Color converts implicitly to MinMaxGradient, which is a struct — no allocation.
            binding.Main.startColor = state.Color;
        }

        protected override void OnReleasing(Transform item)
        {
            if (_bindings == null || !_bindings.TryGetValue(item, out Binding binding)) return;
            if (binding.System == null) return;

            // StopEmittingAndClear, not StopEmitting: a deactivated system freezes its particles
            // rather than finishing them, so anything still alive reappears mid-flight on the next
            // spawn, at the previous item's coordinates.
            binding.System.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
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
