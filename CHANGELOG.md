# Changelog

All notable changes to A2BKit are documented here.

## [0.3.0] — 2026-07-18

### Added

- **Particles and trails over a UI canvas — `A2BUIParticle`.** A `MaskableGraphic` that bakes world-space
  `ParticleSystem`s (particles + their trail module), `TrailRenderer`s and `LineRenderer`s into its
  `CanvasRenderer` every frame — so they render over a screen-space HUD, maskable and sortable, in one
  draw call, with no extra Camera, RenderTexture or Canvas. Drop it on a UI object with a `ParticleSystem`,
  or `Register()` any renderer. It bakes on `Canvas.willRenderCanvases` and reuses its meshes and buffers,
  so it holds to the package's zero-per-frame-allocation rule. The bake is guarded to run once per frame
  (not once per canvas-render pass) and the graphic's own geometry rebuild is suppressed, so the mesh does
  not flicker when several views render the canvas. It lives in its **own assembly**
  (`A2BKit.UIParticles`) with no dependency on the rest of A2BKit, so it can be lifted into a standalone
  package unchanged.
- **UI Particles sample (scene 10).** A glow behind a card and a confetti fountain in front, both rendered
  on the canvas via `A2BUIParticle` — the standalone use of the module.
- **UI Trails sample (scene 11).** The trails-over-UI headline: A2B comets streak from the chest to the
  wallet leaving long bright tails drawn *on* the screen-space HUD, baked through `A2BUIParticle` by
  `A2BTrailFeedback`. (Scene 1 also carries a subtler coin trail.)
- **Trails now render over the UI automatically.** `A2BTrailFeedback` detects a Canvas root and routes
  every trail through one `A2BUIParticle` baker — a coin flying across a HUD now leaves a streak, which a
  raw world-space `TrailRenderer` could never draw on a screen-space canvas. On a canvas, trail widths are
  in pixels. Baked trails are laid flat (`LineAlignment.TransformZ`) instead of billboarding each segment
  to the bake camera — billboarding made a fast, curving trail's segments overlap as jittering fish-scale
  chevrons, which was the trail flicker. The world-space `TrailRenderer` also has its own rendering turned
  off (it still simulates for the bake), so the two copies never double up. The **Coin To Wallet** and
  **UI Trails** samples show this.
- **`A2BSampleCameraFit` (samples).** Keeps the 3D examples framed in any Game View aspect — portrait or
  landscape — by moving the camera along its own forward axis to fit the content, so the Mesh Collect,
  Particle Burst and Cross Space scenes no longer fall outside a narrow vertical field of view.
