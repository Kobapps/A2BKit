---
title: A2BKit
status: final
created: 2026-07-17
updated: 2026-07-17
---

# PRD: A2BKit

## 0. Document Purpose

This PRD is for the A2BKit implementer and for the downstream `bmad-architecture` and `bmad-create-epics-and-stories` workflows. It builds on `../../briefs/brief-A2BKit-2026-07-17/brief.md` (vision, scope boundaries) and its `addendum.md` (verified Unity 6 technical constraints, competitive landscape, coordinate-conversion gotchas) — it does not duplicate them. Vocabulary is Glossary-anchored (§3); features are grouped with globally-numbered FRs nested; inferences are tagged `[ASSUMPTION]` inline and indexed in **§11**. This was produced headless from a goal statement, so **§11 is the review surface that matters most**.

## 1. Vision

A2BKit is a Unity 6 package for **A-to-B effects**: the burst of coins that flies from a chest into the wallet HUD, the score text that floats off a kill, the XP orbs that stream into a level bar. Nearly every f2p game ships this moment; most teams rebuild it per project as tween sequences, ad-hoc pools, and camera-space math that breaks the moment the target starts moving.

The mechanic looks varied but is uniform underneath: *spawn N items at A, move them to B along some path over some time, and raise events when things happen.* A2BKit makes that shape the product. One **Effect Definition** and one runtime cover every combination of **Space**, **Payload**, and **Path**, with variation isolated behind small strategy interfaces rather than smeared across per-effect scripts.

The hard parts are not the motion — they are allocation, endpoint tracking, and event timing. A2BKit's bet is that solving those three structurally, and enforcing the first with an automated test, produces something teams adopt once and stop thinking about.

## 2. Target User

### 2.1 Jobs To Be Done

- **Functional:** Ship a coin-burst-to-HUD that lands on a moving target and costs near-zero frame time — today, without writing motion math.
- **Functional:** Know exactly when the first item lands, so the currency counter starts rolling in sync with the visual.
- **Functional:** Tune feel (arc, spread, stagger, easing) without opening a script.
- **Emotional:** Stop re-earning the same six bugs on every project.
- **Contextual:** Hold 60 fps on a mid-tier Android phone during the most-repeated moment in the game.
- **Social:** Hand the package to a teammate who can configure it without reading the source.

### 2.2 Non-Users (v1)

- Teams needing GPU-scale ambient VFX (thousands of non-interactive particles) — that is VFX Graph's job, not this.
- Teams wanting a general-purpose tweening library. A2BKit animates A-to-B transfers; it is not a DOTween replacement.
- Teams on Built-in RP or HDRP who need a *validated* pipeline. The core avoids pipeline-specific APIs, but only URP is validated. `[ASSUMPTION]`

### 2.3 Key User Journeys

Library product, single operator role — UJs run at the **lighter** scope dial (one sentence each).

- **UJ-1. Dana wires the coin burst.** Dana, a gameplay dev two days from a milestone build, drags an `A2BEffectAsset` onto her chest prefab, points its target at the wallet HUD `RectTransform`, and calls `Play()` on open — coins fly, the counter starts when the first one lands, and she never computes a screen-space coordinate.
- **UJ-2. Ravi tunes the feel.** Ravi, a technical artist with no interest in C#, opens the effect asset, drags the arc height slider, and watches the path redraw live in the Scene view against the real endpoints.
- **UJ-3. Mei awaits the reward.** Mei, writing a reward-sequence coroutine, does `await effect.PlayAsync(ct)` so the "level up" panel opens exactly when the last orb lands, and cancels cleanly when the player quits mid-flight.
- **UJ-4. Sam debugs the leak.** Sam, six months later, sees 200 coins stuck on screen, opens the debug overlay, and reads active-effect count and pool occupancy without attaching a profiler.
- **UJ-5. Priya proves it's free.** Priya, the perf owner, runs the allocation test in CI and gets a hard failure the moment someone's change starts allocating during playback.

## 3. Glossary

Downstream workflows must use these terms verbatim. Introducing a synonym anywhere is a discipline violation.

