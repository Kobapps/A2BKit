using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace A2BKit.UIParticles
{
    /// <summary>
    /// Renders world-space <see cref="ParticleSystem"/>s, <see cref="TrailRenderer"/>s and
    /// <see cref="LineRenderer"/>s INTO a uGUI canvas — maskable, sortable, batched, with no extra
    /// camera, RenderTexture or Canvas. This is what lets a trail or particle burst show over a
    /// screen-space HUD, which a plain `TrailRenderer`/`ParticleSystem` cannot do (they are world-space
    /// mesh renderers a screen-space overlay never draws).
    ///
    /// How: every source exposes a Bake API (<c>TrailRenderer.BakeMesh</c>, <c>LineRenderer.BakeMesh</c>,
    /// <c>ParticleSystemRenderer.BakeMesh</c>/<c>BakeTrailsMesh</c>) that snapshots its current geometry
    /// into a Mesh. We bake each registered source, lift it out of its own simulation space (see
    /// <see cref="SimulationToWorld"/>) into this graphic's local space, combine the lot into ONE mesh, and
    /// hand it to our <see cref="CanvasRenderer"/> — one draw call for the whole set, drawn in canvas order
    /// like any other UI element.
    ///
    /// Any simulation space works. LOCAL space is the one to reach for when the effect decorates a UI
    /// element that MOVES — a cell in a scroll view, a card sliding in — because the particles then travel
    /// with it for free; world space leaves already-emitted particles behind in mid-air.
    ///
    /// Allocation discipline: the bake runs every frame, so it reuses everything — one worker mesh, a
    /// grown-once pool of per-source bake meshes, and shared scratch lists. Baking on
    /// <see cref="Canvas.willRenderCanvases"/> means the snapshot is taken at the same instant the canvas
    /// is about to draw, so the trail never lags the item by a frame.
    ///
    /// This type is deliberately SELF-CONTAINED — it depends only on UnityEngine and uGUI, never on the
    /// rest of A2BKit — so it can be lifted into a standalone UI-particle package unchanged.
    /// </summary>
    [AddComponentMenu("UI/Effects/A2B UI Particle")]
    [RequireComponent(typeof(CanvasRenderer))]
    public sealed class A2BUIParticle : MaskableGraphic
    {
        [Tooltip("A ParticleSystem to render on the canvas (its particles and, if enabled, its trails). " +
                 "Optional — you can also Register renderers from code.")]
        public ParticleSystem Source;

        [Tooltip("Camera the ribbons/billboards orient to while baking. Empty uses the canvas camera, then " +
                 "Camera.main, then a shared 2D bake camera created on demand for screen-space canvases.")]
        public Camera BakeCamera;

        [SerializeField, Tooltip("Texture sampled by the UI material. Empty pulls it from the first source's material.")]
        private Texture _texture;

        // ---- shared, never per-frame allocated -------------------------------------------------
        private static readonly List<CombineInstance> s_Combine = new List<CombineInstance>(32);
        private static readonly List<Color> s_Colors = new List<Color>(1024);
        private static Camera s_SharedBakeCamera;

        // A registered source and whether to bake its particle-system TRAIL module (vs its particles).
        // Irrelevant for TrailRenderer/LineRenderer, which have exactly one geometry.
        private readonly struct Src
        {
            public readonly Renderer Renderer;
            public readonly bool Trails;
            public Src(Renderer renderer, bool trails) { Renderer = renderer; Trails = trails; }
        }

        // ---- per-instance, grown once then reused ----------------------------------------------
        private readonly List<Src> _sources = new List<Src>(16);
        private readonly List<Mesh> _bakeMeshes = new List<Mesh>(16);   // one scratch mesh per combine slot
        private CombineInstance[] _combineArray = System.Array.Empty<CombineInstance>();
        private Mesh _worker;
        private int _lastBakeFrame = -1;

        /// <summary>Texture the CanvasRenderer samples — the particle/trail texture, so it draws textured.</summary>
        public override Texture mainTexture => _texture != null ? _texture : base.mainTexture;

        /// <summary>Adds a renderer to bake each frame. Cheap; ignores nulls and duplicates.</summary>
        public void Register(Renderer renderer) => Register(renderer, bakeTrails: false);

        /// <summary>
        /// Adds a renderer to bake each frame. Set <paramref name="bakeTrails"/> to bake a particle
        /// system's TRAIL module rather than its particles — register the same renderer twice (once each)
        /// to draw a particle system that has both.
        /// </summary>
        public void Register(Renderer renderer, bool bakeTrails)
        {
            if (renderer == null) return;
            for (int i = 0; i < _sources.Count; i++)
                if (_sources[i].Renderer == renderer && _sources[i].Trails == bakeTrails) return;
            _sources.Add(new Src(renderer, bakeTrails));
            if (_texture == null) PullTextureFrom(renderer);
        }

        /// <summary>Stops baking a renderer (both its particle and trail entries). Safe if never registered.</summary>
        public void Unregister(Renderer renderer)
        {
            if (renderer == null) return;
            for (int i = _sources.Count - 1; i >= 0; i--)
                if (_sources[i].Renderer == renderer) _sources.RemoveAt(i);
        }

        /// <summary>Explicitly sets the sampled texture (e.g. the trail's sprite), overriding auto-detect.</summary>
        public void SetTexture(Texture texture)
        {
            _texture = texture;
            SetMaterialDirty();
        }

        protected override void OnEnable()
        {
            base.OnEnable();
            if (_worker == null) _worker = NewMesh("A2B UI Particle (worker)");
            if (Source != null)
            {
                var psr = Source.GetComponent<ParticleSystemRenderer>();
                Register(psr, bakeTrails: false);                       // particles
                if (Source.trails.enabled) Register(psr, bakeTrails: true);   // its trail module
            }
            Canvas.willRenderCanvases += Bake;
        }

        protected override void OnDisable()
        {
            Canvas.willRenderCanvases -= Bake;
            if (canvasRenderer != null) canvasRenderer.SetMesh(null);
            base.OnDisable();
        }

        protected override void OnDestroy()
        {
            if (_worker != null) DestroyMesh(_worker);
            for (int i = 0; i < _bakeMeshes.Count; i++) DestroyMesh(_bakeMeshes[i]);
            _bakeMeshes.Clear();
            base.OnDestroy();
        }

        // The CanvasRenderer's MESH is driven entirely by Bake(); the Graphic's normal rebuild must not
        // touch it. Left to its own devices, a rebuild (marked dirty by a layout or material change) runs
        // OnPopulateMesh and sets an EMPTY mesh — clobbering the baked one for that pass, which flickers.
        // No-op'ing UpdateGeometry hands the mesh to us exclusively; the material still updates normally.
        protected override void UpdateGeometry() { }

        protected override void OnPopulateMesh(VertexHelper vh) => vh.Clear();

        private void Bake()
        {
            if (canvasRenderer == null) return;

            // willRenderCanvases fires once per canvas-render PASS, not per frame — in the Editor that is
            // the Game view AND the Scene view (and any other), so without this guard the mesh is re-baked
            // and re-set several times a frame, which reads as flicker. Bake once per frame; every pass
            // then draws the same mesh.
            if (_lastBakeFrame == Time.frameCount) return;
            _lastBakeFrame = Time.frameCount;

            if (_sources.Count == 0)
            {
                canvasRenderer.SetMesh(null);
                return;
            }

            Camera cam = ResolveBakeCamera();
            Matrix4x4 worldToLocal = canvasRenderer.transform.worldToLocalMatrix;

            s_Combine.Clear();
            for (int i = 0; i < _sources.Count; i++)
            {
                Src src = _sources[i];
                if (src.Renderer == null || !src.Renderer.gameObject.activeInHierarchy) continue;

                Mesh dst = ScratchMesh(s_Combine.Count);
                dst.Clear(false);
                if (!BakeSource(src, cam, dst) || dst.vertexCount == 0) continue;

                // A bake is in its source's own SIMULATION space, which is world space for most sources but
                // not all — see SimulationToWorld. Lift it to world first, then into this graphic's local
                // space with the shared matrix.
                s_Combine.Add(new CombineInstance { mesh = dst, transform = worldToLocal * SimulationToWorld(src) });
            }

            if (s_Combine.Count == 0)
            {
                canvasRenderer.SetMesh(null);
                return;
            }

            // Reuse the combine array; it only reallocates when the number of active sources changes,
            // not every frame. CopyTo into it does not allocate.
            if (_combineArray.Length != s_Combine.Count) _combineArray = new CombineInstance[s_Combine.Count];
            s_Combine.CopyTo(_combineArray);

            _worker.Clear(false);
            _worker.CombineMeshes(_combineArray, true, true);
            FlattenAndGammaCorrect(_worker);

            canvasRenderer.SetMesh(_worker);
        }

        /// <summary>
        /// The matrix that lifts one source's baked geometry into WORLD space. Identity for the common case
        /// — a world-simulated particle system, or a line already in world space — and that is why this went
        /// unnoticed for so long.
        ///
        /// ⚠️ It is NOT identity for a particle system simulating in LOCAL (or Custom) space. Unity bakes
        /// such a system in its simulation space, and <see cref="ParticleSystemBakeMeshOptions.BakeRotationAndScale"/>
        /// folds in the transform's rotation and scale but NOT its position — verified exactly, to the float:
        /// baked + transform.position reproduces the simulated world positions. So the missing piece is the
        /// translation alone; applying a full localToWorld here would double-apply rotation and scale.
        ///
        /// Without this, a local-space system's particles were shifted by the graphic's entire world offset
        /// (hundreds of screen units) and simply vanished — which made local space, the natural way to author
        /// an effect that must travel with a moving UI element, unusable.
        ///
        /// A CUSTOM-space system is carried by its custom transform's position on the same reasoning. Its
        /// rotation and scale are not honoured: the bake applied the SYSTEM's, and there is no way to undo
        /// that here. Custom space is exact only while the two agree, which is the normal UI case.
        ///
        /// A LineRenderer is different in kind: it is baked with <c>useTransform:false</c>, so nothing at all
        /// was applied and a local-space line needs the whole localToWorld. A TrailRenderer is always world.
        /// </summary>
        private static Matrix4x4 SimulationToWorld(Src src)
        {
            switch (src.Renderer)
            {
                case ParticleSystemRenderer psr:
                    var system = psr.GetComponent<ParticleSystem>();
                    if (system == null) return Matrix4x4.identity;
                    ParticleSystem.MainModule main = system.main;
                    switch (main.simulationSpace)
                    {
                        case ParticleSystemSimulationSpace.Local:
                            return Matrix4x4.Translate(system.transform.position);
                        case ParticleSystemSimulationSpace.Custom:
                            Transform custom = main.customSimulationSpace;
                            return Matrix4x4.Translate(custom != null
                                ? custom.position
                                : system.transform.position);
                        default:
                            return Matrix4x4.identity;
                    }
                case LineRenderer line:
                    return line.useWorldSpace
                        ? Matrix4x4.identity
                        : line.transform.localToWorldMatrix;
                default:
                    return Matrix4x4.identity;
            }
        }

        /// <summary>Snapshots one source's current geometry into <paramref name="dst"/> (its simulation space).</summary>
        private static bool BakeSource(Src src, Camera cam, Mesh dst)
        {
            switch (src.Renderer)
            {
                case TrailRenderer trail:
                    trail.BakeMesh(dst, cam, false);
                    return true;
                case LineRenderer line:
                    line.BakeMesh(dst, cam, false);
                    return true;
                case ParticleSystemRenderer ps:
                    if (src.Trails)
                    {
                        var system = ps.GetComponent<ParticleSystem>();
                        if (system == null || !system.trails.enabled || system.particleCount == 0) return false;
                        ps.BakeTrailsMesh(dst, cam, ParticleSystemBakeMeshOptions.BakeRotationAndScale);
                    }
                    else
                    {
                        ps.BakeMesh(dst, cam, ParticleSystemBakeMeshOptions.BakeRotationAndScale);
                    }
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>
        /// Zeroes Z on the combined mesh (a UI graphic is flat) and, in a linear project, converts the
        /// baked vertex colours to gamma — CanvasRenderer expects gamma, and skipping this washes the
        /// colours out. Reuses <see cref="s_Colors"/>, so it does not allocate after the first frame.
        /// </summary>
        private void FlattenAndGammaCorrect(Mesh mesh)
        {
            Bounds b = mesh.bounds;
            Vector3 c = b.center; c.z = 0f; b.center = c;
            Vector3 e = b.extents; e.z = 0f; b.extents = e;
            mesh.bounds = b;

            if (QualitySettings.activeColorSpace != ColorSpace.Linear) return;

            mesh.GetColors(s_Colors);
            if (s_Colors.Count == 0) return;
            for (int i = 0; i < s_Colors.Count; i++)
            {
                Color col = s_Colors[i];
                col.r = Mathf.LinearToGammaSpace(col.r);
                col.g = Mathf.LinearToGammaSpace(col.g);
                col.b = Mathf.LinearToGammaSpace(col.b);
                s_Colors[i] = col;
            }
            mesh.SetColors(s_Colors);
        }

        private Camera ResolveBakeCamera()
        {
            if (BakeCamera != null) return BakeCamera;
            if (canvas != null && canvas.worldCamera != null) return canvas.worldCamera;
            if (Camera.main != null) return Camera.main;

            // Screen-space-overlay canvases have no camera; a shared, hidden 2D camera gives the bake a
            // stable orientation and, crucially, a frustum big enough to CONTAIN the geometry — BakeMesh
            // snapshots only what the camera would see, and UI particles live out at screen-pixel world
            // coordinates (hundreds to thousands). A very large orthographic view captures them all
            // without the scale-to-fit dance UIParticle does. It never renders (culling mask nothing).
            if (s_SharedBakeCamera == null)
            {
                var go = new GameObject("[A2B UI Bake Camera]") { hideFlags = HideFlags.HideAndDontSave };
                s_SharedBakeCamera = go.AddComponent<Camera>();
                s_SharedBakeCamera.enabled = false;
                s_SharedBakeCamera.orthographic = true;
                s_SharedBakeCamera.orthographicSize = 10000f;
                s_SharedBakeCamera.nearClipPlane = 0.1f;
                s_SharedBakeCamera.farClipPlane = 20000f;
                s_SharedBakeCamera.cullingMask = 0;
                s_SharedBakeCamera.clearFlags = CameraClearFlags.Nothing;
                go.transform.SetPositionAndRotation(new Vector3(0f, 0f, -5000f), Quaternion.identity);
            }
            return s_SharedBakeCamera;
        }

        private void PullTextureFrom(Renderer renderer)
        {
            Material m = renderer != null ? renderer.sharedMaterial : null;
            if (m != null && m.HasProperty("_MainTex") && m.mainTexture != null) _texture = m.mainTexture;
        }

        private Mesh ScratchMesh(int index)
        {
            while (_bakeMeshes.Count <= index) _bakeMeshes.Add(NewMesh("A2B UI Particle (bake)"));
            return _bakeMeshes[index];
        }

        private static Mesh NewMesh(string name)
        {
            return new Mesh { name = name, hideFlags = HideFlags.HideAndDontSave };
        }

        private static void DestroyMesh(Mesh mesh)
        {
            if (mesh == null) return;
            if (Application.isPlaying) Destroy(mesh);
            else DestroyImmediate(mesh);
        }
    }
}
