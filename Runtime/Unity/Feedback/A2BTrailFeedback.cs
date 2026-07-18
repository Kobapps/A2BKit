using System.Collections.Generic;
using A2BKit.Core;
using A2BKit.UIParticles;
using UnityEngine;
using UnityEngine.Pool;

namespace A2BKit.Unity
{
    /// <summary>How an arrived item's trail leaves the screen. Applies to the on-canvas (baked) path;
    /// off-canvas world trails always age their own tail out.</summary>
    public enum A2BTrailClearMode
    {
        /// <summary>Snap off the instant the item lands. Cheapest, but pops.</summary>
        Immediate,

        /// <summary>Fade the trail's opacity to transparent in place over <see cref="A2BTrailFeedback.Time"/>.</summary>
        Fade,

        /// <summary>Shrink the trail's width to nothing in place over <see cref="A2BTrailFeedback.Time"/>.</summary>
        Scale,
    }

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

        /// <summary>
        /// How the trail leaves once its item lands: <see cref="A2BTrailClearMode.Fade"/> dissolves it,
        /// <see cref="A2BTrailClearMode.Scale"/> thins it away, <see cref="A2BTrailClearMode.Immediate"/>
        /// snaps it off. The animated modes run over <see cref="ClearSpan"/> and apply on the canvas path.
        /// </summary>
        public A2BTrailClearMode ClearMode = A2BTrailClearMode.Fade;

        /// <summary>Seconds the Fade/Scale clear takes after an item lands. 0 falls back to <see cref="Time"/>.</summary>
        [Min(0f)] public float ClearDuration = 0f;

        /// <summary>Optional material. Null resolves an unlit fallback rather than drawing magenta (AD-8).</summary>
        public Material Material;

        /// <summary>Distance the item must travel before a new point is laid down. Higher is cheaper.</summary>
        public float MinVertexDistance = 0.05f;

        /// <summary>Extra vertices that round each bend in the ribbon. 0 keeps hard mitres (cheapest).</summary>
        public int CornerVertices = 0;

        /// <summary>Extra vertices that round the two ribbon ends. 0 keeps flat caps (cheapest).</summary>
        public int CapVertices = 0;

        /// <summary>
        /// Draw the on-canvas trail as a soft ADDITIVE glow rather than a hard-edged solid ribbon. On a
        /// Canvas a comet effect fires several trails from one origin; they bunch and overlap, and with a
        /// solid ribbon every overlap is a hard-edged block whose boundary jumps frame to frame as the
        /// trails slide over each other — the "square/flat-edge flicker". A soft edge has no hard boundary
        /// to jump, and additive blend makes overlaps sum to a smooth brighter glow (order-independent)
        /// instead of a shape. Canvas-only; off-canvas world trails are untouched. Off by default — it
        /// changes the look to a glow. The tail triangle/square flicker itself is fixed independently by the
        /// width curve (see CreateTrail); enable this only if you also want the glow aesthetic.
        /// </summary>
        public bool SoftGlow = false;

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

            /// <summary>Seconds of clear-animation left after the item arrived. 0 = not clearing.</summary>
            public float FadeRemaining;
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

        // Set when the effect plays on a Canvas: a world-space TrailRenderer never draws on a screen-space
        // canvas, so we feed every active trail into one A2BUIParticle baker that renders them ALL into a
        // single CanvasRenderer — trails over UI, one draw call. Null off-canvas (the trail draws itself).
        private A2BUIParticle _uiBaker;

        // A trail whose item has ARRIVED is not snapped off (which pops at the target); it detaches in
        // place, stops emitting, and lives here — still drawing through the baker — while a per-frame pass
        // shrinks its width to nothing over Time. The pass is driven by Canvas.willRenderCanvases (the same
        // always-firing event the baker uses), NOT a MonoBehaviour: the whole effect hierarchy hangs off a
        // pooled canvas whose scene is never loaded, and Unity does not pump Update/LateUpdate there.
        private List<Binding> _fading;
        private Transform _fadingRoot;
        private bool _fadeSubscribed;
        private int _lastFadeFrame = -1;

        // Reused scratch for the Fade clear, so dimming a trail's gradient does not allocate a Gradient (or
        // key arrays) per fading trail per frame. Built lazily from the base Color the first time it's needed.
        private Gradient _clearGradient;
        private GradientColorKey[] _clearColorKeys;
        private GradientAlphaKey[] _clearBaseAlpha;
        private GradientAlphaKey[] _clearScratchAlpha;

        public override string FeedbackKey => "Trail";