- **A2B Effect** — One playable instance of the mechanic: N Items travelling from an Origin to a Destination. The unit that is played, awaited, and cancelled.
- **Effect Definition** — The authored, reusable configuration of an A2B Effect (counts, timing, Space, Payload, Path, easing). Exists as an `A2BEffectAsset` (ScriptableObject) or an equivalent runtime-built config struct. One Definition → many Effects.
- **Item** — A single travelling element within an Effect (one coin, one orb, one text label). Cardinality: 1 Effect → N Items.
- **Origin** — Where Items spawn. Resolved via an Endpoint Provider. Also written "A".
- **Destination** — Where Items travel to. Resolved via an Endpoint Provider. Also written "B".
- **Endpoint Provider** — The strategy that resolves an Origin or Destination position *per evaluation*, not once at Play. Kinds: static point, Transform-following, RectTransform-following, custom.
- **Space** — The coordinate domain an Effect plays in. Exactly three: `World3D`, `World2D`, `Canvas`. `[ASSUMPTION: the source goal listed "2d, 3d, world, canvas" as four peers, conflating two axes; this three-Space taxonomy is the drafting resolution and the entire Space Adapter design hangs off it. Reversible only cheaply if caught now.]`
- **Canvas Render Mode** — A property of the `Canvas` Space, not a Space: Overlay, Screen-Space-Camera, or World-Space canvas. "World-space canvas" is therefore a Render Mode, not a fourth Space.
- **Space Adapter** — The strategy that converts between world/screen/canvas coordinates for a given Space (+ Render Mode). Where the coordinate-conversion gotchas are encapsulated.
- **Payload** — What an Item *is*, visually: `Sprite`, `Mesh`, `Particle`, or `Text`.
- **Path** — The trajectory function taking normalized progress `t ∈ [0,1]` plus Origin/Destination to a position. Kinds: `Linear`, `Bezier`, `Procedural`, `Custom`.
- **Easing** — The reparameterization of `t` over time. Orthogonal to Path.
- **Emission** — How the N Items are released over time (all at once, staggered, over a duration) and their initial spatial scatter.
- **Effect Handle** — The caller's reference to a running A2B Effect: exposes events, completion awaiting, and cancellation. Struct-based to avoid allocation.
- **Steady-State Playback** — Playback after warm-up, with pools pre-filled and no new Effect Definitions being loaded. The window in which the allocation budget applies.
- **Per-Frame Allocation** — Managed heap allocated while an Effect advances, measured per frame excluding the frame on which `Play`/`PlayAsync` is called. **This is what the zero-allocation budget governs** (NFR-1). Distinguished from Per-Play Allocation because conflating the two makes the budget unsatisfiable.
- **Per-Play Allocation** — Bounded, constant managed heap allocated once at `Play`/`PlayAsync` — chiefly the UniTask awaiter machinery on the async path. Budgeted and asserted as *constant with respect to Item count and frame count*, not as zero.
- **Arrival** — The moment an Item's normalized progress reaches `t >= 1`. Triggers `ItemArrived`; the first Arrival in an Effect also triggers `FirstItemArrived`. Defined by progress, **not** by distance-to-Destination: because every Path is pinned to the Destination at `t=1` (FR-9), a distance test is redundant, and with a moving Destination the two definitions fire on different frames for different Items — making `FirstItemArrived` untestable. See architecture AD-13.
- **Time Source** — The injected abstraction supplying delta time. Enables deterministic testing and scaled/unscaled time.

## 4. Features

### 4.1 Effect Authoring

**Description:** Authors configure an A2B Effect two ways, as peers rather than one wrapping the other: a data-driven `A2BEffectAsset` for designers and a code-first fluent API for programmers. Both produce the same Effect Definition, so an asset can be played directly or taken as a base and overridden in code. Realizes UJ-1, UJ-2. `[ASSUMPTION: both surfaces ship in v1 as peers — the source goal implied designer-configurability and code control without ranking them.]`

**Functional Requirements:**

#### FR-1: Author an Effect Definition as an asset

A designer can create an `A2BEffectAsset` via the Unity Create menu and configure Space, Payload, Path, Emission, Easing, counts, and durations without writing code. Realizes UJ-2.

**Consequences (testable):**
- The asset appears under `Assets > Create > A2BKit > Effect`.
- A default-constructed asset is playable with no further configuration (sane defaults for every field).
- Configuring the asset requires no code changes and no scene references baked into the asset.

#### FR-2: Author an Effect Definition in code

A programmer can build an Effect Definition fluently and play it in one statement, without an asset. Realizes UJ-1, UJ-3.

**Consequences (testable):**
- A definition can be constructed, played, and awaited in a single chained expression.
- Building a definition in code allocates only at build time, never per-Play in Steady-State Playback.
- Any asset-authored definition can be cloned and overridden in code without mutating the source asset.

#### FR-3: Play an Effect from a Definition

A caller can play an Effect from a Definition with an Origin and Destination, receiving an Effect Handle.

**Consequences (testable):**
- `Play` returns an Effect Handle synchronously; Items begin emitting per the Emission config.
- Playing the same Definition concurrently N times yields N independent Effects that do not share mutable state.
- Playing an invalid Definition (unset Destination, missing Payload prefab) **logs an actionable error at Play naming the offending object and returns an invalid Effect Handle — it does not throw** (NFR-3) and does not fail silently at Arrival. "Loudly" means a console error at the point of the mistake, not an exception in the caller's gameplay code.
- An invalid Effect Handle reports `IsValid == false`, raises no events, and is safe to await (completes immediately as cancelled).

