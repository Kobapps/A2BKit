using UnityEngine;

namespace A2BKit.Core
{
    /// <summary>
    /// Code-first authoring (FR-2), a peer of the A2BEffectAsset surface rather than a wrapper on it.
    ///
    /// Allocates at build time and never per play: build once, cache the definition, play it forever.
    /// Chaining into Play() every frame would allocate a definition per call — the builder is setup
    /// code, not tick-path code.
    /// </summary>
    public sealed class A2BEffectBuilder
    {
        private readonly A2BEffectDefinition _def;

        /// <summary>
        /// True once the emission instance belongs to this builder alone. Create() owns its emission
        /// outright; From() inherits the SOURCE's instance by reference and must copy before writing.
        /// </summary>
        private bool _ownsEmission;

        private A2BEffectBuilder(A2BEffectDefinition def, bool ownsEmission)
        {
            _def = def;
            _ownsEmission = ownsEmission;
        }

        /// <summary>Starts from package defaults.</summary>
        public static A2BEffectBuilder Create() => new A2BEffectBuilder(new A2BEffectDefinition(), ownsEmission: true);

        /// <summary>
        /// Starts from an existing definition. Overriding an asset in code never mutates the source
        /// asset (FR-2) — the definition is cloned, and any strategy this builder edits is copied on
        /// first write. Note the clone is shallow over strategies, so the guarantee comes from the
        /// copy-on-write, not from the clone.
        /// </summary>
        public static A2BEffectBuilder From(A2BEffectDefinition source)
        {
            // ownsEmission: false — Clone() is shallow over strategies, so the cloned definition still
            // points at the SOURCE's emission. Mutating it here would edit the source asset (FR-2).
            if (source == null) return new A2BEffectBuilder(new A2BEffectDefinition(), ownsEmission: true);
            return new A2BEffectBuilder(source.Clone(), ownsEmission: false);
        }

        public A2BEffectBuilder Duration(float seconds) { _def.Duration = Mathf.Max(0.01f, seconds); return this; }

        public A2BEffectBuilder DurationJitter(float fraction) { _def.DurationJitter = Mathf.Clamp01(fraction); return this; }

        public A2BEffectBuilder Path(IA2BPath path) { _def.Path = path; return this; }

        public A2BEffectBuilder Linear() { _def.Path = new A2BLinearPath(); return this; }

        public A2BEffectBuilder Arc(float height, float bias = 0.5f, float jitter = 0.25f)
        {
            _def.Path = new A2BBezierPath { ArcHeight = height, ArcBias = bias, ArcJitter = jitter };
            return this;
        }

        public A2BEffectBuilder Spiral(float amplitude, float frequency = 2f)
        {
            _def.Path = new A2BProceduralPath { Amplitude = amplitude, Frequency = frequency };
            return this;
        }

        /// <summary>
        /// The classic reward: items explode outward, hang for a beat, then get sucked to the target.
        ///
        /// Distinct from <see cref="Arc"/> plus <see cref="Scatter"/> — scatter moves where an item
        /// STARTS, so it reads as one continuous flight. This reads as two beats, and the pause at the
        /// peak is what sells it.
        /// </summary>
        /// <param name="radius">Outward distance in working-space units. Canvas units are pixels, so
        /// this wants ~100s there and ~1-3 in world space.</param>
        /// <param name="burstFraction">Portion of the flight spent flying outward.</param>
        /// <param name="hold">Portion spent hanging at the peak before the gather.</param>
        /// <param name="planar">True keeps the spray in the XY plane (Canvas/2D); false sprays in 3D.</param>
        public A2BEffectBuilder BurstThenGather(
            float radius, float burstFraction = 0.35f, float hold = 0.12f, bool planar = true)
        {
            _def.Path = new A2BBurstGatherPath
            {
                BurstRadius = Mathf.Max(0f, radius),
                BurstFraction = Mathf.Clamp(burstFraction, 0.05f, 0.9f),
                HoldFraction = Mathf.Clamp(hold, 0f, 0.5f),
                BurstAxisWeights = planar ? new Vector3(1f, 1f, 0f) : Vector3.one,
            };
            return this;
        }