- **Trail clear animation — `A2BTrailFeedback.ClearMode` + `ClearDuration`.** How a trail leaves once its
  item lands: `Fade` (dissolve opacity, the default), `Scale` (shrink width to nothing), or `Immediate`
  (snap off). The animated modes run over `ClearDuration` seconds (`0` = the trail's own `Time`). The
  fade dims a reused gradient with no per-frame allocation.
- **Soft additive glow for canvas trails — `A2BTrailFeedback.SoftGlow`.** Bakes the trail with a
  soft-edged additive material so a comet effect's several trails, which bunch and overlap on their way to
  one target, sum into one bright glow instead of stacking as hard-edged ribbons whose boundaries jump
  frame to frame. `CornerVertices` / `CapVertices` round the ribbon's bends and ends.
- **The A2B Effect Editor is now UI Toolkit.** The definition is a stack of collapsible **module panels**
  — Timing, Path, Emission, Scale over Life, Colour & Orientation, Payload, Trail, Feedbacks, Advanced —
  like the Particle System inspector, driven by a bound `SerializedObject` so the subclass-selector,
  curve and gradient drawers and Undo are the native ones. Retained-mode, so the per-frame IMGUI layout is
  gone. Edits apply to the preview **live**: a paused frame re-simulates in place; a playing preview
  rebuilds when the control is released.
- **Trail panel with a live silhouette.** The Trail module draws the streak's head→tail taper, tinted by
  its gradient, above its fields — tune the look by eye.
- **3D move gizmo on path control points.** The selected spline control point (and the Bézier arc point)
  shows a full X/Y/Z move handle like a Transform; other spline points are click-to-select dots, with
  **＋** to insert a point on a segment and **−** to remove the selected one.
- **Scale / colour envelope preview in the Scene.** A row of dots along the path, each sized by
  `ScaleOverProgress` and tinted by `ColorOverProgress`, shows how an item grows and recolours in flight
  without pressing Play.

### Fixed

- **Samples: missing materials on the 3D and particle examples** (was 0.2.1). The generated coin mesh and
  materials were `HideFlags.DontSave`, so they dropped out of the scenes/assets that referenced them and
  rendered magenta. They are now baked to real assets under `Art/Generated/`.
- **Canvas trail flicker in flight — the real fix.** A baked canvas trail no longer ages its tail *during*
  flight: as an item decelerates near its target, time-based point expiry made the ribbon shrink and
  re-taper every frame (and the `TrailRenderer`'s own tail-vertex interpolation flipped the tip between a
  point and a flat edge). The trail is now a stable streak that only grows to the item and then animates
  away on arrival, and its head stays glued to the item — so the shape is steady frame to frame.
- **Editor crash when clicking a module panel or dragging a path handle.** The window was IMGUI, where a
  panel click toggles a foldout and mutates layout mid-event; and a global rename had left `AfterEdit`
  calling itself, so any edit (including a handle drag) recursed to a stack-overflow crash. The UI Toolkit
  rewrite removes the IMGUI layout entirely, and the recursion is fixed. Handle drags also defer the
  re-simulation to mouse-release, so dragging a path no longer stalls.
- **Spline control points were not draggable in the Scene.** Selecting a point only repainted the window,
  not the Scene view, so its 3D gizmo never appeared. Selection now repaints the Scene and the first point
  is auto-selected, so there is always a live gizmo to grab.
- **`NullReferenceException` from the trail fade clear.** The reused fade-gradient buffers could be a
  half-built clone from `CreateRuntimeInstance`; they are now reset per instance and fully guarded.
- **Duplicated "Path → Path" module sections.** A single `[SerializeReference]` property (Path, Emission,
  Payload) whose drawer already shows a named foldout is no longer wrapped in a second module of the same
  name.

## [0.2.1] — 2026-07-18

### Added

- **In-session example navigator.** Press Play in any sample and a slim **A2BKit Examples** bar appears
  along the bottom — Prev / Next and the current example, with a click to open the full list — to hop
  straight between scenes without leaving Play mode. It bootstraps itself (no wiring in the sample
  scenes) and only ever appears in the example scenes, so importing the samples never injects the menu
  into a consumer's own game. In the Editor it registers the example scenes in Build Settings on first
  switch (also **Tools ▸ A2BKit ▸ Samples ▸ Add Example Scenes to Build Settings**).

### Fixed

- **Samples: missing materials on the 3D and particle examples.** The generated coin mesh and the unlit
  and particle materials were created `HideFlags.DontSave`, so they were dropped from the scenes and
  effect assets that referenced them — the chest, collector and 3D/particle coins rendered as Unity's
  magenta error material (or not at all). They are now baked to real assets under
  `Art/Generated/`, so the references persist. (The 2D examples were unaffected — they reference real
  PNG sprites.)

## [0.2.0] — 2026-07-18

### Added

- **`A2BSplinePath` — a multi-point Bézier path.** The multi-point peer of `A2BBezierPath`: add any
  number of control points to sculpt an S-curve, a loop-round, a double-hump. It's a Bézier over
  [origin, control points…, destination] (De Casteljau), so it still lands exactly on both endpoints no
  matter how the middle is shaped. Control offsets are stored as **fractions of the endpoint distance**,
  so one authored curve reads the same in world space (metres) and on a Canvas (pixels) — the arc no
  longer collapses to an invisible few-pixel wobble in canvas space. Allocation-free on the tick path.
- **Scene handles for the spline path.** In the A2B Effect Editor, each control point gets a draggable
  handle; a **+** on every segment inserts a point there and a **−** beside each removes it (both
  Undo-able). Dragging decomposes back to (position-along, offset) exactly.
- **Arc-lift scaling (`ArcLiftScale`).** Items grow in proportion to how far they bulge off the straight
  line, so an arc reads as depth — a coin popping up toward the camera and settling as it lands.
  Especially useful on a flat Canvas, where there's no real perspective; works in any space; off (0) by
  default.
- **Burst-area gizmo.** The A2B Effect Editor now draws the burst footprint at the origin — the spawn
  **scatter** radius (where items are born) and, for a burst-gather path, the **burst** radius (how far
  they spray before turning for the target). Each is an editable radius handle: drag the ring to resize
  (Undo-able), so you can size the spread visually instead of guessing pixel values.
- **Burst release easing (`A2BBurstEmission.ReleaseEasing`).** Shapes *when* items release across
  `SpreadOverDuration` instead of an even trickle — ease-out front-loads the spray, ease-in-out softens
  both ends. Any `IA2BEasing` (the 21 built-ins or an AnimationCurve); null keeps the linear spread.
- **Scale from the motion path's depth (`ScaleFromPathDepth`).** Beside "scale over the duration"
  (`ScaleOverProgress`, an animation curve), you can now drive scale from the path's **Z**: the depth the
  path pushes an item to, as a fraction of the endpoint distance, multiplies its scale. On a 2D/Canvas
  effect Z moves nothing visible, so the curve's Z channel is free to mean "how big" — sculpt scale with
  the same spline handles by raising a control point's Z. The item scales rather than drifting in depth,
  and it composes with the duration curve and arc-lift. Strength via `PathDepthScaleStrength`.

- **A2B Effect Editor window** (`Tools ▸ A2BKit ▸ A2B Effect Editor`). A visual, in-scene editor for a
  single effect asset, without entering play mode:
  - **Preview and scrub the timeline.** Play, pause, restart, loop, and vary speed — or drag the Time
    slider to any point in the effect and hold there. Both playback and scrubbing run the *real*
    scheduler on the injected editor clock (the same seam the tests drive), so what you see is the
    frame the game would show. Scrubbing re-simulates from zero to the chosen time, because the
    scheduler is forward-only. The scrub bar spans the true end-to-end length — stagger and duration
    jitter included — not just `Duration`.
  - **Real payload visuals in the Scene AND Game view — no play mode.** "Show payload visuals" (on by
    default) draws the actual sprite / image / mesh / text flying, in both views, by running the
    shipping presenter (space adapter + pooled renderer + feedbacks) on a self-owned `DontSave` stage
    that is torn down whole on every exit. Turn it off to fall back to lightweight motion dots (the
    Scene-only overlay). An effect with no payload falls back to dots automatically. Specifically:
    - **Canvas effects preview on a real screen overlay**, with endpoints resolved the way the runtime
      does — a RectTransform target reports its true screen position (via its canvas scaler) instead of
      being re-projected as a world point, which is what previously threw canvas items thousands of
      pixels off-screen. Virtual points on a canvas effect are treated as screen positions.
    - **The Game view refreshes during preview.** It does not re-render on its own in edit mode, so the
      preview now requests it each frame; without this the items were placed correctly but the Game view
      showed a stale frame.
    - **Particle payloads actually emit in the preview.** Particle systems do not simulate in edit mode,
      so the preview steps each active system with the clock — a burst is visible without play mode.
  - **Path and live items drawn in the Scene view** with no `A2BEffectPlayer` present, reusing the
    gizmo's own sampler and palette so the two never diverge.
  - **Drag the arc in the Scene.** A Bezier path gets a control-point handle: drag it and `ArcHeight`,
    `ArcDirection` and `ArcBias` update together while the arc bulges to follow (Undo-able). Shapes
    whose form is per-item and seed-driven (procedural spiral, burst spray) are left to the inspector
    rather than given a handle that would misrepresent where the curve goes.
  - **Endpoints as scene objects or virtual points.** Pick a Transform (or "Use Selection"), or switch
    an endpoint to a virtual point and drag it with a Scene handle — so an effect can be tuned before
    the objects it fires between exist. Endpoints resolve live, so moving either updates the preview.
  - **Embedded definition editing** — the full effect inspector, inline; edits show immediately, and a
    held scrub frame re-simulates so a tweak is visible without touching Play.
- **`A2BEffectDefinition.ResolveSpan(seed)`** (Core) — the effect's total end-to-end length for a seed,
  which the editor timeline maps its scrub bar onto. Guarded by `A2BEffectSpanTests`.
- **Scrub / pause / speed on `A2BEffectPreview`** (`Begin`, `Scrub`, `SetPaused`, `Speed`, `Span`,
  `Paused`) — the preview engine now supports holding and seeking a frame, not only free-running.

## [0.1.2] — 2026-07-17

### Added

- **Multiple Effects sample (scene 9).** One reward fires two effects at once, to different places —
  coins to the wallet HUD and xp orbs to the level bar. They run concurrently and never interfere.
- **`A2BDemoMultiPlay`** (samples) — fires several `A2BEffectPlayer`s together.
- **`A2BConcurrentEffectsTests`** — permanent guards that playing a new effect while one is running
  never reuses or disturbs the running one's pooled items, even under rapid overlapping replays.

### Fixed

- **Floating Score Text: rapid clicks no longer jostle popups already in flight.** The float
  destination was parented to the star, which is scale-punched on each click; since endpoints resolve
  every frame, punching moved the destination and yanked in-flight popups. The destination now hangs
  off the canvas, independent of the punch. (This was a sample-scene wiring issue — the effect pool
  itself was never at fault, which the new concurrency tests confirm.)

### Changed

- **Floating Score Text sample is now interactive.** Click the star and a "+N" pops off it, floats up
  and fades — each click plays a fresh popup, tallied into a running score. Previously it auto-played
  on a timer.

### Added

- **`A2BDemoClickToPlay`** (samples) — fires an `A2BEffectPlayer` when you click a target rect, or from
  a `Button`'s `onClick`. Detects the click with no `EventSystem` (it hit-tests the pointer against the
  rect and reads whichever input backend is active), so it never logs an input-backend mismatch on
  scene open.