### 4.2 Spaces and Coordinate Adaptation

**Description:** An Effect declares its Space; a Space Adapter converts coordinates so callers never touch `WorldToScreenPoint` or `ScreenPointToLocalPointInRectangle`. This feature exists to make the four documented conversion bugs (addendum §3) unreachable from user code. Realizes UJ-1.

**Functional Requirements:**

#### FR-4: Play in every supported Space

A caller can play an Effect in `World3D`, `World2D`, or `Canvas` Space, and in `Canvas` Space under all three Canvas Render Modes.

**Consequences (testable):**
- Each of the three Spaces has an example scene and automated coverage.
- `Canvas` Space works under Overlay, Screen-Space-Camera, and World-Space canvas without caller-side branching.
- The correct camera argument is chosen per Render Mode internally — `null` for Overlay, `canvas.worldCamera` for Screen-Space-Camera — and this is asserted by test.

#### FR-5: Cross-Space endpoints

A caller can play an Effect whose Origin is in one Space and whose Destination is in another — the canonical case being a world-space chest (Origin) and a Canvas wallet HUD (Destination).

**Consequences (testable):**
- A world-space Origin with a Canvas Destination places Items on the Destination's `RectTransform` within tolerance on Arrival.
- Behind-camera Origins are handled explicitly: an Item whose world Origin is behind the camera does not spawn at a spurious on-screen position. `[ASSUMPTION: the correct behavior is to clamp/suppress rather than throw — least-surprise for a cosmetic system.]`

#### FR-6: Space Adapters are extensible

A developer can register a custom Space Adapter without modifying any shipped class.

**Consequences (testable):**
- A custom adapter can be supplied and used by an Effect with no edits to A2BKit source.

#### FR-28: Canvas rebuild containment

In `Canvas` Space, Items are parented to a dedicated Canvas by default so that moving them does not dirty the host game's UI Canvas. Any drawable change re-runs batch building for **every** element on that Canvas — with 200 moving Items on the main HUD Canvas, the rebuild cost, not the motion, is the frame spike. This is the #2 recurring failure in the addendum's survey and has no other owning FR.

**Consequences (testable):**
- Default behavior parents spawned Items to a dedicated A2BKit Canvas, not to the Destination's Canvas.
- The target Canvas is overridable — batches do not merge across Canvases, so the default trades a draw call for rebuild isolation, and a team that has profiled the other way must be able to opt out. `[ASSUMPTION: rebuild isolation is the right default; it is the failure teams actually hit.]`
- Sort order relative to the host UI is configurable, so Items can be made to fly above or below HUD elements.

**Feature-specific NFRs:**
- Mixed `World3D`/`World2D`/`Canvas` Effects on screen simultaneously must have a defined, configurable draw order rather than an incidental one. `[ASSUMPTION: per-Effect sorting config is sufficient; a global sorting authority is over-engineering for v1.]`

### 4.3 Payloads

**Description:** A Payload determines what an Item looks like. The four kinds share one lifecycle (acquire → position each frame → release) behind a common interface, so Path and Space code never branches on Payload. Realizes UJ-1.

**Functional Requirements:**

#### FR-7: Sprite, Mesh, Text, and Particle payloads

A caller can configure an Effect with a `Sprite`, `Mesh`, `Text`, or `Particle` Payload.

**Consequences (testable):**
- Each Payload kind has an example scene and automated coverage.
- `Text` Payload renders via TextMeshPro with no package dependency beyond `com.unity.ugui` (already present; namespace `TMPro`).
- `Text` Payload content is settable per-Item at Play (e.g. `"+250"`) without allocating a new string per frame.
- `Particle` Payload raises Arrival events from CPU-side simulation (this is why VFX Graph is excluded).
- Payload choice does not change the Path, Space, or event code paths.

#### FR-8: Payloads are extensible

A developer can implement a custom Payload without modifying any shipped class.

**Consequences (testable):**
- A custom Payload can be registered and used with no edits to A2BKit source.

### 4.4 Paths and Motion

**Description:** A Path maps normalized progress to a position given Origin and Destination; Easing reparameterizes progress over time. The two are orthogonal — any Easing composes with any Path. Paths are pure functions, which is what makes them testable without a scene. Realizes UJ-2.

**Functional Requirements:**

#### FR-9: Linear, Bezier, Procedural, and Custom paths

A caller can configure an Effect with a `Linear`, `Bezier`, `Procedural`, or `Custom` Path.