        /// <summary>The trail's un-faded width scale (widthMultiplier at rest); the width curve is normalised against it.</summary>
        private float BaseWidthMultiplier => Mathf.Max(Mathf.Max(0f, StartWidth), Mathf.Max(0f, EndWidth), 0.0001f);

        /// <summary>Seconds the clear animation runs: the explicit <see cref="ClearDuration"/>, or <see cref="Time"/> if unset.</summary>
        private float ClearSpan => Mathf.Max(0.02f, ClearDuration > 0f ? ClearDuration : Time);

        protected override void OnInitialized()
        {
            _disposed = false;
            _all = new List<Binding>(Mathf.Max(8, DefaultCapacity));
            _byTrail = new Dictionary<Transform, Binding>(Mathf.Max(8, DefaultCapacity), A2BTransformComparer.Instance);
            _byItem = new Dictionary<Transform, Binding>(Mathf.Max(8, DefaultCapacity), A2BTransformComparer.Instance);
            _fading = new List<Binding>(Mathf.Max(8, DefaultCapacity));

            // Drop the Fade-clear scratch cloned in from the template by CreateRuntimeInstance, so this
            // instance rebuilds its OWN (never sharing the arrays, and never a half-null clone state).
            _clearGradient = null;
            _clearColorKeys = null;
            _clearBaseAlpha = null;
            _clearScratchAlpha = null;

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

            // On a Canvas the trails are invisible on their own — build the baker that draws them.
            TryCreateUIBaker();

            // An ACTIVE home for arrived-but-still-fading trails (the pool root is inactive, which would
            // stop the baker from drawing them mid-fade). The fade itself is pumped from
            // Canvas.willRenderCanvases (see _fading) — a MonoBehaviour here would never tick, since this
            // whole hierarchy lives under a pooled canvas whose scene is not loaded.
            var fadeRootObject = new GameObject("A2B Fading Trails");
            _fadingRoot = fadeRootObject.transform;
            _fadingRoot.SetParent(Root, false);

            // Only the canvas path fades (it drives off the always-firing canvas event); off-canvas the
            // baker is null and trails snap back to the pool as before.
            if (_uiBaker != null && !_fadeSubscribed)
            {
                Canvas.willRenderCanvases += ProcessFading;
                _fadeSubscribed = true;
            }

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

            // Hand the trail to the canvas baker so it draws on the UI (no-op off-canvas).
            if (_uiBaker != null && binding.Trail != null) _uiBaker.Register(binding.Trail);
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

            // An ARRIVED trail animates away over Time (ClearMode) instead of snapping off at the target,
            // which pops. Only on a Canvas: the animation is driven by the baker's render event, and
            // off-canvas a world trail ages its own tail. A CANCELLED trail is gone with the effect, and
            // ClearMode.Immediate opts out of the animation entirely.
            bool animateClear = reason == A2BReleaseReason.Arrived
                                && Time > 0f
                                && binding.Trail != null
                                && _uiBaker != null
                                && ClearMode != A2BTrailClearMode.Immediate;
            if (animateClear)
                FadeOut(binding);
            else
                ReturnTrail(binding);
        }

        /// <summary>
        /// Detaches the trail in place and starts it fading instead of snapping off at the target. It stays
        /// active and registered with the baker while <see cref="ProcessFading"/> (pumped from
        /// <see cref="Canvas.willRenderCanvases"/>) scales its width to nothing over the trail's own Time —
        /// a width animation, not natural point ageing, because a stationary <c>forceRenderingOff</c> trail
        /// freezes its points and would never empty on its own.
        /// </summary>
        private void FadeOut(Binding binding)
        {
            if (binding.Trail != null) binding.Trail.emitting = false;

            // worldPositionStays:true keeps the ribbon exactly where the item left it (its points are in
            // world space regardless, but the transform must not jump the drawn frame).
            if (binding.Root != null) binding.Root.SetParent(_fadingRoot, worldPositionStays: true);

            binding.Emitting = false;
            binding.FadeRemaining = ClearSpan;
            _fading.Add(binding);
        }