## [0.1.0] — 2026-07-17

First release.

### Added

- **The A2B model.** One `A2BEffectAsset` (or a fluent `A2BEffectBuilder`) drives every combination of
  space, payload and path. Both authoring surfaces are peers: `A2B.Play(spec, from, to)` takes either.
- **Spaces.** `World3D`, `World2D`, and `Canvas` across all three render modes — plus cross-space, so a
  world chest can throw coins onto a screen-space wallet with no camera math at the call site.
- **Payloads.** Sprite, Image (uGUI), Mesh, Text (TextMeshPro), Particle, and Prefab.
- **Paths.** `Linear`; `Bezier` with a designer-facing arc rather than raw control points;
  `BurstGather` — the two-beat reward, where items explode outward, hang, then get pulled in;
  `Procedural` (spiral/wave); and any `IA2BPath` you write.
- **Easing.** 21 built-in kinds plus `AnimationCurve`, composable with any path.
- **Emission.** Count range, all-at-once / fixed stagger / spread-over-duration, and scatter.
- **Feedback.** Trails, on-hit impacts, spawn flashes, and pooled audio with pitch that rises per item.
  Stackable, and extensible via `IA2BFeedback`.
- **Events.** `Started`, `ItemSpawned`, `FirstItemArrived`, `ItemArrived`, `Completed`, `Cancelled`.
  `FirstItemArrived` is first-class because it is the hook the mechanic exists for — it starts the
  counter roll-up when the first coin actually lands.