**Consequences (testable):**
- Every Path evaluates to Origin at `t=0` and Destination at `t=1`, within floating-point tolerance. This holds for every Path kind including Custom, and is the invariant that makes Arrival meaningful.
- `Bezier` supports a configurable arc, expressed in a way a designer can reason about (e.g. arc height and direction) rather than raw control points. `[ASSUMPTION]`
- `Procedural` supports parameterized motion (e.g. spiral, wave, scatter-then-gather) configured without code.
- Path evaluation is a pure function of `(t, Origin, Destination, params)` — no frame state, no side effects.
- Path evaluation allocates zero bytes.

#### FR-10: Paths are extensible

A developer can add a Path kind without modifying any existing class — the open/closed check named in the brief's success criteria.

**Consequences (testable):**
- A custom Path is implemented, registered, and played in a test with zero edits to shipped files.

#### FR-11: Easing composes with any Path

A caller can apply an Easing to any Path, including a custom one.

**Consequences (testable):**
- A standard easing library is provided; a custom easing curve can be supplied.
- Easing is applied to `t` before Path evaluation and never inside a Path implementation.
- Easing allocates zero bytes per evaluation.

#### FR-26: Emission — count, stagger, and scatter

A designer can configure how the N Items are released over time and how they are initially scattered in space. This is the "spread and stagger" half of the tuning JTBD (§2.1); arc is FR-9 and easing is FR-11.

**Consequences (testable):**
- Item count is configurable, including a range for per-Play variation.
- Release timing is configurable: all-at-once, fixed stagger interval, or spread evenly across a duration.
- Initial scatter is configurable (e.g. radius/shape around the Origin) so a burst does not spawn N Items at one point.
- Per-Item start delay derives from Emission, and `Started` fires once for the Effect regardless of stagger (FR-14 ordering holds).
- Emission produces per-Item variation without allocating per Item — variation is computed, not collected into a list.
- With stagger configured, `FirstItemArrived` still fires on the genuine first Arrival, which may not be the first Item spawned (a scattered Item can overtake). This is the case most likely to be implemented wrong.

### 4.5 Endpoints

**Description:** Origin and Destination resolve through Endpoint Providers queried as the Effect runs, so moving targets are the default rather than the edge case. This is one of the three structural problems the package exists to solve. Realizes UJ-1.

**Functional Requirements:**

#### FR-12: Live endpoint tracking

A caller can bind a Destination to a moving `Transform` or `RectTransform` and have in-flight Items track it.

**Consequences (testable):**
- Moving the Destination mid-flight causes in-flight Items to arrive at the Destination's *current* position, not its position at Play.
- Endpoint resolution allocates zero bytes per evaluation.
- Endpoint providers are the only place positions are read; no position is cached across frames unless the provider declares itself static.

#### FR-13: Endpoint destruction is survivable

An Effect whose Origin or Destination is destroyed mid-flight terminates predictably rather than throwing.

**Consequences (testable):**
- Destroying the Destination mid-flight completes or cancels the Effect (per configured policy) and raises the corresponding event, without a `MissingReferenceException`. `[ASSUMPTION: default policy is cancel-and-release; a cosmetic system must never throw into gameplay code.]`
- Pooled Items are returned to the pool in this path — no leak.

### 4.6 Lifecycle, Events, and Async

**Description:** The event set is the package's API for staying in sync with gameplay. `FirstItemArrived` is first-class because it is the hook the mechanic exists to serve — it starts the counter roll-up. Async is UniTask-based. Realizes UJ-1, UJ-3.

**Functional Requirements:**

#### FR-14: Full event set

A caller can subscribe to `Started`, `ItemSpawned`, `FirstItemArrived`, `ItemArrived`, `Completed`, and `Cancelled` on an Effect Handle.

**Consequences (testable):**
- `FirstItemArrived` fires exactly once per Effect, on the first Arrival, and always before `Completed`.
- `ItemArrived` fires once per Item; total invocations equal Item count when no Items are cancelled.
- Exactly one of `Completed` or `Cancelled` fires per Effect — never both, never neither.
- `Started` fires before any `ItemSpawned`.
- Event dispatch allocates zero bytes (no delegate boxing, no closure capture per dispatch, no `params` arrays).
- An exception thrown by a subscriber does not corrupt Effect state, leak pooled Items, or prevent remaining events.

#### FR-15: Await an Effect with UniTask

A caller can `await` an Effect's completion and cancel it via `CancellationToken`. Realizes UJ-3.