        /// <summary>
        /// Runs once per rendered frame (from <see cref="Canvas.willRenderCanvases"/>): shrink each fading
        /// trail's width toward zero and retire it to the pool once the fade completes. Time-driven so it
        /// always converges — no reliance on the trail emptying itself. The event fires once per canvas
        /// render PASS (Game + Scene view in the editor); the frame guard collapses those to one tick.
        /// </summary>
        private void ProcessFading()
        {
            if (_disposed || _fading == null) return;

            // One age-out per frame, no matter how many canvases/passes render this frame.
            int frame = UnityEngine.Time.frameCount;
            if (_lastFadeFrame == frame) return;
            _lastFadeFrame = frame;

            float dt = UnityEngine.Time.deltaTime;
            float fadeSpan = ClearSpan;

            for (int i = _fading.Count - 1; i >= 0; i--)
            {
                Binding binding = _fading[i];
                if (binding == null || binding.Trail == null)
                {
                    _fading.RemoveAt(i);
                    ReturnTrail(binding);
                    continue;
                }

                binding.FadeRemaining -= dt;
                if (binding.FadeRemaining <= 0f)
                {
                    _fading.RemoveAt(i);
                    ReturnTrail(binding);
                    continue;
                }

                // f: 1 → 0 across the clear. Fade dissolves the opacity, Scale thins the width; both re-bake
                // each frame off the frozen points, so neither depends on the trail ageing.
                float f = Mathf.Clamp01(binding.FadeRemaining / fadeSpan);
                if (ClearMode == A2BTrailClearMode.Fade)
                    ApplyClearAlpha(binding.Trail, f);
                else
                    binding.Trail.widthMultiplier = BaseWidthMultiplier * f;
            }
        }

        /// <summary>
        /// Scales the trail's colour-gradient alpha by <paramref name="mul"/> (1 = opaque, 0 = transparent)
        /// for the Fade clear. Absolute, not cumulative — rebuilt from the base Color each call — so it is
        /// safe to call every frame and to reset with mul = 1. The baker re-bakes the dimmed vertex colours.
        /// </summary>
        private void ApplyClearAlpha(TrailRenderer trail, float mul)
        {
            if (trail == null) return;
            EnsureClearGradient();
            for (int i = 0; i < _clearBaseAlpha.Length; i++)
            {
                GradientAlphaKey k = _clearBaseAlpha[i];
                _clearScratchAlpha[i] = new GradientAlphaKey(k.alpha * mul, k.time);
            }
            _clearGradient.SetKeys(_clearColorKeys, _clearScratchAlpha);
            trail.colorGradient = _clearGradient;
        }

        /// <summary>Lazily snapshots the base gradient (the effect's <see cref="Color"/>, or opaque white) into reusable buffers.</summary>
        private void EnsureClearGradient()
        {
            // Guard on EVERY buffer, not just the gradient: a MemberwiseClone or a domain reload can leave
            // the gradient set while an array is null, and a half-built state is what spammed the NRE.
            if (_clearGradient != null && _clearBaseAlpha != null && _clearScratchAlpha != null && _clearColorKeys != null)
                return;

            Gradient src = Color;
            if (src == null)
            {
                src = new Gradient();
                src.SetKeys(
                    new[] { new GradientColorKey(UnityEngine.Color.white, 0f), new GradientColorKey(UnityEngine.Color.white, 1f) },
                    new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(1f, 1f) });
            }