- **Async.** `await A2B.PlayAsync(...)` via UniTask, with cancellation.
- **Live endpoints.** Origin and destination resolve every frame, so a moving wallet is the default
  case rather than an edge case. A destroyed endpoint cancels cleanly instead of throwing.
- **Editor tooling.** An **A2BKit window** (`Tools ▸ A2BKit ▸ A2BKit Window`) with live runtime
  diagnostics (active effects, items in flight, pool occupancy), the common tools, and a one-click
  installer for the AI skill. Plus context-aware inspectors, a Scene-view gizmo that draws the *real*
  path (it calls the same `Evaluate` the runtime does), an in-editor preview driven by the same
  injected clock the tests use, and a runtime debug overlay.
- **AI skill.** A `SKILL.md` teaching the API, patterns, performance rules and gotchas, installable
  into `.claude/skills/a2bkit/` from the window (or `Tools ▸ A2BKit ▸ Install AI Skill`) so an AI
  assistant working in the project uses the real API rather than guessing.
- **Extensibility.** Paths, easings, emissions, payloads and feedbacks are `[SerializeReference]` fields
  — the inspector's picker finds your implementation automatically, no registration. Space adapters
  install through `A2BAdapters` (per-asset or global), because `Space` is an enum and cannot be
  extended from outside.
- **Samples.** Eight scenes covering the canonical cases.

### Performance

- **Zero per-frame allocation** during steady-state playback, enforced by an automated test rather than
  claimed in a README. Per-play allocation is bounded and constant with respect to item count.
- Everything is pooled: item visuals, effect state, and internal buffers. Nothing self-updates — one
  scheduler ticks every effect, so 200 items cost one callback rather than 200.
- Canvas items are parented to a dedicated A2BKit canvas by default, so moving items don't force a
  batch rebuild of the host HUD every frame. Overridable if you've profiled the other way.

### Notes

- **UniTask is required** and must be installed separately — a package's `dependencies` can only name
  registry packages, and UniTask ships as a Git package. See the README.
- Built pipeline-agnostic where free; validated on URP.