**Consequences (testable):**
- `await PlayAsync(ct)` resumes when the Effect completes.
- Cancelling the token mid-flight stops the Effect, raises `Cancelled`, and returns all Items to the pool.
- Cancellation via `destroyCancellationToken` on a destroyed host does not throw an unhandled exception.
- Awaiting incurs bounded **Per-Play Allocation** only (the UniTask awaiter machinery) and **zero Per-Frame Allocation** — consistent with NFR-1, which governs per-frame cost. The awaiter cost does not scale with Item count.
- The async path and the event path are consistent: awaiting does not suppress events and vice versa.

#### FR-16: Deterministic time

An Effect advances via an injected Time Source rather than reading `Time.deltaTime` directly. Realizes UJ-5.

**Consequences (testable):**
- A test can advance an Effect by fixed steps and assert exact positions without entering play mode.
- Scaled vs. unscaled time is configurable per Effect (a reward flying during a paused menu is the motivating case).
- No production code path reads `Time.deltaTime` outside the Time Source implementation — enforceable by test or review.

#### FR-27: Effect Handle validity contract

An Effect Handle is a struct that refers to pooled, recycled Effect state. It must therefore define what happens when it is used after the Effect it referred to has ended — otherwise a stale copy silently addresses a *different* Effect that has since reused the slot. This is the classic pooled-handle defect and the reason this FR exists.

**Consequences (testable):**
- A Handle exposes `IsValid`; it reports `false` once the Effect has completed, been cancelled, or been recycled.
- Every Handle operation (subscribe, cancel, await, query) on an invalid Handle is a safe no-op — no exception (NFR-3), no effect on the Effect that reused the slot.
- A Handle retained across an Effect's completion and a subsequent `Play` that reuses the same pooled slot **does not** address the new Effect. Verified by test: this is the aliasing bug the generation/version stamp exists to prevent.
- Copying a Handle by value does not duplicate ownership or double-cancel.

### 4.7 Pooling and Caching

**Description:** Everything reusable is pooled: Item GameObjects per Payload kind, Effect state objects, and internal buffers. Pools are per-Definition-and-Payload so a coin pool never hands out a text label. Realizes UJ-4, UJ-5.

**Functional Requirements:**

#### FR-17: Pool all Items

Items are acquired from and released to a pool across their lifecycle.