            _clearColorKeys = src.colorKeys;              // returns a fresh copy; kept as the constant colour track
            _clearBaseAlpha = src.alphaKeys;              // the un-dimmed alpha track we scale from
            _clearScratchAlpha = new GradientAlphaKey[_clearBaseAlpha.Length];
            _clearGradient = new Gradient();
        }

        /// <summary>Immediately parks a trail back in the pool, cleared and inactive.</summary>
        private void ReturnTrail(Binding binding)
        {
            if (binding == null || _pool == null || _disposed) return;

            // Stop baking this trail before it detaches, so its last frame is not smeared to the pool.
            if (_uiBaker != null && binding.Trail != null) _uiBaker.Unregister(binding.Trail);

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
                binding.Trail.widthMultiplier = BaseWidthMultiplier;   // undo a Scale-clear shrink before reuse.
                if (ClearMode == A2BTrailClearMode.Fade)
                    ApplyClearAlpha(binding.Trail, 1f);                // undo a Fade-clear dim before reuse.
                binding.Trail.Clear();                                 // Clear #2: the one the next coin needs.
            }

            binding.Emitting = false;
            binding.FadeRemaining = 0f;
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
            _fading?.Clear();

            // Stop the age-out pass before tearing down; a dangling static-event subscription would keep
            // this instance (and everything it holds) alive across a domain reload.
            if (_fadeSubscribed)
            {
                Canvas.willRenderCanvases -= ProcessFading;
                _fadeSubscribed = false;
            }

            // Destroying the fading root takes any still-fading trails (its children) with it.
            if (_fadingRoot != null)
            {
                A2BFeedbackKit.Destroy(_fadingRoot.gameObject);
                _fadingRoot = null;
            }

            if (_poolRoot != null)
            {
                A2BFeedbackKit.Destroy(_poolRoot.gameObject);
                _poolRoot = null;
            }

            if (_uiBaker != null)
            {
                A2BFeedbackKit.Destroy(_uiBaker.gameObject);
                _uiBaker = null;
            }

            if (_ownedMaterial != null)
            {
                A2BFeedbackKit.Destroy(_ownedMaterial);
                _ownedMaterial = null;
            }
        }

        /// <summary>
        /// If the effect plays under a Canvas, builds one <see cref="A2BUIParticle"/> baker as a UI child
        /// of the adapter Root. Every active trail registers with it and is drawn into a single
        /// CanvasRenderer — the whole point being that a TrailRenderer is a world-space mesh renderer a
        /// screen-space canvas never draws. Off-canvas this is a no-op and the trails draw themselves.
        /// </summary>
        private void TryCreateUIBaker()
        {
            Canvas canvas = Root != null ? Root.GetComponentInParent<Canvas>() : null;
            if (canvas == null) return;

            var go = new GameObject("A2B UI Trails", typeof(RectTransform), typeof(A2BUIParticle));
            var rect = (RectTransform)go.transform;
            rect.SetParent(Root, false);
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
            rect.localPosition = Vector3.zero;

            _uiBaker = go.GetComponent<A2BUIParticle>();
            _uiBaker.raycastTarget = false;

            if (SoftGlow)
            {
                // Soft additive glow: the fix for overlapping-comet flicker (see SoftGlow). The baker draws
                // with an additive material so overlaps sum, and samples a ribbon texture that fades to
                // nothing at the two long edges so there is no hard boundary to flicker.
                _uiBaker.material = SharedAdditiveMaterial();
                _uiBaker.SetTexture(SharedSoftRibbonTexture());
            }
            else
            {
                // Solid ribbon: the trail's own texture (if any) drives the UI material; a plain coloured
                // trail bakes its gradient into the vertex colours and needs none.
                Material trailMat = Material != null ? Material : _ownedMaterial;
                if (trailMat != null && trailMat.HasProperty("_MainTex") && trailMat.mainTexture != null)
                    _uiBaker.SetTexture(trailMat.mainTexture);
            }
        }

        /// <summary>
        /// A width curve, normalised against <paramref name="multiplier"/>, that runs from the head width
        /// down to the tail width by ~75% of the length and then holds flat. The flat tail zone is where
        /// <see cref="TrailRenderer"/>'s virtual ageing-out vertex lives; keeping the whole zone at the tail
        /// width means that vertex has nothing to oscillate between, killing the triangle/square tail flip.
        /// </summary>
        private static AnimationCurve BuildStableWidthCurve(float startW, float endW, float multiplier)
        {
            float head = multiplier > 0f ? Mathf.Clamp01(startW / multiplier) : 1f;
            float tail = multiplier > 0f ? Mathf.Clamp01(endW / multiplier) : 0f;

            const float hold = 0.75f;   // taper is done here; hold flat from here to the tail
            var curve = new AnimationCurve(
                new Keyframe(0f, head),
                new Keyframe(hold, tail),
                new Keyframe(1f, tail));

            // Flat tangents on the held segment so it does not dip below the tail width and re-introduce a
            // thin oscillating sliver; smooth the head so the taper reads as a curve, not a wedge.
            curve.SmoothTangents(0, 0f);
            for (int i = 1; i < curve.length; i++) curve.SmoothTangents(i, 0f);
            return curve;
        }

        // One soft ribbon texture and one additive material for every trail feedback in the project; both
        // are read-only and never mutated after creation, so sharing them costs nothing and allocates once.
        private static Texture2D s_SoftRibbon;
        private static Material s_AdditiveMaterial;

        /// <summary>
        /// A ribbon-cross-section texture: opaque down the centre line, fading smoothly to transparent at
        /// the two edges. Baked trail UVs run V (0..1) across the ribbon width, so this softens the long
        /// sides. 1 texel wide (constant along the length), tall enough for a clean gradient.
        /// </summary>
        private static Texture2D SharedSoftRibbonTexture()
        {
            if (s_SoftRibbon != null) return s_SoftRibbon;

            const int h = 64;
            var tex = new Texture2D(1, h, TextureFormat.RGBA32, mipChain: false)
            {
                name = "A2B Soft Ribbon",
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
                hideFlags = HideFlags.HideAndDontSave,
            };
            for (int y = 0; y < h; y++)
            {
                float v = y / (h - 1f);
                float edge = Mathf.Abs(v * 2f - 1f);          // 0 at centre, 1 at either edge
                float a = Mathf.SmoothStep(1f, 0f, edge);     // soft, symmetric falloff
                tex.SetPixel(0, y, new Color(a, a, a, a));    // greyscale in RGB too: additive fades with it
            }
            tex.Apply(updateMipmaps: false);
            s_SoftRibbon = tex;
            return tex;
        }

        /// <summary>Additive material (Blend One One) so overlapping trails sum to a smooth glow, order-free.</summary>
        private static Material SharedAdditiveMaterial()
        {
            if (s_AdditiveMaterial != null) return s_AdditiveMaterial;

            Shader shader = Shader.Find("Mobile/Particles/Additive")
                          ?? Shader.Find("Legacy Shaders/Particles/Additive")
                          ?? Shader.Find("Sprites/Default");
            s_AdditiveMaterial = new Material(shader)
            {
                hideFlags = HideFlags.HideAndDontSave,
                mainTexture = SharedSoftRibbonTexture(),
            };
            return s_AdditiveMaterial;
        }

        private Transform CreateTrail()
        {
            var instance = new GameObject("A2B Trail");
            var trail = instance.AddComponent<TrailRenderer>();

            // On a Canvas, DO NOT age the trail during flight. If points expire on a timer (time = Time),
            // then near the target — where the item decelerates — the tail keeps expiring faster than the
            // slow head extends it, so the whole ribbon shrinks and re-tapers every frame: that is the
            // flicker "closer to the end point". A time long enough to outlast any flight means points are
            // only ever ADDED (at the head), never removed, so the geometry is stable frame to frame and
            // there is nothing to flicker. The streak still clears on release and fades on arrival.
            trail.time = _uiBaker != null ? 3600f : Mathf.Max(0.001f, Time);

            // On a Canvas, commit a point every frame (minVertexDistance ~0) so the head stays glued to the
            // item. With a larger distance, a DECELERATING item near the target creeps forward slower than
            // the threshold: no new point is committed, yet the head segment from the last point to the item
            // still draws — a hard-edged rectangle that grows, then snaps back when a point finally commits.
            trail.minVertexDistance = _uiBaker != null ? 0f : Mathf.Max(0f, MinVertexDistance);

            // Width: NOT the plain startWidth/endWidth linear taper. As the oldest point ages out, Unity
            // interpolates a virtual tail vertex whose width oscillates between the last point's width and
            // zero every frame — the tail visibly flips between a point (triangle) and a flat edge (square)
            // during movement. Reaching the tail width EARLY (~75% along) and holding it flat to the end
            // parks that oscillating virtual vertex in a zone that is already at the tail width, so there is
            // nothing left to flip. widthMultiplier carries the scale; the curve is the normalised shape.
            float startW = Mathf.Max(0f, StartWidth);
            float endW = Mathf.Max(0f, EndWidth);
            trail.widthMultiplier = BaseWidthMultiplier;
            trail.widthCurve = BuildStableWidthCurve(startW, endW, BaseWidthMultiplier);

            // Round the bends and end caps. Without these a curved trail's hard mitres fold and pinch,
            // flickering between a clean strip and a blob as the arc sweeps along it (invisible to the
            // per-ring width but very visible on screen). Clamped to Unity's 0..90 accepted range.
            trail.numCornerVertices = Mathf.Clamp(CornerVertices, 0, 90);
            trail.numCapVertices = Mathf.Clamp(CapVertices, 0, 90);

            // autodestruct would Destroy the GameObject when the trail empties — destroying an object
            // this pool still believes it owns, which is a use-after-free, not a convenience.
            trail.autodestruct = false;
            trail.emitting = false;

            // When we bake onto a canvas, the world-space TrailRenderer must NOT also draw itself: the
            // two copies overlap and double the alpha where they cross, which reads as flicker. forceRendering
            // Off disables the draw but keeps the component simulating, so BakeMesh still has geometry.
            if (_uiBaker != null)
            {
                trail.forceRenderingOff = true;

                // Lay the ribbon flat in its own plane instead of billboarding each segment to the bake
                // camera. View alignment makes a fast, curving trail's segments face the camera at slightly
                // different angles and overlap as jittering fish-scale chevrons — THE flicker. On a flat
                // canvas the trail lives in one plane, so TransformZ gives a stable, continuous ribbon.
                trail.alignment = LineAlignment.TransformZ;
            }

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