        public A2BEffectBuilder Easing(IA2BEasing easing) { _def.Easing = easing; return this; }

        public A2BEffectBuilder Ease(A2BEaseKind kind) { _def.Easing = new A2BStandardEasing(kind); return this; }

        /// <summary>
        /// Uses the caller's emission instance as-is. Ownership passes to the caller: a later
        /// Count()/Stagger()/Scatter() copies before writing rather than mutating what was passed in.
        /// </summary>
        public A2BEffectBuilder Emission(IA2BEmission emission)
        {
            _def.Emission = emission;
            _ownsEmission = false;
            return this;
        }

        public A2BEffectBuilder Count(int count)
        {
            EnsureBurst().MinCount = EnsureBurst().MaxCount = Mathf.Max(1, count);
            return this;
        }

        public A2BEffectBuilder Count(int min, int max)
        {
            A2BBurstEmission e = EnsureBurst();
            e.MinCount = Mathf.Max(1, min);
            e.MaxCount = Mathf.Max(e.MinCount, max);
            return this;
        }

        public A2BEffectBuilder Stagger(float interval)
        {
            A2BBurstEmission e = EnsureBurst();
            e.ReleaseMode = A2BReleaseMode.FixedStagger;
            e.StaggerInterval = Mathf.Max(0f, interval);
            return this;
        }

        public A2BEffectBuilder SpreadOver(float duration)
        {
            A2BBurstEmission e = EnsureBurst();
            e.ReleaseMode = A2BReleaseMode.SpreadOverDuration;
            e.SpreadDuration = Mathf.Max(0f, duration);
            return this;
        }

        public A2BEffectBuilder AllAtOnce()
        {
            EnsureBurst().ReleaseMode = A2BReleaseMode.AllAtOnce;
            return this;
        }

        public A2BEffectBuilder Scatter(float radius)
        {
            EnsureBurst().ScatterRadius = Mathf.Max(0f, radius);
            return this;
        }

        public A2BEffectBuilder Scatter(float radius, Vector3 axisWeights)
        {
            A2BBurstEmission e = EnsureBurst();
            e.ScatterRadius = Mathf.Max(0f, radius);
            e.ScatterAxisWeights = axisWeights;
            return this;
        }

        public A2BEffectBuilder ScaleOverProgress(AnimationCurve curve) { _def.ScaleOverProgress = curve; return this; }

        public A2BEffectBuilder ColorOverProgress(Gradient gradient) { _def.ColorOverProgress = gradient; return this; }

        public A2BEffectBuilder AlignToVelocity(bool align = true) { _def.AlignToVelocity = align; return this; }

        public A2BEffectBuilder UseUnscaledTime(bool unscaled = true) { _def.UseUnscaledTime = unscaled; return this; }

        public A2BEffectBuilder OnEndpointLost(A2BEndpointLostPolicy policy) { _def.EndpointLostPolicy = policy; return this; }

        public A2BEffectBuilder Prewarm(int count) { _def.PrewarmCount = Mathf.Max(0, count); return this; }

        /// <summary>Returns the built definition. Cache it; do not rebuild per play.</summary>
        public A2BEffectDefinition Build() => _def;

        /// <summary>
        /// Returns an emission this builder OWNS, copying on first write.
        ///
        /// Without the copy, `From(asset).Stagger(0.1f)` reached through the cloned definition into
        /// the asset's own A2BBurstEmission and edited it — so "overriding an asset in code" silently
        /// rewrote the asset on disk and every other effect using it, exactly inverting FR-2. The
        /// definition clone is shallow by design (strategies are shared), which makes this
        /// copy-on-write the builder's responsibility.
        /// </summary>
        private A2BBurstEmission EnsureBurst()
        {
            if (_ownsEmission && _def.Emission is A2BBurstEmission owned) return owned;

            if (_def.Emission is A2BBurstEmission shared)
            {
                A2BBurstEmission copy = shared.Copy();
                _def.Emission = copy;
                _ownsEmission = true;
                return copy;
            }

            var created = new A2BBurstEmission();
            _def.Emission = created;
            _ownsEmission = true;
            return created;
        }
    }
}