**Consequences (testable):**
- Playing the same Effect twice in Steady-State Playback instantiates zero new GameObjects.
- Every terminal path — Completed, Cancelled, destroyed endpoint, subscriber exception — returns Items to the pool. Verified per path, not just the happy one.
- Pool capacity is configurable and pre-warmable.
- Double-release is detected in the Editor (Unity's `collectionCheck`) and disabled in player builds for performance.

#### FR-18: Zero per-frame allocation in steady state

Per-Frame Allocation during Steady-State Playback is zero bytes. This is the package's headline constraint and is enforced, not documented. Realizes UJ-5.

**Consequences (testable):**
- An automated test plays an Effect for N frames after warm-up and asserts **0 B Per-Frame Allocation**; it fails the build otherwise.
- The test measures frames *after* the Play frame, so bounded Per-Play Allocation does not mask or break the per-frame assertion.
- Per-Play Allocation is separately asserted **constant with respect to Item count** — playing a 10-Item and a 200-Item Effect allocates the same bytes at Play. This is the assertion that catches per-Item allocation hiding inside setup.
- The test covers every Space × Payload × Path combination the package claims. `[ASSUMPTION: full matrix is affordable; if it proves slow in CI, a representative subset per axis with the full matrix nightly is the fallback.]`
- Warm-up allocation (pool fill, definition load, first-time caches) is explicitly outside the budget and is measured separately rather than hidden.

### 4.8 Editor Tooling

**Description:** The configuration surface is the product for the designer persona. Inspectors show only the fields relevant to the chosen Space/Payload/Path; gizmos draw the real path against the real endpoints. Realizes UJ-2.

**Functional Requirements:**

#### FR-19: Context-aware inspector

A designer sees only the fields relevant to the current Space, Payload, and Path selection.

**Consequences (testable):**
- Selecting `Linear` hides Bezier arc fields; selecting `Text` hides sprite fields.
- Invalid configurations surface an inline, actionable message in the inspector — not a runtime exception.

#### FR-20: Scene gizmos draw the real path

A designer sees the actual computed Path drawn in the Scene view between the actual endpoints.

**Consequences (testable):**
- The gizmo path matches the runtime path — both call the same Path evaluation code, so a divergence is impossible by construction.
- Origin and Destination are drawn and distinguishable.
- Gizmos update live as parameters change.

#### FR-21: In-editor preview

A designer can preview an Effect without entering play mode. Realizes UJ-2.

**Consequences (testable):**
- Preview animates using the injected Time Source (the same seam the tests use).
- Preview cleans up fully — no leaked preview objects on stop, scene change, or domain reload.

### 4.9 Diagnostics

**Description:** Answers "what is running and where did my pool go" without a profiler session. Realizes UJ-4.

**Functional Requirements:**

#### FR-22: Runtime debug overlay

A developer can enable an overlay reporting active Effects, in-flight Item counts, and pool occupancy (active / available / capacity) per pool.

**Consequences (testable):**
- The overlay is toggleable at runtime and compiled out (or inert and non-allocating) in release builds.
- The overlay itself does not allocate per frame while displayed. `[ASSUMPTION: this is achievable and worth the effort; a diagnostic that violates the package's own headline constraint would be embarrassing.]`

#### FR-23: Actionable diagnostics

Misconfiguration produces an actionable message naming the offending asset or GameObject.

**Consequences (testable):**
- Null Destination, missing Payload prefab, and pool exhaustion each produce a distinct message identifying the source object.
- Pool exhaustion degrades gracefully (grow or drop per policy) rather than throwing. `[ASSUMPTION: default is grow-with-warning — dropping a reward the player earned is worse than a frame spike.]`

### 4.10 Examples and Tests

**Description:** Every claim in this PRD is backed by a runnable scene and an automated test. The examples are the documentation.

**Functional Requirements:**

#### FR-24: Example scene per canonical case

Every **canonical use case** has a runnable example scene, and every Space, Payload, and Path kind appears in at least one scene.

**Consequences (testable):**
- The canonical set is: coin→wallet (Canvas), **coin burst→wallet** (the two-beat reward), floating score text, XP orbs to a bar, 3D mesh collect, particle burst, moving-target, and cross-Space. **Eight scenes, not the full 48-cell matrix** — the matrix is covered by tests (FR-25/FR-18), which is cheaper and more reliable than 48 hand-built scenes.
- The set is defined by *distinct mechanics a team would actually ship*, not by cells of the matrix. Coin→wallet and coin-burst→wallet share a space and a payload and are still both canonical, because a burst-then-gather is a different beat from an arc and cannot be reached by tuning one — the addition is justified by that test, not by novelty.
- Coverage check: each of the 3 Spaces, 4 Payloads, and 4 Path kinds is exercised by at least one scene. Any kind not reachable from a scene is a gap.
- Each scene runs standalone with no external setup.
- Art is either sourced with a compatible license or authored in Blender; provenance is recorded.

#### FR-25: Automated test coverage

EditMode tests cover pure logic; PlayMode tests cover integration; the allocation guard covers the perf budget.

**Consequences (testable):**
- Path, Easing, and Space Adapter math is tested in EditMode with no scene.
- Async/cancellation is tested with **`[Test] public async Task`**, which Test Framework 1.7 supports natively. (`[UnityTest]` does not accept `async`, but the once-common `UniTask.ToCoroutine` bridge is an obsolete workaround and forfeits 1.7's async fixes. `[UnityTest] IEnumerator` remains correct for frame-stepping.)
- Allocation assertions use `UnityEngine.TestTools.Constraints` — `Is.Not.AllocatingGCMemory()` — with a mandatory `using Is = UnityEngine.TestTools.Constraints.Is;` alias, since NUnit's `Is` otherwise shadows it and the test passes while measuring nothing.
- Event ordering guarantees from FR-14 are asserted explicitly.
- Tests pass in a headless/batch-mode run.

## 5. Non-Goals (Explicit)

- **Not a tweening library.** A2BKit animates A-to-B transfers. It will not grow a general-purpose tween API.
- **Not an economy system.** A2BKit animates the reward; the game owns the number. No currency state, no save/load, no netcode.
- **No third-party dependency in the core.** No DOTween (its ~730 B/tween-start allocation is the problem being solved), no Coffee UIParticle. Optional adapters only.
- **No VFX Graph payloads.** GPU particles cannot raise CPU-side Arrival events or render into a Canvas — the two capabilities that define this package.
- **Not a general UI framework.** No layout, no widgets beyond the package's own tooling.
- **Not becoming a game-feel suite.** No screen shake, no haptics, no audio orchestration. Those compose from the outside via events.

## 6. MVP Scope

### 6.1 In Scope

- Spaces: `World3D`, `World2D`, `Canvas` (all three Render Modes), plus cross-Space endpoints.
- Payloads: `Sprite`, `Mesh`, `Particle`, `Text`.
- Paths: `Linear`, `Bezier`, `Procedural`, `Custom`; Easing orthogonal to all.
- Live Endpoint Providers with mid-flight tracking and safe destruction.
- Full event set including `FirstItemArrived`; UniTask async + cancellation.
- Injected Time Source; scaled/unscaled support.
- Pooling and caching throughout; zero steady-state allocation with an enforcing test.
- Editor tooling: context-aware inspectors, real-path gizmos, in-editor preview, runtime debug overlay.
- Example scene per advertised case; EditMode + PlayMode tests.
- Both authoring surfaces (asset + fluent API) as peers.

### 6.2 Out of Scope for MVP

- **Asset Store packaging, marketing, store art.** Internal/reusable first. `[NOTE FOR PM]` If Asset Store is actually near-term, API-stability and doc requirements change *now* — this is the single assumption most expensive to get wrong.
- **Coffee UIParticle adapter.** Deferred to v2. Optional-adapter architecture must not be foreclosed by v1 decisions. `[ASSUMPTION]`
- **Built-in RP / HDRP validation.** Core stays pipeline-agnostic where free; only URP is validated.
- **VFX Graph payload.** Deferred; see Non-Goals.
- **Multi-hop paths** (A→B→C waypoint chains). The Path abstraction should not preclude it. `[NOTE FOR PM]` Cheap-looking and frequently requested — worth revisiting if v1 lands early.
- **Runtime-authoring UI** (in-game effect editor). No demand identified.
- **Localization of Text Payload content.** Caller supplies the string.

## 7. Success Metrics

**Primary**
- **SM-1: Zero per-frame allocation.** Per-Frame Allocation during Steady-State Playback == 0 bytes, asserted by automated test across the claimed matrix; Per-Play Allocation constant w.r.t. Item count. Validates FR-18, FR-9, FR-11, FR-12, FR-14, FR-15.
- **SM-2: Perf budget holds.** 200 concurrent Items sustain 60 fps on a mid-tier Android device. `[ASSUMPTION: both the 200-Item ceiling and "mid-tier Android" are inferred; needs a named reference device before it is measurable.]` Validates FR-17, FR-18.
- **SM-3: Five-minute first effect.** A developer new to the package gets a coin→wallet effect running in under five minutes using only the example scene and inspector, without reading source. Validates FR-1, FR-3, FR-24.

**Secondary**
- **SM-4: Open/closed holds.** A new Path, Payload, and Space Adapter are each added in a test with zero edits to shipped files. Validates FR-6, FR-8, FR-10.
- **SM-5: Claim coverage.** Every advertised Space × Payload × Path cell has a passing test (FR-25), and every Space, Payload, and Path kind is reachable from at least one of the seven canonical example scenes (FR-24). Validates FR-24, FR-25.

**Counter-metrics (do not optimize)**
- **SM-C1: API surface size.** Do not grow the public API to win SM-4. Extensibility achieved by exposing everything is not extensibility. Counterbalances SM-4.
- **SM-C2: Configuration field count.** Do not grow inspector fields to win SM-3. A five-minute first effect requires good defaults, not more knobs. Counterbalances SM-3.
- **SM-C3: Micro-optimization damage.** Do not sacrifice readability or SOLID structure to win SM-1/SM-2 beyond the stated budget. The budget is zero steady-state allocation and 60 fps at 200 Items — not "as fast as physically possible." Counterbalances SM-1, SM-2.

## 8. Cross-Cutting NFRs

- **NFR-1 — Allocation:** Zero **Per-Frame Allocation** in Steady-State Playback. **Per-Play Allocation** is bounded, constant with respect to Item count, and budgeted rather than zero. Warm-up is exempt and measured separately. (The three-way split is deliberate: a literal "zero bytes, ever" reading is unsatisfiable on the UniTask path and would make FR-18's test unwritable.)
- **NFR-2 — Frame budget:** 200 concurrent Items at 60 fps on the reference device. `[ASSUMPTION: device unnamed.]`
- **NFR-3 — Cosmetic systems never throw:** No A2BKit failure mode propagates an exception into caller gameplay code. Misconfiguration logs actionably; runtime edge cases (destroyed endpoint, pool exhaustion) degrade per policy.
- **NFR-4 — Determinism:** Given a fixed Time Source sequence, Effect positions are reproducible frame-for-frame. Required for FR-16 and all EditMode motion tests.
- **NFR-5 — Domain-reload safety:** No static mutable state survives a domain reload incorrectly; pools and registries reinitialize cleanly. Editor preview leaks nothing.
- **NFR-6 — SOLID structure:** Variation lives behind strategy interfaces (Space Adapter, Payload, Path, Endpoint Provider, Time Source). Adding a variant modifies no existing class (SM-4 is the enforcement).

## 9. Developer-Product Requirements

### 9.1 Public API Surface

- The public surface is: the Effect Definition (asset + fluent builder), `Play`/`PlayAsync`, the Effect Handle and its events, and the five extension interfaces (Space Adapter, Payload, Path, Endpoint Provider, Time Source).
- Everything else is internal. `internal` is the default; `public` requires justification (SM-C1).
- Extension points are interfaces, not abstract base classes with protected state, so implementers are not coupled to shipped internals.

### 9.2 Versioning and Deprecation

- SemVer. v1 is `1.0.0`. `[ASSUMPTION]`
- Pre-1.0, breaking changes are permitted between minors. Post-1.0, breaking changes require a major and a deprecation cycle: one minor with `[Obsolete]` plus a documented migration path.
- The extension interfaces are the contract most costly to break; they get the most conservative treatment.

### 9.3 Performance Budgets

- Per-Frame Allocation in Steady State: **0 B** (NFR-1, hard gate).
- Per-Play Allocation: bounded and **constant w.r.t. Item count**; the async path's awaiter machinery is the only permitted contributor.
- Sustained: **200 Items @ 60 fps** on the reference device (NFR-2).
- Path/Easing/Endpoint evaluation: allocation-free by construction (FR-9, FR-11, FR-12).
- Event dispatch: allocation-free (FR-14).
- Warm-up: bounded and pre-warmable; measured, not budgeted.

### 9.4 Runtime Targets and Dependency Policy

- **Unity 6000.5+**, URP validated. `[ASSUMPTION: 6000.5 is the floor because it is the project's version; a lower floor may widen adoption and costs little.]`
- **Hard dependencies:** UniTask (already in the project manifest — the cost is already paid), `com.unity.ugui` 2.x (bundled with Unity 6; supplies TextMeshPro, namespace `TMPro`).
- **Explicitly rejected:** DOTween, PrimeTween, Coffee UIParticle in core.
- **Pooling substrate:** `UnityEngine.Pool.ObjectPool<T>` — Unity's recommended default; no bespoke pool without cause.
- New dependencies require justification against the adoption-tax rationale in the brief.

## 10. Open Questions

1. **What is the reference device?** SM-2 and NFR-2 are unmeasurable until "mid-tier Android" names a real phone. Blocks perf verification, not architecture.
2. **Is 200 concurrent Items the right ceiling?** Inferred. A wrong ceiling mis-sizes pool defaults.
3. **Is Asset Store publication genuinely out for v1?** If not, API stability and docs become v1 requirements. Highest-cost open question.
4. **Is Unity 6000.5 the right floor**, or should the package target an earlier LTS to widen adoption?
5. **Behind-camera policy** (FR-5): clamp, suppress, or project to screen edge? Assumed clamp/suppress.
6. **Pool exhaustion policy** (FR-23): grow-with-warning assumed. Confirm against a memory-constrained target.
7. **Does the full Space × Payload × Path allocation matrix fit CI time budget** (FR-18)? Fallback is representative subset + nightly full.
8. **Multi-hop paths** — confirm deferral; the Path abstraction should stay compatible.

## 11. Assumptions Index

Every inference in this document, for explicit confirmation. This PRD was produced headless; this section is where review should start.

- **§3 / Glossary — The three-Space taxonomy (`World3D`/`World2D`/`Canvas`).** The source goal listed "2d, 3d, world, canvas" as four peers. The whole Space Adapter design hangs off this resolution; it is the cheapest assumption to fix now and the most expensive later.
- §4.1 / FR-1, FR-2 — Both authoring surfaces (asset + fluent API) ship in v1 as peers.
- §4.2 / FR-5 — Behind-camera Origins clamp or suppress rather than throw.
- §4.2 / FR-28 — Canvas rebuild isolation (dedicated Canvas) is the right default; overridable for teams who profiled otherwise.
- §4.2 / FR-28 — Per-Effect sorting config suffices; a global sorting authority is over-engineering for v1.
- §4.4 / FR-9 — Bezier arc is exposed as designer-friendly parameters (arc height/direction) rather than raw control points.
- §4.5 / FR-13 — Destroyed-endpoint default policy is cancel-and-release.
- §4.7 / FR-18 — The full allocation matrix is affordable in CI; subset + nightly is the fallback.
- §4.9 / FR-22 — A non-allocating debug overlay is achievable and worth the effort.
- §4.9 / FR-23 — Pool exhaustion defaults to grow-with-warning.
- §2.2 — Only URP is validated; Built-in RP / HDRP users are non-users in v1.
- §6.2 — Asset Store is out of scope for v1. **Most expensive assumption in the document.**
- §6.2 — Coffee UIParticle adapter deferred to v2.
- §7 / SM-2, §8 / NFR-2 — 200 concurrent Items @ 60 fps on an unnamed mid-tier Android device.
- §9.2 — SemVer with v1 at `1.0.0`.
- §9.4 — Unity 6000.5 is the floor, inherited from the host project's version rather than chosen.
