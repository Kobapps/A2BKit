# Changelog

All notable changes to A2BKit are documented here.

## [0.1.1] — 2026-07-17

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
