---
stepsCompleted: [1, 2, 3, 4]
inputDocuments:
  - _bmad-output/planning-artifacts/prds/prd-A2BKit-2026-07-17/prd.md
  - _bmad-output/planning-artifacts/architecture/architecture-A2BKit-2026-07-17/ARCHITECTURE-SPINE.md
  - _bmad-output/planning-artifacts/briefs/brief-A2BKit-2026-07-17/brief.md
  - _bmad-output/planning-artifacts/briefs/brief-A2BKit-2026-07-17/addendum.md
title: A2BKit - Epic Breakdown
status: final
epics: 11
stories: 56
created: 2026-07-17
updated: 2026-07-17
generated: headless
---

# A2BKit - Epic Breakdown

## Overview

This document provides the complete epic and story breakdown for A2BKit, decomposing the requirements from the PRD (FR-1..FR-28, NFR-1..NFR-6) and the Architecture Spine (AD-1..AD-17) into implementable stories.

**A2BKit is a library/package, not an application.** Stories are therefore *implementation slices*, not end-user features. The "user" in each story is the persona who consumes that slice: the **package consumer** (game developer), the **designer** (technical artist configuring assets), or the **package maintainer** (the agent implementing and verifying A2BKit itself). Every story cites the FR(s) it satisfies and the AD(s) that bind it.

**Generated headless.** Inferences are tagged `[ASSUMPTION]` inline and indexed in the *Open Questions and Assumptions* section at the end. That section is the review surface that matters most.

**No UX Design Requirements section is present.** A2BKit ships no end-user UI; its only human-facing surface is Unity Editor tooling, whose requirements are carried by FR-19, FR-20, FR-21, and FR-22. No `bmad-ux` design contract exists in `planning-artifacts`, and none is warranted. `[ASSUMPTION: editor-tooling UX is adequately specified by FR-19..FR-22 and needs no separate UX spine.]`

## Requirements Inventory

### Functional Requirements

Verbatim from PRD §4. Numbering is the PRD's global numbering; it is **not** feature-grouped order.

**Effect Authoring (PRD §4.1)**

- **FR-1:** Author an Effect Definition as an asset — a designer creates an `A2BEffectAsset` via the Create menu and configures Space, Payload, Path, Emission, Easing, counts, durations without code.
- **FR-2:** Author an Effect Definition in code — a programmer builds a Definition fluently and plays it in one statement, without an asset.
- **FR-3:** Play an Effect from a Definition — returns an Effect Handle; invalid Definitions log actionably and return an invalid Handle rather than throwing.

**Spaces and Coordinate Adaptation (PRD §4.2)**

- **FR-4:** Play in every supported Space — `World3D`, `World2D`, `Canvas`, and `Canvas` under all three Render Modes.
- **FR-5:** Cross-Space endpoints — world-space Origin to Canvas Destination; behind-camera Origins handled explicitly.
- **FR-6:** Space Adapters are extensible — register a custom adapter with no edits to shipped source.
- **FR-28:** Canvas rebuild containment — Items parent to a dedicated A2BKit Canvas by default; overridable; sort order configurable.

**Payloads (PRD §4.3)**

- **FR-7:** Sprite, Mesh, Text, and Particle payloads — one lifecycle; `Text` via TMPro with no dependency beyond `com.unity.ugui`; `Particle` raises CPU-side Arrival.
- **FR-8:** Payloads are extensible — register a custom Payload with no edits to shipped source.

**Paths and Motion (PRD §4.4)**

- **FR-9:** Linear, Bezier, Procedural, and Custom paths — every Path pinned to Origin at `t=0` and Destination at `t=1`; pure; zero-alloc.
- **FR-10:** Paths are extensible — a custom Path implemented, registered, and played with zero edits to shipped files.
- **FR-11:** Easing composes with any Path — applied to `t` before Path evaluation, never inside a Path; zero-alloc.
- **FR-26:** Emission — count (incl. range), stagger, and scatter; per-Item variation computed, not collected; `FirstItemArrived` fires on the genuine first Arrival even when an Item overtakes.

**Endpoints (PRD §4.5)**

- **FR-12:** Live endpoint tracking — in-flight Items track a moving `Transform`/`RectTransform`; zero-alloc resolution; no cross-frame position caching unless the provider declares itself static.
- **FR-13:** Endpoint destruction is survivable — destroyed endpoint completes or cancels per policy without `MissingReferenceException`; Items returned to pool.

**Lifecycle, Events, and Async (PRD §4.6)**

- **FR-14:** Full event set — `Started`, `ItemSpawned`, `FirstItemArrived`, `ItemArrived`, `Completed`, `Cancelled`; strict ordering guarantees; zero-alloc dispatch; subscriber exceptions contained.
- **FR-15:** Await an Effect with UniTask — `await PlayAsync(ct)`; cancellation returns Items to pool; bounded Per-Play allocation, zero Per-Frame; async and event paths consistent.
- **FR-16:** Deterministic time — injected Time Source; scaled/unscaled per Effect; no production `Time.deltaTime` read outside the Time Source.
- **FR-27:** Effect Handle validity contract — `IsValid`; every operation on an invalid Handle is a safe no-op; a retained Handle never addresses an Effect that reused its slot; copy-by-value is safe.

**Pooling and Caching (PRD §4.7)**

- **FR-17:** Pool all Items — zero new GameObjects on replay in Steady State; every terminal path returns Items; capacity configurable and pre-warmable; `collectionCheck` in Editor only.
- **FR-18:** Zero per-frame allocation in steady state — automated test asserts 0 B Per-Frame Allocation after warm-up across the claimed matrix; Per-Play Allocation separately asserted constant w.r.t. Item count; warm-up explicitly exempt and measured separately.

**Editor Tooling (PRD §4.8)**

- **FR-19:** Context-aware inspector — only fields relevant to the current Space/Payload/Path; invalid configs surface inline, not as runtime exceptions.
- **FR-20:** Scene gizmos draw the real path — gizmo and runtime call the same Path evaluation; endpoints drawn and distinguishable; live updates.
- **FR-21:** In-editor preview — previews without play mode using the injected Time Source; cleans up fully on stop, scene change, and domain reload.

**Diagnostics (PRD §4.9)**

- **FR-22:** Runtime debug overlay — active Effects, in-flight Item counts, pool occupancy per pool; toggleable; inert/compiled out in release; non-allocating while displayed.
- **FR-23:** Actionable diagnostics — null Destination, missing Payload prefab, and pool exhaustion each produce a distinct message naming the source object; pool exhaustion degrades gracefully.

**Examples and Tests (PRD §4.10)**

- **FR-24:** Example scene per canonical case — exactly seven scenes (coin→wallet, floating score text, XP orbs to a bar, 3D mesh collect, particle burst, moving-target, cross-Space); each of 3 Spaces, 4 Payloads, 4 Path kinds reachable from at least one scene; each scene standalone; art provenance recorded.
- **FR-25:** Automated test coverage — EditMode for pure logic, PlayMode for integration, allocation guard for the budget; `[Test] public async Task` for async; mandatory `using Is = UnityEngine.TestTools.Constraints.Is;` alias; FR-14 ordering asserted explicitly; tests pass headless.

### NonFunctional Requirements

- **NFR-1 — Allocation:** Zero Per-Frame Allocation in Steady-State Playback. Per-Play Allocation bounded and constant w.r.t. Item count, budgeted rather than zero. Warm-up exempt and measured separately.
- **NFR-2 — Frame budget:** 200 concurrent Items at 60 fps on the reference device. `[ASSUMPTION: device unnamed — PRD Open Question 1.]`
- **NFR-3 — Cosmetic systems never throw:** No A2BKit failure mode propagates an exception into caller gameplay code.
- **NFR-4 — Determinism:** Given a fixed Time Source sequence, Effect positions are reproducible frame-for-frame.
- **NFR-5 — Domain-reload safety:** No static mutable state survives a domain reload incorrectly; pools and registries reinitialize cleanly; editor preview leaks nothing.
- **NFR-6 — SOLID structure:** Variation lives behind strategy interfaces; adding a variant modifies no existing class.

### Additional Requirements

Binding technical constraints extracted from the Architecture Spine. Each is an **AD** and is cited by the stories it binds.

**Starter template:** None in the conventional sense. The Architecture Spine specifies an exact **UPM package layout** (`Packages/com.a2bkit/` with `package.json`, `Runtime/Core`, `Runtime/Unity`, `Editor/`, `Tests/EditMode`, `Tests/PlayMode`, `Samples~/`) and a fixed assembly graph. **This layout is the Epic 1 Story 1 deliverable** — it is the substrate every later story builds on, and AD-1 makes it compiler-enforced rather than advisory.

- **AD-1 — Dependency direction is compiler-enforced by assembly, not convention.** `Editor → Unity → Core`, never back. `A2BKit.Core` must not reference `A2BKit.Unity`, `UnityEngine.UI`, `TMPro`, or any `MonoBehaviour`/`ScriptableObject` type; its only permitted Unity surface is value math. Enforced by asmdef references.
- **AD-2 — Strategy implementations are stateless classes; never structs behind an interface.** A struct in an interface-typed field boxes per assignment and per virtual call. Strategy instances are stateless and shared; per-effect data travels in the `in`-passed context struct.
- **AD-3 — Per-frame allocation is zero, by construction.** In tick-reachable code: no LINQ, closures, `params` arrays, `foreach` over interface-typed/boxed enumerators, string concat/interpolation (incl. logs), boxing, or `ToString()` on value types. Enforcement API pinned: `UnityEngine.TestTools.Constraints` + `Is.Not.AllocatingGCMemory()`, with the mandatory `using Is =` alias.
- **AD-4 — All motion math runs in the effect's working space; adapters own every conversion.** `IA2BSpaceAdapter` is the *only* type permitted to call `Camera.WorldToScreenPoint`, `RectTransformUtility.*`, or convert coordinate domains. A conversion call site outside an adapter is a defect regardless of correctness.
- **AD-5 — The Canvas adapter encodes the four known gotchas as invariants.** (a) Overlay passes `null` camera, Screen-Space-Camera passes `canvas.worldCamera`, World-Space uses neither. (b) Every `WorldToScreenPoint` result gated on `z > 0`. (c) Render mode read live from the `Canvas`, never cached or assumed. Each clause has a named test.
- **AD-6 — One scheduler ticks every effect; nothing self-updates; `Tick` takes no delta.** The scheduler pulls delta from each slot's `IA2BTimeSource`, evaluating each distinct source instance at most once per tick. No `Update`/`LateUpdate`/`Coroutine` on items, effects, payloads, or providers. Item order stable and index-based.
- **AD-7 — Effect Handle is a struct with a generation stamp; validity checked on every access.** `readonly struct { int slot; uint generation; }`. Generation increments on every release. No API accepts a raw slot index. A handle never owns disposal.
- **AD-8 — Failure is logged, never thrown.** Subscriber callbacks invoked inside `try/catch` that logs and continues. `OperationCanceledException` on the UniTask path is the sole exception.
- **AD-9 — Every terminal path returns items to the pool through one exit.** A single `ReleaseEffect(slot, reason)` is the only place items are released, the only place generation bumps, and the only place `Completed`/`Cancelled` are raised — making FR-14's "exactly one, never both, never neither" structural.
- **AD-10 — Emission variation is computed from (seed, index), never stored.** Per-item delay and scatter are pure functions evaluated on demand via a struct xorshift RNG. No per-item collection at play time.
- **AD-11 — Two event APIs, one honest about its cost.** `IA2BEffectListener` is the allocation-free path; C# events are the convenience path, documented as costing Per-Play allocation when the subscriber captures. Both raise identical events in identical order.
- **AD-12 — Nothing in the tick path reads `Time.*`.** `IA2BTimeSource` is the only type permitted to read `UnityEngine.Time`. Editor preview and tests inject a manual source — the identical seam.
- **AD-13 — Path evaluation is a pure function pinned at both ends.** `Evaluate(ctx,0) == Origin`, `Evaluate(ctx,1) == Destination` within tolerance, for every path including user-authored. Asserted by a shared conformance test. **Arrival is `t >= 1` and only `t >= 1`** — never distance. `ItemArrived` raised in the tick where `t` crosses 1, index-ascending within the frame.
- **AD-14 — Canvas items parent to a dedicated A2BKit canvas by default.** Overridable. **Pool identity is `(PayloadKind, Definition, Space)`** — closes FR-17's silence on Space. Parenting happens at exactly two call sites: pool `actionOnGet` and `actionOnRelease`.
- **AD-15 — The Space Adapter owns the item's Transform; payload renderers own only drawable properties.** `IA2BSpaceAdapter` exposes `Transform Root` and `ApplyToTransform(Transform, in A2BVisualState)` and is the only type that writes position/rotation/scale/parent. A payload renderer that writes any of those is a defect regardless of correctness. Order: `renderer.UpdateItem` then `adapter.ApplyToTransform`.
- **AD-16 — Emission emits unitless scatter; the adapter assigns units.** `IA2BEmission` returns a unitless normalized offset in `[-1,1]³`; the adapter converts via `ScaleScatter(in Vector3 unit, float radius)`. Scatter applies to the working-space origin **after** `ToWorkingSpace`, never before.
- **AD-17 — `Tick` is non-reentrant; the active-slot collection is never structurally mutated mid-tick.** Effects created during a tick enter a pending queue and first advance on the next tick with a full delta. `ReleaseEffect` during a tick defers slot reuse to the tick boundary; generation still bumps immediately.

**Consistency conventions that bind every story** (Spine §Consistency Conventions): `IA2B*` interface prefix; `A2B*` public type prefix; past-tense event names verbatim from the Glossary; one public type per file; `internal` by default with `InternalsVisibleTo` for tests only; `[SerializeReference]` for polymorphic config; `in`/`ref` struct passing in the tick path; `== null` for Unity refs and `is null` for plain C# refs; `A2BLog.Error/Warn(object context, string message)` never `Debug.Log`; `UnityEngine.Random` forbidden package-wide.

**Stack (pinned, verified 2026-07-17):** Unity 6000.5.0f1; URP 17.5.0; UniTask (git, Cysharp); `com.unity.ugui` 2.5.0 (supplies TMP, namespace `TMPro`; no separate TMP package); Test Framework 1.7.0; `UnityEngine.Pool.ObjectPool<T>` as the pooling substrate.

**Packaging:** `package.json` declares samples with a `path` written **`"Samples/<Name>"` — no tilde** — even though the on-disk folder is `Samples~`.

### UX Design Requirements

**Not applicable.** See the Overview: A2BKit ships no end-user UI. Its human-facing surface is Unity Editor tooling, specified by FR-19 (context-aware inspector), FR-20 (real-path gizmos), FR-21 (in-editor preview), and FR-22 (debug overlay), and carried by Epic 8 and Epic 9. No `bmad-ux` design contract exists or is warranted.

### FR Coverage Map

Every FR maps to an owning epic. Three FRs are deliberately split across epics; each split is named and justified rather than left implicit.

| FR | Epic | Coverage |
| --- | --- | --- |
| FR-1 | E8 | `A2BEffectAsset` + `A2BEffectPlayer` — same files as the drawers that render them |
| FR-2 | E2 | Fluent builder + `A2BEffectDefinition` — it *is* the input to `Play`, so it cannot lag the kernel |
| FR-3 | E2 | `Play` → Effect Handle; invalid Definition logs and returns invalid Handle |
| FR-4 | E5 | World3D / World2D / Canvas adapters incl. all three Render Modes |
| FR-5 | E5 | Cross-Space endpoints; behind-camera gating |
| FR-6 | E5 | Custom adapter registration, zero edits to shipped source |
| FR-7 | E6 | Sprite / Mesh / Particle / Text renderers |
| FR-8 | E6 | Custom payload registration, zero edits to shipped source |
| FR-9 | E3 | Linear / Bezier / Procedural / Custom paths + endpoint-pinning conformance |
| FR-10 | E3 | Custom path registration |
| FR-11 | E3 | Easing library; applied to `t` before `Evaluate` |
| FR-12 | E4 | Live Transform / RectTransform tracking |
| FR-13 | E4 | Destroyed-endpoint survivability |
| FR-14 | E2 | Full event set + ordering guarantees, structural via AD-9's single exit |
| FR-15 | E7 | `PlayAsync` / UniTask / cancellation |
| FR-16 | E2 | Injected `IA2BTimeSource`; scaled/unscaled |
| FR-17 | **E2 + E6** | **Split:** E2 owns effect-slot pooling and AD-9's single release exit; E6 owns the Item GameObject pools and AD-14's `(PayloadKind, Definition, Space)` pool identity. The identity term is meaningless until Payloads and Spaces both exist. |
| FR-18 | **E11 + all** | **Split:** AD-3 forbids deferring allocation proof — *"new code in the tick path ships with its FR-18 matrix entry passing, or it does not ship."* So every epic that touches the tick path carries its own allocation assertions. E11 owns only what cannot exist earlier: the assembled **full Space × Payload × Path matrix**, the Per-Play-constant-w.r.t.-Item-count assertion, and the headless CI gate. |
| FR-19 | E8 | Context-aware inspector via the `[SerializeReference]` drawer |
| FR-20 | E8 | Real-path gizmo calling runtime `Evaluate` |
| FR-21 | E8 | In-editor preview on the manual time source |
| FR-22 | E9 | Runtime debug overlay |
| FR-23 | **E1 + E9** | **Split:** E1 lands the `A2BLog.Error/Warn(object context, string message)` surface because every later epic logs through it from its first story; E9 owns the three named diagnostic messages and the pool-exhaustion policy. |
| FR-24 | E10 | The seven canonical example scenes |
| FR-25 | **E11 + all** | **Split:** per-area tests ship inside the epic that creates the code under test (EditMode math with E3, adapter tests with E5, and so on). E11 owns the async-attribute convention, the `using Is =` alias audit, explicit FR-14 ordering assertions, and the headless/batch-mode run. |
| FR-26 | E3 | Count / stagger / scatter, computed from `(seed, index)` |
| FR-27 | E2 | Handle validity + generation stamp |
| FR-28 | E5 | Canvas rebuild containment via the dedicated A2BKit canvas |

## Epic List

Eleven epics. Sequencing rule: **each epic is independently verifiable at its own boundary, and earlier epics unblock later ones without later epics being required to make earlier ones work.**

The ports & adapters paradigm (AD-1) is what makes this possible for a package with this much internal coupling: E2's simulation kernel is fully verifiable against **in-test fakes** of the seven ports before a single adapter, payload, or path implementation exists. Without hexagonal structure, E2 could not be closed before E5/E6 and the sequence would collapse into one undeliverable epic.

### Epic 1: Package Scaffold and Compiler-Enforced Assembly Boundary

A maintainer can add A2BKit to a Unity 6000.5 project and have it compile, and the **assembly graph itself** now rejects the code that would dissolve the paradigm. This is the Architecture Spine's structural seed made real: the UPM layout, the five asmdefs, the `A2BLog` surface every later story logs through, and a test that fails the build the moment `A2BKit.Core` reaches for `UnityEngine.UI`, `TMPro`, or a `MonoBehaviour`.

**FRs covered:** FR-23 (partial — `A2BLog` surface only) · **Enables:** all
**ADs bound:** AD-1, plus the Consistency Conventions
**Standalone:** yes — verified by compilation plus an asmdef-reference assertion test.
**Note on user value:** this is the one epic that delivers no consumer-visible capability, and that is deliberate rather than a lapse into technical-layering. AD-1 is not setup — it is a *shipped, tested constraint* and the PRD's SM-4/NFR-6 enforcement point. Deferring it means every later epic writes code against an unenforced boundary and AD-1 becomes a cleanup task, which is exactly the failure AD-1 exists to prevent. Kept minimal for that reason.

### Epic 2: The Simulation Kernel — Play, Tick, Events, Handles

A package consumer can build an Effect Definition in code, `Play` it, receive a struct Effect Handle, subscribe to the full event set, and watch it advance deterministically under an injected clock — **all against test doubles, with no Unity scene**. This is the load-bearing epic: it lands the scheduler, the slot pool, AD-9's single release exit (which makes FR-14's "exactly one of Completed or Cancelled" structural rather than disciplinary), AD-7's generation stamp, and AD-17's re-entrancy queue.

**FRs covered:** FR-2, FR-3, FR-14, FR-16, FR-17 (slot pooling + single exit), FR-27
**ADs bound:** AD-2, AD-3, AD-6, AD-7, AD-8, AD-9, AD-11, AD-12, AD-17
**Standalone:** yes — EditMode-verifiable against in-test fakes of the seven ports.
**Depends on:** E1 (assembly boundary).

### Epic 3: Paths, Easing, and Emission

A designer can shape *how* items travel and *how they are released*: Linear, Bezier, Procedural, and Custom paths, any easing composed with any path, and count/stagger/scatter emission. Pure math over structs, EditMode-testable with no scene — the payoff AD-1 was bought for. Ships AD-13's shared **path conformance test** that any user-authored path can run.

**FRs covered:** FR-9, FR-10, FR-11, FR-26
**ADs bound:** AD-13 (endpoint pinning, Arrival = `t >= 1`), AD-10 (variation from `(seed,index)`), AD-16 (emission side — unitless scatter), AD-2, AD-3
**Standalone:** yes — every story is EditMode, no scene, no adapters.
**Depends on:** E1, E2 (the context structs and port definitions).

### Epic 4: Endpoint Providers — Live Tracking and Survivable Destruction

A package consumer can bind an Origin or Destination to a static point, a moving `Transform`, or a moving `RectTransform`, and in-flight Items track it to its *current* position. Destroying an endpoint mid-flight terminates the Effect predictably instead of throwing a `MissingReferenceException` into gameplay code.

**FRs covered:** FR-12, FR-13
**ADs bound:** AD-4 (providers resolve to **world position + validity flag** — never a converted coordinate), AD-8, AD-9, AD-3
**Standalone:** yes — a provider resolving a world position with a validity flag is verifiable without any adapter.
**Depends on:** E1, E2. Deliberately **before** E5: an adapter converts a world position and does not care where it came from, but no end-to-end Space test can run without at least a static provider.

### Epic 5: Spaces — World3D, World2D, Canvas, and Cross-Space

A package consumer can play an Effect in any of the three Spaces, in `Canvas` Space under all three Render Modes, and across Spaces (the canonical world-chest → Canvas-wallet case) — **without ever touching `WorldToScreenPoint` or `ScreenPointToLocalPointInRectangle`**. This is the epic the package exists for: it makes the four documented conversion bugs unreachable from user code, and it contains AD-15, the spine's largest identified hole.

**FRs covered:** FR-4, FR-5, FR-6, FR-28
**ADs bound:** AD-4 (adapters own every conversion), AD-5 (the four Canvas gotchas as named tests), AD-14 (dedicated canvas + pool identity), AD-15 (adapter owns the Transform; renderers own drawables only), AD-16 (adapter assigns scatter units)
**Standalone:** yes — verifiable with a stub payload renderer.
**Depends on:** E1, E2, E3, E4.

### Epic 6: Payloads — Sprite, Mesh, Particle, Text

A package consumer can make an Item *look like* something: a coin sprite, a gem mesh, a particle burst, or a `"+250"` text label — with Path, Space, and event code branching on none of it. Lands the Item GameObject pools under AD-14's `(PayloadKind, Definition, Space)` identity, and holds the AD-15 line that a renderer touching `transform.position` is a defect **regardless of correctness**.

**FRs covered:** FR-7, FR-8, FR-17 (Item GameObject pools + pool identity)
**ADs bound:** AD-15 (drawables only), AD-14 (pool identity), AD-2, AD-9, AD-3, AD-1 (TMPro stays out of Core)
**Standalone:** yes.
**Depends on:** E1, E2, E5 (pool identity's Space term is undefined until Spaces exist).

### Epic 7: Async and Cancellation

A consumer can `await effect.PlayAsync(ct)` so the level-up panel opens exactly when the last orb lands, and cancel cleanly when the player quits mid-flight. Separated from E2 because it is a genuine **risk boundary**, not a layer: the UniTask awaiter is the *only* permitted Per-Play allocation contributor (PRD §9.3) and the sole place `OperationCanceledException` is allowed to escape (AD-8).

**FRs covered:** FR-15
**ADs bound:** AD-11 (async never suppresses events, nor vice versa), AD-8, AD-9, AD-7, AD-3
**Standalone:** yes.
**Depends on:** E1, E2.

### Epic 8: Authoring and Editor Tooling — the Designer's Surface

A designer with no interest in C# can create an effect asset from the Create menu, see **only** the fields their Space/Payload/Path selection makes relevant, watch the real path redraw live in the Scene view against the real endpoints, and preview it without entering play mode. Consolidated into one epic rather than four because FR-1, FR-19, FR-20, and FR-21 are **the same files** — the asset, its `[SerializeReference]` drawer, and its gizmo — and the step-02 file-overlap rule says to merge rather than churn.

**FRs covered:** FR-1, FR-19, FR-20, FR-21
**ADs bound:** AD-13 (the gizmo calls the **same** `Evaluate` as runtime, so divergence is impossible by construction), AD-12 (preview injects the manual time source — the identical seam the tests use), AD-1, AD-8, plus the `[SerializeReference]` convention
**Standalone:** yes.
**Depends on:** E1–E6 (an asset must be playable for the inspector and preview to mean anything).

### Epic 9: Diagnostics

A developer six months later can see 200 coins stuck on screen, toggle an overlay, and read active-Effect count and pool occupancy **without attaching a profiler** — and any misconfiguration names the offending asset or GameObject instead of failing silently.

**FRs covered:** FR-22, FR-23 (the named messages + pool-exhaustion policy)
**ADs bound:** AD-3 (the overlay must not violate the package's own headline constraint), AD-8
**Standalone:** yes.
**Depends on:** E1–E6.

### Epic 10: The Seven Canonical Example Scenes

A developer new to A2BKit gets a coin→wallet effect running in under five minutes using only an example scene and the inspector, without reading source. Exactly **seven** scenes — not the 48-cell matrix, which E11 covers by test far more cheaply and reliably.

**FRs covered:** FR-24
**ADs bound:** the `package.json` samples-array rule (`"Samples/<Name>"`, **no tilde**, despite the on-disk `Samples~`)
**Standalone:** yes.
**Depends on:** E1–E8.

### Epic 11: The Allocation Matrix and the Test Gate

The perf owner runs the allocation test in CI and gets a **hard failure** the moment someone's change starts allocating during playback. This epic does **not** own testing generally — AD-3 forbids that, so per-area tests ship inside the epic that creates the code under test. E11 owns only what genuinely cannot exist before every axis does: the assembled full Space × Payload × Path matrix, the Per-Play-constant-w.r.t.-Item-count assertion, and the headless gate.

**FRs covered:** FR-18, FR-25
**ADs bound:** AD-3 (the pinned `Is.Not.AllocatingGCMemory()` enforcement API and the **mandatory `using Is =` alias** — without it NUnit's `Is` silently wins and the suite passes while measuring nothing), AD-13, AD-14
**Standalone:** yes.
**Depends on:** E1–E7 (needs all three axes to exist to form the matrix).
</content>
</invoke>

---

## Epic 1: Package Scaffold and Compiler-Enforced Assembly Boundary

Deliver the Architecture Spine's structural seed as a real, compiling UPM package whose assembly graph mechanically rejects the code that would dissolve the ports & adapters paradigm. **FRs:** FR-23 (partial). **ADs:** AD-1 + Consistency Conventions. **NFRs:** NFR-6.

### Story 1.1: UPM package skeleton

As a **package maintainer**,
I want A2BKit to exist as a UPM package with the exact folder tree the Architecture Spine specifies,
So that every later story has a defined, unambiguous home for its files and no one invents a parallel layout.

**Acceptance Criteria:**

**Given** a Unity 6000.5 project,
**When** the package is placed at `Packages/com.a2bkit/`,
**Then** `package.json` declares `name: "com.a2bkit"`, `unity: "6000.5"`, and version `1.0.0`,
**And** the folder tree matches the Spine exactly: `Runtime/Core/{Simulation,Ports,Paths,Easing,Emission,Time,Config}`, `Runtime/Unity/{Runner,Spaces,Payloads,Endpoints,Authoring,Diagnostics}`, `Editor/{Inspectors,Gizmos,Preview}`, `Tests/{EditMode,PlayMode}`, `Samples~/`.

**Given** `package.json`,
**When** its `dependencies` are read,
**Then** they list `com.unity.ugui` (2.5.0) and the Test Framework (1.7.0), and UniTask is resolvable from the project manifest,
**And** no dependency on DOTween, PrimeTween, or Coffee UIParticle exists (PRD §9.4 — explicitly rejected).

**Given** the samples declaration (Spine §Structural Seed — the named packaging trap),
**When** the `samples` array in `package.json` is inspected,
**Then** each entry's `path` is written `"Samples/<Name>"` with **no tilde**, even though the on-disk folder is `Samples~`,
**And** an automated test asserts no `path` value contains `~`.

**Given** the package,
**When** Unity imports it,
**Then** compilation succeeds with zero errors and zero warnings.

### Story 1.2: Five assembly definitions enforcing the dependency direction

As a **package maintainer**,
I want the dependency direction `Editor → Unity → Core` enforced by asmdef references rather than by review,
So that Unity types cannot creep into the simulation and silently make the core untestable without a scene.

**Acceptance Criteria:**

**Given** the package,
**When** the asmdefs are inspected,
**Then** exactly five exist: `A2BKit.Core`, `A2BKit.Unity`, `A2BKit.Editor`, `A2BKit.Tests.EditMode`, `A2BKit.Tests.PlayMode`,
**And** `A2BKit.Core.references` is empty of any A2BKit assembly, `A2BKit.Unity` references `A2BKit.Core`, and `A2BKit.Editor` references both,
**And** `A2BKit.Editor` has `includePlatforms: ["Editor"]`.

**Given** AD-1's rule,
**When** an EditMode test reflects over every type in the `A2BKit.Core` assembly,
**Then** the test **fails** if any type references `UnityEngine.UI`, `TMPro`, `UnityEngine.Canvas`, `MonoBehaviour`, or `ScriptableObject`,
**And** the test passes for the permitted value-math surface only (`Vector2/3/4`, `Quaternion`, `Mathf`, `Color`, `AnimationCurve`).

**Given** a maintainer adds `using UnityEngine.UI;` to any file under `Runtime/Core/`,
**When** the project compiles,
**Then** compilation **fails** — the guard is the compiler via asmdef references, not the reflection test alone (the test is the backstop for types reachable transitively).

**Given** `A2BKit.Core.asmdef`,
**When** its `InternalsVisibleTo` grants are inspected,
**Then** they name **only** `A2BKit.Tests.EditMode` and `A2BKit.Tests.PlayMode` (Spine §Consistency Conventions — `internal` is the default).

### Story 1.3: The A2BLog diagnostic surface

As a **package maintainer**,
I want one logging surface that always names the offending object and never allocates in the tick path,
So that every later story logs through it from its first line instead of reaching for `Debug.Log` and retrofitting FR-23 later.

**Acceptance Criteria:**

**Given** `A2BLog`,
**When** its API is inspected,
**Then** it exposes `Error(object context, string message)` and `Warn(object context, string message)` and nothing that takes a format string or `params` array (AD-3),
**And** the `context` argument is passed through to Unity's log context so clicking the console entry selects the offending asset or GameObject (FR-23).

**Given** AD-3's rule that log calls must not concatenate or interpolate in the tick path,
**When** an EditMode test greps the `A2BKit.Core` and `A2BKit.Unity` sources,
**Then** zero direct `Debug.Log`/`Debug.LogError`/`Debug.LogWarning` call sites exist outside `A2BLog` itself,
**And** `A2BLog` exposes a level check (e.g. `A2BLog.ErrorEnabled`) callers can guard with so message construction is skippable.

**Given** a release build,
**When** `A2BLog` is compiled,
**Then** verbose/diagnostic levels are compiled out via `[Conditional]` or a define, and the remaining error path allocates zero bytes when passed a constant string.

---

## Epic 2: The Simulation Kernel — Play, Tick, Events, Handles

Land the core that owns *when an item is and where it goes*: pure math over structs driven by an injected clock, verifiable end-to-end against in-test fakes of the seven ports with **no Unity scene**. **FRs:** FR-2, FR-3, FR-14, FR-16, FR-17 (slots), FR-27. **ADs:** AD-2, AD-3, AD-6, AD-7, AD-8, AD-9, AD-11, AD-12, AD-17. **NFRs:** NFR-1, NFR-3, NFR-4, NFR-6.

### Story 2.1: The seven port interfaces and the context structs

As a **package maintainer**,
I want the seven `IA2B*` ports and their `in`-passed context structs defined in `A2BKit.Core`,
So that the kernel and every adapter are written against one fixed contract instead of drifting into seven bespoke shapes.

**Acceptance Criteria:**

**Given** `Runtime/Core/Ports/`,
**When** its contents are inspected,
**Then** exactly seven interfaces exist — `IA2BPath`, `IA2BEasing`, `IA2BEmission`, `IA2BEndpointProvider`, `IA2BSpaceAdapter`, `IA2BTimeSource`, `IA2BPayloadRenderer` — one public type per file, file name == type name,
**And** each is an **interface**, not an abstract base class with protected state (PRD §9.1).

**Given** the port signatures,
**When** they are inspected,
**Then** `IA2BPath.Evaluate(in A2BPathContext ctx, float t)` returns `Vector3`; `IA2BEndpointProvider` resolves to a **world position plus a validity flag** (AD-4); `IA2BSpaceAdapter` exposes `Transform Root`, `ApplyToTransform(Transform, in A2BVisualState)`, and `ScaleScatter(in Vector3 unit, float radius)` (AD-15, AD-16); `IA2BEmission` returns a **unitless** offset in `[-1,1]³` (AD-16),
**And** every context/state struct is passed by `in` (mutated state by `ref`), never by value (Spine §Consistency Conventions).

**Given** AD-1,
**When** the `IA2BSpaceAdapter` signature is compiled into `A2BKit.Core`,
**Then** its `Transform`/`ApplyToTransform` surface uses only `UnityEngine.Transform` from `UnityEngine.CoreModule` — no `UnityEngine.UI`, no `TMPro`,
**And** the Story 1.2 reflection guard still passes. `[ASSUMPTION: UnityEngine.Transform is inside AD-1's permitted surface. AD-1 literally names only "value math (Vector2/3/4, Quaternion, Mathf, Color, AnimationCurve)", yet AD-15 places Transform Root and ApplyToTransform(Transform, ...) on a port that lives in Core. Resolved in favour of Transform being permitted — otherwise AD-15's port cannot be declared where AD-15 says it lives. Flagged in Open Questions as a genuine spine conflict.]`

**Given** AD-2,
**When** the port contracts are documented,
**Then** XML comments on each port state that implementations must be **stateless classes** and that per-effect data travels in the context struct.

### Story 2.2: Time sources — scaled, unscaled, and manual

As a **package consumer**,
I want an Effect to advance from an injected Time Source rather than reading `Time.deltaTime`,
So that a reward can fly during a paused menu and a test can assert exact positions without entering play mode.

**Acceptance Criteria:**

**Given** `Runtime/Core/Time/`,
**When** its contents are inspected,
**Then** three `IA2BTimeSource` implementations exist: scaled, unscaled, and manual,
**And** each is a `class`, not a struct (AD-2).

**Given** AD-12's rule,
**When** an EditMode test greps `A2BKit.Core` and `A2BKit.Unity` for `UnityEngine.Time.`,
**Then** the **only** matching call sites are inside the scaled and unscaled time source implementations,
**And** the test fails if any other type reads `Time.*`.

**Given** the manual time source,
**When** a test calls `Advance(0.016f)` five times,
**Then** the source reports a delta of exactly `0.016f` on each of the five reads and `0` on a sixth read without an intervening `Advance`,
**And** this is the identical seam the editor preview uses (FR-21), so preview and tests cannot drift from runtime behavior.

**Given** the scaled and unscaled sources,
**When** `Time.timeScale == 0`,
**Then** the scaled source reports `0` and the unscaled source reports the real frame delta (FR-16 — the paused-menu case).

**Given** any time source,
**When** its delta is read,
**Then** the read allocates zero bytes, asserted with `Is.Not.AllocatingGCMemory()` under the mandatory `using Is = UnityEngine.TestTools.Constraints.Is;` alias (AD-3).

### Story 2.3: A2BEffectDefinition and the fluent builder

As a **package consumer**,
I want to build an Effect Definition fluently and play it in one chained statement without an asset,
So that I get code-first control without being forced through a ScriptableObject.

**Acceptance Criteria:**

**Given** `Runtime/Core/Config/`,
**When** the fluent builder is used,
**Then** a Definition can be constructed, played, and awaited in a **single chained expression** (FR-2),
**And** the builder lives in `A2BKit.Core` and references no `ScriptableObject` (AD-1).

**Given** a default-constructed Definition,
**When** it is played,
**Then** it is playable with no further configuration — every field has a sane default (FR-1's consequence, honored by the shared Definition type).

**Given** FR-2's allocation consequence,
**When** a definition is built once and played 100 times,
**Then** building allocates only at build time and **never per-Play in Steady-State Playback**, asserted by measuring 0 B per-frame across the 100 plays.

**Given** polymorphic config fields (`IA2BPath`, `IA2BEmission`, `IA2BEasing`, payload),
**When** the Definition is inspected,
**Then** they are declared for `[SerializeReference]` use (Spine §Consistency Conventions) — the one mechanism that yields both open/closed (FR-10) and the context-aware inspector (FR-19).

**Given** a Definition,
**When** it is cloned and overridden in code,
**Then** the source Definition is not mutated (FR-2) — asserted here against a runtime-built Definition, and re-asserted against an asset in Story 8.1.

### Story 2.4: The scheduler — one tick, N deltas, stable item order

As a **package maintainer**,
I want exactly one scheduler that ticks every effect, with `Tick()` taking **no delta**,
So that 200 items cost one interop call instead of 200 `MonoBehaviour.Update()` callbacks, and scaled and unscaled effects can coexist in the same frame.

**Acceptance Criteria:**

**Given** `A2BScheduler`,
**When** its API is inspected,
**Then** `Tick()` takes **no parameters** (AD-6 — a single `Tick(dt)` cannot serve a paused-menu effect and a gameplay effect in the same frame),
**And** the scheduler pulls the delta from each slot's `IA2BTimeSource` and never fetches one itself (AD-12).

**Given** ten active slots sharing three distinct `IA2BTimeSource` instances,
**When** `Tick()` runs once,
**Then** each **distinct source instance** is evaluated **at most once** and its delta cached for that tick (AD-6),
**And** a counting fake source asserts exactly 3 reads, not 10.

**Given** AD-6's no-self-update rule,
**When** an EditMode test reflects over `A2BKit.Core` and `A2BKit.Unity`,
**Then** no item, effect slot, payload renderer, or endpoint provider type declares `Update`, `LateUpdate`, `FixedUpdate`, or a coroutine,
**And** the only `Update` in the package is the single `A2BRunner` driver.

**Given** an effect with N items,
**When** it is ticked across many frames,
**Then** item order within the effect is **stable and index-based** across frames (AD-6),
**And** iteration is an index `for` over a concrete array — not `foreach` over an interface-typed enumerator (AD-3).

**Given** a steady-state tick,
**When** `Tick()` is called for 100 frames after warm-up,
**Then** per-frame allocation is **0 B**, asserted with `Is.Not.AllocatingGCMemory()` under the `using Is =` alias (AD-3, FR-18 — this story's matrix entry ships with it, per AD-3's "or it does not ship").

### Story 2.5: The Effect Handle and its generation stamp

As a **package consumer**,
I want a struct Handle that is safe to copy and safe to hold after its effect ends,
So that a stale copy never cancels someone else's coins — the silent, intermittent, near-undiagnosable pooled-handle bug.

**Acceptance Criteria:**

**Given** `A2BEffectHandle`,
**When** its declaration is inspected,
**Then** it is a `readonly struct` containing exactly `int slot` and `uint generation` (AD-7),
**And** no public API accepts a raw slot index.

**Given** a slot,
**When** it is released,
**Then** its generation increments (AD-7),
**And** **every** handle operation — subscribe, cancel, await, query — resolves through a validity check comparing stamps before touching slot state.

**Given** an effect that has completed and a subsequent `Play` that reuses the same pooled slot,
**When** the **retained stale handle** is used,
**Then** `IsValid` reports `false`, every operation is a **safe no-op returning a safe default**, and the **new** effect occupying that slot is unaffected (FR-27, AD-7),
**And** this is asserted by an explicit named test — it is the aliasing bug the generation stamp exists to prevent.

**Given** an invalid handle,
**When** any operation is called on it,
**Then** no exception is thrown (NFR-3, AD-8) and no event is raised.

**Given** a handle,
**When** it is copied by value and both copies are cancelled,
**Then** ownership is not duplicated and the effect is not double-cancelled (FR-27) — a handle never owns disposal.

### Story 2.6: Play, and the invalid-Definition contract

As a **package consumer**,
I want `Play` to hand back a Handle synchronously and to fail *loudly but safely* on a bad Definition,
So that a misconfigured coin effect logs an error naming the culprit instead of throwing into my reward-granting call stack.

**Acceptance Criteria:**

**Given** a valid Definition with an Origin and Destination,
**When** `Play` is called,
**Then** it returns an Effect Handle **synchronously** and items begin emitting per the Emission config (FR-3).

**Given** the same Definition played concurrently N times,
**When** all N effects run,
**Then** they are independent and share **no mutable state** (FR-3),
**And** a test asserting per-effect state divergence passes — which holds structurally because strategies are stateless (AD-2) and per-effect data lives in the slot.

**Given** an invalid Definition (unset Destination, or missing Payload prefab),
**When** `Play` is called,
**Then** exactly one actionable error is logged **at Play** via `A2BLog.Error(context, message)` naming the offending object, and an **invalid Handle** is returned (FR-3, AD-8),
**And** **no exception is thrown** and the failure is not deferred to Arrival.

**Given** the invalid Handle from that call,
**When** it is inspected,
**Then** `IsValid == false`, it raises no events, and it is safe to await — completing immediately as cancelled (FR-3).

### Story 2.7: The single release exit and slot pooling

As a **package maintainer**,
I want every terminal path to funnel through one `ReleaseEffect(slot, reason)`,
So that leaks on the paths nobody tests — cancel-mid-flight, destroyed-destination, subscriber-threw — become structurally impossible instead of a discipline problem.

**Acceptance Criteria:**

**Given** `ReleaseEffect(slot, reason)`,
**When** the sources are audited,
**Then** it is the **only** place items are released, the **only** place the generation is bumped, and the **only** place `Completed`/`Cancelled` are raised (AD-9),
**And** an EditMode test asserts no other call site releases items or bumps a generation.

**Given** the five terminal reasons — Completed, Cancelled, destroyed endpoint, invalid config, subscriber exception —
**When** each is triggered in turn,
**Then** each has its **own named test** asserting pool occupancy returns to the pre-play baseline (AD-9, FR-17),
**And** coverage is verified per reason, not only on the happy path.

**Given** effect slots,
**When** effects are played and released repeatedly in steady state,
**Then** slot objects are pooled and reused — zero new `EffectSlot` allocations after warm-up (FR-17),
**And** the backing `ItemState[]` is reused rather than reallocated per play.

**Given** pool capacity,
**When** it is configured and pre-warmed,
**Then** the configured capacity is honored and pre-warming fills the pool before first play (FR-17).

**Given** the Editor,
**When** an item is double-released,
**Then** Unity's `collectionCheck` detects it; **and** in player builds `collectionCheck` is disabled for performance (FR-17).

### Story 2.8: The full event set, on two APIs

As a **package consumer**,
I want the six events with ironclad ordering, on both an allocation-free API and a convenient one,
So that my currency counter starts rolling exactly when the first coin lands, and I am told honestly what the convenient path costs.

**Acceptance Criteria:**

**Given** an Effect Handle,
**When** its events are inspected,
**Then** exactly six exist, named verbatim from the Glossary: `Started`, `ItemSpawned`, `FirstItemArrived`, `ItemArrived`, `Completed`, `Cancelled` (FR-14),
**And** `IA2BEffectListener` is the supported allocation-free path (one reusable implementer, dispatch is a plain interface call), while C# events on the handle are the convenience path (AD-11).

**Given** both APIs,
**When** the same effect is run twice, once per API,
**Then** both raise **identical events in identical order**, asserted by comparing the recorded sequences (AD-11),
**And** the C# event path is documented as costing **Per-Play** allocation when the subscriber captures — documented, not hidden.

**Given** FR-14's ordering guarantees,
**When** an effect with N items completes,
**Then** `Started` fires before any `ItemSpawned`; `FirstItemArrived` fires **exactly once**, on the first Arrival, always before `Completed`; `ItemArrived` fires exactly N times; and **exactly one** of `Completed`/`Cancelled` fires — never both, never neither (structural via AD-9's single exit),
**And** each guarantee has its own explicit assertion (FR-25).

**Given** a subscriber that throws,
**When** its callback is invoked,
**Then** the exception is caught inside the dispatch `try/catch`, logged via `A2BLog`, and **the remaining events still fire** (AD-8),
**And** effect state is not corrupted and no pooled item leaks — the effect still funnels through `ReleaseEffect` (AD-9).

**Given** steady-state dispatch,
**When** events are raised for 100 frames,
**Then** dispatch allocates **0 B** — no delegate boxing, no per-dispatch closure capture, no `params` arrays, no boxed event args (FR-14, AD-3, AD-11).

### Story 2.9: Non-reentrant tick and the pending queue

As a **package consumer**,
I want to call `Play()` from inside an `ItemArrived` callback,
So that I can spawn a follow-up burst when a coin lands — the documented use case — without corrupting the scheduler.

**Acceptance Criteria:**

**Given** a subscriber that calls `Play()` from inside an `ItemArrived` callback,
**When** the tick runs,
**Then** the new effect enters a **pending queue** and receives its first advance on the **next** tick with a **full delta** (AD-17),
**And** this exact scenario is the named test AD-17 requires.

**Given** the active-slot collection,
**When** any tick executes,
**Then** it is **never structurally mutated** between the loop's first and last index (AD-17),
**And** the index `for` loop's bound is captured before iteration.

**Given** `ReleaseEffect` called **during** a tick,
**When** it runs,
**Then** slot reuse defers to the tick boundary, but the **generation bumps immediately** so AD-7 invalidates stale handles at once (AD-17),
**And** a test asserts a handle released mid-tick reports `IsValid == false` before the tick ends.

**Given** the pending queue and deferred reuse,
**When** they operate for 100 frames in steady state,
**Then** they allocate **0 B** per frame — the queue is pre-sized and reused, never grown per frame (AD-3, AD-17).

---

## Epic 3: Paths, Easing, and Emission

Land the trajectory and release model as pure math over structs — EditMode-testable with no scene, which is the payoff AD-1 was bought for. **FRs:** FR-9, FR-10, FR-11, FR-26. **ADs:** AD-13, AD-10, AD-16 (emission side), AD-2, AD-3. **NFRs:** NFR-1, NFR-4, NFR-6.

### Story 3.1: The path conformance contract and the Linear path

As a **package maintainer**,
I want a shared conformance test that pins every path to its endpoints, plus the Linear path that proves it,
So that Arrival — and therefore `FirstItemArrived`, the package's reason to exist — is meaningful for every path including ones users write.

**Acceptance Criteria:**

**Given** the shared conformance test harness,
**When** it is run against any `IA2BPath` implementation,
**Then** it asserts `Evaluate(ctx, 0) == ctx.Origin` and `Evaluate(ctx, 1) == ctx.Destination` within floating-point tolerance (AD-13, FR-9),
**And** the harness is **public** so any user-authored path can run it (AD-13 — "a shared conformance test any custom path can run"), justified against SM-C1 as an intentional public surface.

**Given** the conformance harness,
**When** it is run against a deliberately non-conforming fake path that drifts from the Destination at `t=1`,
**Then** the harness **fails** — proving it actually measures something.

**Given** `LinearPath`,
**When** it is evaluated,
**Then** it passes the conformance harness,
**And** it is a stateless `class` (AD-2), and `Evaluate` is a pure function of `(t, Origin, Destination, params)` with no frame state and no side effects (FR-9, AD-13).

**Given** `LinearPath.Evaluate`,
**When** it is called 10,000 times,
**Then** it allocates **0 B** (FR-9, AD-3), asserted under the `using Is =` alias.

**Given** AD-13's easing rule,
**When** any path implementation is audited,
**Then** no path applies easing internally — easing reparameterizes `t` *before* `Evaluate` (a path that eases internally would double-apply and break FR-11 composition).

### Story 3.2: The Bezier path with designer-legible arc

As a **designer**,
I want to shape the arc with a height and a direction rather than raw control points,
So that I can tune the coin's flight without reasoning about cubic control polygons.

**Acceptance Criteria:**

**Given** `BezierPath`,
**When** its inspector-facing parameters are inspected,
**Then** they are expressed as **arc height and direction**, not raw control points (FR-9). `[ASSUMPTION: PRD FR-9 tags the exact parameterization as an assumption; arc height + direction vector is the drafting resolution.]`

**Given** `BezierPath` with any arc height including 0 and negative values,
**When** it is run against the Story 3.1 conformance harness,
**Then** it passes — pinned to Origin at `t=0` and Destination at `t=1` regardless of arc (AD-13).

**Given** a non-zero arc height,
**When** the path is evaluated at `t=0.5`,
**Then** the result is displaced from the Linear midpoint in the configured direction by a magnitude that scales monotonically with arc height (FR-9).

**Given** `BezierPath.Evaluate`,
**When** it is called 10,000 times,
**Then** it allocates **0 B** (AD-3).

### Story 3.3: Procedural paths

As a **designer**,
I want parameterized procedural motion — spiral, wave, scatter-then-gather — configured without code,
So that I can reach for a distinctive flight shape without asking a programmer.

**Acceptance Criteria:**

**Given** `ProceduralPath`,
**When** its configuration is inspected,
**Then** it supports at minimum spiral, wave, and scatter-then-gather modes, each parameterized and configurable **without code** (FR-9).

**Given** every procedural mode and any parameter combination,
**When** run against the Story 3.1 conformance harness,
**Then** each passes the endpoint-pinning invariant (AD-13) — including scatter-then-gather, whose whole point is departing the direct line mid-flight.

**Given** any procedural mode,
**When** evaluated twice with identical `(t, ctx, params)`,
**Then** results are bit-identical — the path is pure and derives any randomness from an explicit seed, never `UnityEngine.Random` (NFR-4, Spine §Consistency Conventions — `UnityEngine.Random` is forbidden package-wide).

**Given** `ProceduralPath.Evaluate`,
**When** called 10,000 times in any mode,
**Then** it allocates **0 B** (AD-3).

### Story 3.4: Custom paths — the open/closed proof

As a **package consumer**,
I want to add a path kind without touching A2BKit source,
So that the open/closed claim in the brief's success criteria is a fact rather than a promise.

**Acceptance Criteria:**

**Given** a custom `IA2BPath` implemented **in the test assembly**,
**When** it is registered and played,
**Then** it runs with **zero edits to any shipped file** (FR-10, SM-4),
**And** the test asserts this by living entirely outside `A2BKit.Core`/`A2BKit.Unity`.

**Given** the custom path,
**When** it is run against the public conformance harness from Story 3.1,
**Then** it passes — demonstrating the harness is usable by third parties (AD-13).

**Given** the `[SerializeReference]` mechanism,
**When** the custom path type exists in the project,
**Then** it is offered by the drawer's implementation picker without any registry edit (Spine §Consistency Conventions; the drawer itself lands in Story 8.3).

**Given** SM-C1 (do not grow the public API to win SM-4),
**When** the public surface added by this story is reviewed,
**Then** it is limited to `IA2BPath`, `A2BPathContext`, and the conformance harness — nothing else is made public to enable extension.

### Story 3.5: The easing library and its composition rule

As a **package consumer**,
I want any easing to compose with any path, including custom ones,
So that feel and trajectory are tuned independently instead of multiplying into a combinatorial mess.

**Acceptance Criteria:**

**Given** `Runtime/Core/Easing/`,
**When** it is inspected,
**Then** a standard easing library is provided, and a custom easing curve can be supplied (FR-11),
**And** each easing is a stateless `class` (AD-2).

**Given** any easing and any path,
**When** an item is advanced,
**Then** easing is applied to `t` **before** `Evaluate` and never inside a path implementation (FR-11, AD-13),
**And** an EditMode test asserts the composition order by injecting a recording fake easing and a recording fake path.

**Given** easing composed with a path,
**When** `t=0` and `t=1` are evaluated,
**Then** the endpoint-pinning invariant still holds — easing must map `0→0` and `1→1`, asserted for every shipped easing (AD-13).

**Given** any easing evaluation,
**When** called 10,000 times,
**Then** it allocates **0 B** (FR-11, AD-3) — including the `AnimationCurve`-backed custom easing path.

### Story 3.6: Emission — count, ranges, and stagger

As a **designer**,
I want to configure how many items release and when,
So that a burst reads as a burst rather than N items appearing at one instant.

**Acceptance Criteria:**

**Given** Emission config,
**When** it is inspected,
**Then** item count is configurable **including a range** for per-play variation (FR-26),
**And** release timing supports all three modes: all-at-once, fixed stagger interval, and spread evenly across a duration (FR-26).

**Given** AD-10's rule,
**When** the implementation is audited,
**Then** per-item delay is a **pure function of `(effectSeed, itemIndex)`** evaluated on demand via a struct xorshift RNG,
**And** **no per-item collection** (no `List<float>` of delays) is built at play time — asserted by a test playing a 10-item and a 200-item effect and measuring identical Per-Play allocation.

**Given** the same `effectSeed`,
**When** an effect is played twice,
**Then** the per-item delay layout is identical (NFR-4, AD-10) — which is what makes stagger testable.

**Given** stagger configured,
**When** the effect runs,
**Then** `Started` fires **once** for the effect regardless of stagger, and before any `ItemSpawned` (FR-26, FR-14 ordering holds).

**Given** emission evaluation in steady state,
**When** ticked for 100 frames,
**Then** it allocates **0 B** per frame (FR-26, AD-3).

### Story 3.7: Unitless scatter

As a **designer**,
I want an authored scatter radius to mean the same thing conceptually in every Space,
So that one asset does not scatter 50 px on a Canvas and 50 m in World3D.

**Acceptance Criteria:**

**Given** `IA2BEmission`,
**When** it returns a scatter offset,
**Then** the offset is **unitless and normalized** in `[-1,1]³` (AD-16),
**And** emission never assigns units and never learns which Space it is in (AD-4).

**Given** AD-16,
**When** the emission implementation is audited,
**Then** it contains **no** call to `ScaleScatter` and no radius multiplication — unit assignment is the adapter's job (adapter side lands in Story 5.1),
**And** an EditMode test asserts returned offsets are within `[-1,1]` on every axis for 10,000 `(seed, index)` pairs.

**Given** scatter and `(seed, index)`,
**When** evaluated twice with the same inputs,
**Then** the offset is identical (AD-10, NFR-4),
**And** scatter distribution over many indices is non-degenerate (not all items at one point) — the failure FR-26 explicitly names.

**Given** scatter evaluation,
**When** called 10,000 times,
**Then** it allocates **0 B** (AD-3, AD-10).

---

## Epic 4: Endpoint Providers — Live Tracking and Survivable Destruction

Make moving targets the default rather than the edge case, and make a destroyed endpoint a non-event rather than a `MissingReferenceException` in gameplay code. **FRs:** FR-12, FR-13. **ADs:** AD-4, AD-8, AD-9, AD-3. **NFRs:** NFR-1, NFR-3.

### Story 4.1: The endpoint contract and the static provider

As a **package maintainer**,
I want providers to resolve to a **world position plus a validity flag** and nothing else,
So that AD-4's division of labour holds: providers locate, adapters convert, and neither learns the other's job.

**Acceptance Criteria:**

**Given** any `IA2BEndpointProvider`,
**When** it resolves,
**Then** it returns a **world position and a validity flag** — never a screen, canvas, or local coordinate (AD-4),
**And** an EditMode test greps `Runtime/Unity/Endpoints/` and asserts **zero** call sites of `WorldToScreenPoint`, `ScreenPointToLocalPointInRectangle`, or any `RectTransformUtility` member (AD-4 — a conversion call site outside a Space Adapter is a defect regardless of correctness).

**Given** the static point provider,
**When** it resolves across frames,
**Then** it returns the configured world position and **declares itself static** (FR-12),
**And** because it declares itself static, caching its position across frames is permitted.

**Given** any non-static provider,
**When** the implementation is audited,
**Then** no position is cached across frames (FR-12) — providers are the **only** place positions are read.

**Given** any provider resolution,
**When** called 10,000 times,
**Then** it allocates **0 B** (FR-12, AD-3).

### Story 4.2: Transform and RectTransform live tracking

As a **package consumer**,
I want in-flight items to track a Destination that moves,
So that coins land on my wallet HUD even while it slides in from off-screen.

**Acceptance Criteria:**

**Given** a Destination bound to a moving `Transform`,
**When** the Destination is moved mid-flight,
**Then** in-flight items arrive at the Destination's **current** position, not its position at `Play` (FR-12),
**And** a PlayMode test moves the target between ticks and asserts arrival within tolerance of the final position.

**Given** a Destination bound to a moving `RectTransform`,
**When** it is moved mid-flight,
**Then** the same holds (FR-12),
**And** the provider resolves the `RectTransform` to a **world position** (`RectTransform` has one) — leaving canvas conversion to the adapter (AD-4).

**Given** the scheduler's data flow (Spine §Structural Seed),
**When** an effect with N items is ticked,
**Then** endpoints resolve **once per effect**, not once per item — asserted with a counting fake provider on a 200-item effect expecting exactly 2 resolutions (origin + destination) per tick, not 400,
**And** this is the difference between 1 and N `Transform` reads.

**Given** live tracking in steady state,
**When** ticked for 100 frames,
**Then** resolution allocates **0 B** per frame (FR-12, AD-3).

### Story 4.3: Survivable endpoint destruction

As a **package consumer**,
I want destroying a target mid-flight to be survivable,
So that closing a UI panel during a coin burst does not throw into my gameplay code or leak the pool.

**Acceptance Criteria:**

**Given** an effect in flight,
**When** its Destination `GameObject` is destroyed,
**Then** the provider resolves as **invalid** (validity flag `false`) rather than throwing (AD-4, NFR-3),
**And** **no `MissingReferenceException`** reaches caller code (FR-13, AD-8).

**Given** an invalid endpoint resolution,
**When** the configured policy is applied,
**Then** the effect completes or cancels per policy and raises the corresponding event (FR-13),
**And** the **default policy is cancel-and-release**. `[ASSUMPTION: PRD FR-13 and its Assumptions Index tag this default as an inference — a cosmetic system must never throw into gameplay code.]`

**Given** the destroyed-endpoint path,
**When** it terminates,
**Then** it funnels through the single `ReleaseEffect(slot, reason)` exit (AD-9) and **pooled items are returned — no leak** (FR-13),
**And** the named test from Story 2.7's five-reason matrix asserts pool occupancy returns to baseline.

**Given** the nullability convention,
**When** the provider checks its Unity object reference,
**Then** it uses `== null` (honoring Unity's fake-null lifetime check), **not** `is null` (Spine §Consistency Conventions — mixing them here is a real bug, not style; `is null` would miss the destroyed object entirely and this is exactly the call site where it matters).

---

## Epic 5: Spaces — World3D, World2D, Canvas, and Cross-Space

Make the four documented coordinate-conversion bugs **unreachable from user code**. This is the epic the package exists for, and it carries AD-15 — the largest hole the spine's own review found. **FRs:** FR-4, FR-5, FR-6, FR-28. **ADs:** AD-4, AD-5, AD-14, AD-15, AD-16. **NFRs:** NFR-1, NFR-2, NFR-3.

### Story 5.1: The Space Adapter contract — Root, ApplyToTransform, ScaleScatter

As a **package maintainer**,
I want the adapter to be the *only* type that writes an item's Transform and the *only* type that assigns scatter units,
So that two payload authors both obeying AD-4 cannot still produce a mesh at raw canvas coordinates near the world origin.

**Acceptance Criteria:**

**Given** `IA2BSpaceAdapter`,
**When** its surface is inspected,
**Then** it exposes `Transform Root`, `ApplyToTransform(Transform t, in A2BVisualState s)`, `ToWorkingSpace(...)`, and `ScaleScatter(in Vector3 unit, float radius)` (AD-15, AD-16).

**Given** AD-15's rule,
**When** the sources are audited by an EditMode test,
**Then** `IA2BSpaceAdapter` implementations are the **only** types that write an item's `position`, `localPosition`, `rotation`, `localRotation`, `scale`, `localScale`, or `parent`,
**And** the test **fails** if any `IA2BPayloadRenderer` implementation writes any of them — *a payload renderer that writes a Transform is a defect regardless of correctness*.

**Given** AD-4's rule,
**When** the sources are audited,
**Then** `IA2BSpaceAdapter` implementations are the **only** types that call `Camera.WorldToScreenPoint`, `RectTransformUtility.*`, or otherwise convert coordinate domains,
**And** the test greps the whole package and fails on any conversion call site outside `Runtime/Unity/Spaces/`.

**Given** the scheduler's per-item order (Spine §Structural Seed),
**When** an item is updated,
**Then** the scheduler calls `renderer.UpdateItem` **then** `adapter.ApplyToTransform`, **in that order** (AD-15),
**And** a test with recording fakes asserts the call order.

**Given** AD-16,
**When** scatter is applied,
**Then** the adapter converts the unitless `[-1,1]³` offset via `ScaleScatter(unit, radius)`, and scatter applies to the working-space origin **after** `ToWorkingSpace`, **never before** (AD-16).

### Story 5.2: The World3D adapter

As a **package consumer**,
I want to play an Effect in `World3D` Space,
So that a gem flies from a chest to a collection point in my 3D scene.

**Acceptance Criteria:**

**Given** the World3D adapter,
**When** an effect is played in `World3D` Space,
**Then** items follow the path between the world Origin and world Destination within tolerance (FR-4),
**And** working space **is** world space, so `ToWorkingSpace` is identity.

**Given** AD-16's per-adapter requirement,
**When** the adapter's named scatter test runs,
**Then** a unit offset of `1.0` on an axis maps to exactly `radius` **world units** on that axis (AD-16 — "each adapter carries a named test asserting a unit offset maps to `radius` in its own domain").

**Given** `ApplyToTransform`,
**When** it writes the item's Transform,
**Then** it writes world `position` (AD-15), and the item is parented to the adapter's `Root`.

**Given** a World3D effect with 200 items,
**When** ticked for 100 frames in steady state,
**Then** per-frame allocation is **0 B** (AD-3, FR-18 — this axis's matrix entry ships with this story).

### Story 5.3: The World2D adapter

As a **package consumer**,
I want to play an Effect in `World2D` Space,
So that coins fly between sprites in my 2D game without Z fighting or perspective surprises.

**Acceptance Criteria:**

**Given** the World2D adapter,
**When** an effect is played in `World2D` Space,
**Then** items follow the path between Origin and Destination within tolerance (FR-4),
**And** motion is constrained to the XY plane with Z handled per the adapter's documented, configurable rule. `[ASSUMPTION: World2D pins Z to the adapter Root's Z by default. The PRD names World2D as a distinct Space but never states how Z differs from World3D; if Z were free, World2D would be behaviourally identical to World3D and would not warrant a separate Space. Flagged in Open Questions.]`

**Given** the adapter's named scatter test (AD-16),
**When** it runs,
**Then** a unit offset of `1.0` maps to exactly `radius` world units on X and Y.

**Given** sorting,
**When** mixed World2D and other-Space effects are on screen,
**Then** draw order is **defined and configurable per-effect** rather than incidental (FR-28's feature-NFR). `[ASSUMPTION: per-Effect sorting config suffices; PRD tags a global sorting authority as over-engineering for v1.]`

**Given** a World2D effect with 200 items,
**When** ticked for 100 frames in steady state,
**Then** per-frame allocation is **0 B** (AD-3, FR-18).

### Story 5.4: The Canvas adapter and the four gotchas

As a **package consumer**,
I want `Canvas` Space to work under all three Render Modes without me branching,
So that I never write `ScreenPointToLocalPointInRectangle` and never re-earn the four documented conversion bugs.

**Acceptance Criteria:**

**Given** an Overlay canvas,
**When** the adapter calls `RectTransformUtility.ScreenPointToLocalPointInRectangle`,
**Then** it passes **`null`** as the camera (AD-5a),
**And** a **named test** asserts this — the camera argument is the single most commonly wrong argument in this API.

**Given** a Screen-Space-Camera canvas,
**When** the adapter converts,
**Then** it passes **`canvas.worldCamera`** (AD-5a),
**And** a **named test** asserts it.

**Given** a World-Space canvas,
**When** the adapter converts,
**Then** it uses **neither** screen-point path (AD-5a),
**And** a **named test** asserts it.

**Given** any `WorldToScreenPoint` result,
**When** it is used,
**Then** it is **gated on `z > 0` first** (AD-5b),
**And** a **named test** asserts a behind-camera world point resolves **invalid** rather than as a plausible on-screen coordinate.

**Given** render mode,
**When** the adapter reads it,
**Then** it is read from the **live `Canvas`** every time — **never cached across frames, never assumed from config** (AD-5c),
**And** a **named test** switches a canvas's render mode at runtime mid-flight and asserts the adapter adapts without caller-side branching (FR-4).

**Given** the adapter's named scatter test (AD-16),
**When** it runs,
**Then** a unit offset of `1.0` maps to exactly `radius` in the canvas's own domain.

**Given** a Canvas effect with 200 items under each of the three Render Modes,
**When** ticked for 100 frames in steady state,
**Then** per-frame allocation is **0 B** (AD-3, FR-18).

### Story 5.5: Canvas rebuild containment

As a **package consumer**,
I want flying items to not dirty my HUD canvas,
So that the rebuild storm — not the motion — stops being my frame spike at 200 items.

**Acceptance Criteria:**

**Given** `Canvas` Space,
**When** items spawn with default configuration,
**Then** they parent to a **dedicated A2BKit-owned Canvas**, **not** the Destination's Canvas (FR-28, AD-14),
**And** a test asserts the host game's HUD Canvas is **not** marked dirty while 200 items move on the A2BKit canvas — the #2 recurring failure in the addendum's survey.

**Given** the default,
**When** a team that has profiled the opposite trade wants to opt out,
**Then** the target Canvas is **overridable** (FR-28, AD-14) — the default trades a draw call for rebuild isolation, since batches do not merge across canvases. `[ASSUMPTION: PRD and AD-14 both tag rebuild isolation as the right default.]`

**Given** the A2BKit canvas,
**When** sort order is configured,
**Then** items can be made to fly **above or below** host HUD elements (FR-28),
**And** a test asserts both orderings.

**Given** AD-14's parenting rule,
**When** the sources are audited,
**Then** parenting happens at **exactly two call sites and nowhere else**: the pool's `actionOnGet` (parents to the adapter's `Root`) and `actionOnRelease` (parents back to the pool root),
**And** an EditMode test asserts no third call site sets `parent`.

### Story 5.6: Cross-Space endpoints and the behind-camera policy

As a **package consumer**,
I want a world-space chest to throw coins into my Canvas wallet HUD,
So that the canonical A-to-B case works without me computing a single screen-space coordinate.

**Acceptance Criteria:**

**Given** an effect with a **world-space Origin** and a **Canvas Destination**,
**When** it is played,
**Then** items land on the Destination's `RectTransform` **within tolerance** on Arrival (FR-5),
**And** the caller writes no conversion code.

**Given** a world Origin that is **behind the camera**,
**When** the effect is played,
**Then** the Item **does not spawn at a spurious on-screen position** (FR-5),
**And** the endpoint resolves invalid via AD-5b's `z > 0` gate, and the behavior is **clamp/suppress, not throw** (NFR-3). `[ASSUMPTION: PRD FR-5 and Open Question 5 leave this open — clamp/suppress is the least-surprise resolution for a cosmetic system. Flagged in Open Questions.]`

**Given** the cross-Space case,
**When** the Destination moves mid-flight,
**Then** live tracking (FR-12) still holds across the Space boundary — items arrive at the RectTransform's current position.

**Given** a cross-Space effect,
**When** ticked for 100 frames in steady state,
**Then** per-frame allocation is **0 B** (AD-3, FR-18).

### Story 5.7: Custom Space Adapters

As a **package consumer**,
I want to register a custom Space Adapter without touching A2BKit source,
So that an unanticipated coordinate domain does not require me to fork the package.

**Acceptance Criteria:**

**Given** a custom `IA2BSpaceAdapter` implemented **in the test assembly**,
**When** it is supplied to an effect and played,
**Then** it is used with **zero edits to any shipped A2BKit file** (FR-6, SM-4).

**Given** the custom adapter,
**When** it is audited against AD-15 and AD-16,
**Then** it satisfies the same contract as shipped adapters — owning the Transform write and assigning scatter units,
**And** the AD-16 unit-offset-maps-to-radius test is runnable against it.

**Given** SM-C1,
**When** the public surface added by this story is reviewed,
**Then** it is limited to `IA2BSpaceAdapter`, `A2BVisualState`, and the registration seam — nothing further is made public to enable extension.

---

## Epic 6: Payloads — Sprite, Mesh, Particle, Text

Make an Item *look like* something, with Path, Space, and event code branching on none of it. **FRs:** FR-7, FR-8, FR-17 (Item GameObject pools). **ADs:** AD-15, AD-14, AD-2, AD-9, AD-3, AD-1. **NFRs:** NFR-1, NFR-2.

### Story 6.1: The payload renderer contract and pooled item identity

As a **package maintainer**,
I want one payload lifecycle and a pool identity that includes Space,
So that a Sprite item carrying a `RectTransform` under a Canvas is never handed to a World3D effect.

**Acceptance Criteria:**

**Given** `IA2BPayloadRenderer`,
**When** its surface is inspected,
**Then** it exposes the shared lifecycle (acquire → `UpdateItem(id, in A2BVisualState)` → release) and **drawable properties only**: sprite, mesh, color, text content, material (AD-15),
**And** it exposes **no** Transform-writing member.

**Given** AD-14's pool identity rule,
**When** the pools are inspected,
**Then** pool identity is **`(PayloadKind, Definition, Space)`** — the Space term included (AD-14 closes FR-17's silence on Space),
**And** a **named test** asserts a Sprite item used by a Canvas effect is **never** handed to a World3D effect after release.

**Given** the pooling substrate,
**When** it is inspected,
**Then** it is `UnityEngine.Pool.ObjectPool<T>` — no bespoke pool (PRD §9.4),
**And** `collectionCheck` is enabled in the Editor and disabled in player builds (FR-17).

**Given** steady-state replay,
**When** the same Effect is played twice after warm-up,
**Then** **zero new GameObjects are instantiated** (FR-17),
**And** pool capacity is configurable and pre-warmable (FR-17).

**Given** AD-15,
**When** each shipped renderer is audited by the Story 5.1 guard test,
**Then** none writes `transform.position`, `localPosition`, `parent`, or `localScale`.

### Story 6.2: The Sprite payload

As a **package consumer**,
I want coins to be sprites,
So that the canonical coin→wallet effect looks like coins.

**Acceptance Criteria:**

**Given** a `Sprite` Payload,
**When** an effect is played in each of the three Spaces,
**Then** items render as the configured sprite (FR-7),
**And** the renderer touches drawable properties only (AD-15).

**Given** the Sprite renderer,
**When** used in `Canvas` Space vs `World3D` Space,
**Then** the **Path, Space, and event code paths are identical** — payload choice changes none of them (FR-7),
**And** a test asserts the same event sequence across both.

**Given** a Sprite effect with 200 items,
**When** ticked for 100 frames in steady state,
**Then** per-frame allocation is **0 B** (AD-3, FR-18).

### Story 6.3: The Mesh payload

As a **package consumer**,
I want items to be 3D meshes,
So that a gem collect reads as a solid object rather than a flat card.

**Acceptance Criteria:**

**Given** a `Mesh` Payload,
**When** an effect is played,
**Then** items render with the configured mesh and material (FR-7),
**And** rendering is URP-validated (PRD §9.4) and uses no pipeline-specific API in `A2BKit.Core` (AD-1).

**Given** a Mesh Payload in `Canvas` Space,
**When** it is played,
**Then** it renders correctly — this is precisely the case AD-15 exists to protect (a Canvas-Space mesh at raw canvas coordinates near the world origin is the named failure),
**And** a test asserts the item's world position is within tolerance of the expected canvas-derived position.

**Given** a Mesh effect with 200 items,
**When** ticked for 100 frames in steady state,
**Then** per-frame allocation is **0 B** (AD-3, FR-18).

### Story 6.4: The Text payload

As a **package consumer**,
I want a `"+250"` label to float off a kill,
So that the score feedback is legible without me building a text pool.

**Acceptance Criteria:**

**Given** a `Text` Payload,
**When** it renders,
**Then** it renders via **TextMeshPro** with **no package dependency beyond `com.unity.ugui`** (namespace `TMPro`; uGUI 2.5.0 supplies TMP — there is no separate TMP package) (FR-7, Spine §Stack),
**And** `TMPro` is referenced from `A2BKit.Unity` only — the Story 1.2 guard asserts it never reaches `A2BKit.Core` (AD-1).

**Given** per-item text content,
**When** it is set at Play (e.g. `"+250"`),
**Then** it is settable **per-Item** (FR-7),
**And** **no new string is allocated per frame** — asserted by a 100-frame steady-state test measuring 0 B (FR-7, AD-3).

**Given** AD-3's rule against `ToString()` on value types in the tick path,
**When** the Text renderer is audited,
**Then** no `ToString()`, string concatenation, or interpolation occurs in the tick path — numeric content is converted at Play or via a non-allocating writer. `[ASSUMPTION: content is set at Play, not recomputed per frame; FR-7 only requires per-Item content set at Play.]`

**Given** localization,
**When** the Text payload is used,
**Then** the **caller supplies the string** — no localization is performed (PRD §6.2, out of scope).

### Story 6.5: The Particle payload

As a **package consumer**,
I want particle items that still raise Arrival events,
So that I get particle visuals without losing the CPU-side hook the package exists to provide.

**Acceptance Criteria:**

**Given** a `Particle` Payload,
**When** items arrive,
**Then** Arrival events are raised from **CPU-side simulation** (FR-7) — this is precisely why VFX Graph is excluded (GPU particles cannot raise CPU-side Arrival events),
**And** `ItemArrived` fires once per item and `FirstItemArrived` exactly once (FR-14 ordering holds).

**Given** the Particle renderer,
**When** it is audited,
**Then** it uses Unity's built-in particle system CPU path and touches drawable properties only (AD-15),
**And** no VFX Graph dependency exists (PRD §5 Non-Goals).

**Given** a Particle effect with 200 items,
**When** ticked for 100 frames in steady state,
**Then** per-frame allocation is **0 B** (AD-3, FR-18) — including the particle system's own per-frame surface, which must be driven without `GetParticles`/`SetParticles` array churn. `[ASSUMPTION: a persistent, pre-sized particle buffer is reused; the PRD requires zero per-frame allocation but does not name the mechanism.]`

### Story 6.6: Custom payloads

As a **package consumer**,
I want to implement a payload without touching A2BKit source,
So that a bespoke item visual does not require a fork.

**Acceptance Criteria:**

**Given** a custom `IA2BPayloadRenderer` implemented **in the test assembly**,
**When** it is registered and played,
**Then** it is used with **zero edits to any shipped A2BKit file** (FR-8, SM-4).

**Given** the custom payload,
**When** it is audited by the Story 5.1 AD-15 guard,
**Then** the guard applies to it too — a custom renderer writing a Transform is a defect, and the guard test is runnable by third parties.

**Given** the custom payload,
**When** it is pooled,
**Then** it participates in the `(PayloadKind, Definition, Space)` pool identity (AD-14) and returns to the pool on every terminal path (AD-9).

**Given** SM-C1,
**When** the public surface added by this story is reviewed,
**Then** it is limited to `IA2BPayloadRenderer` and `A2BVisualState` — already public from earlier stories; this story adds no new public types.

---

## Epic 7: Async and Cancellation

Let a reward sequence await the last orb and cancel cleanly when the player quits mid-flight. Separated from E2 as a genuine risk boundary: the UniTask awaiter is the **only** permitted Per-Play allocation contributor and the sole place an exception may escape. **FRs:** FR-15. **ADs:** AD-11, AD-8, AD-9, AD-7, AD-3. **NFRs:** NFR-1, NFR-3.

### Story 7.1: PlayAsync and awaiting completion

As a **package consumer**,
I want to `await effect.PlayAsync(ct)`,
So that my "level up" panel opens exactly when the last orb lands, without me polling a flag.

**Acceptance Criteria:**

**Given** a valid Definition,
**When** `await PlayAsync(ct)` is called,
**Then** the await **resumes when the Effect completes** (FR-15),
**And** the test is written as **`[Test] public async Task`** — natively supported by Test Framework 1.7 (Spine §Consistency Conventions).

**Given** the test convention,
**When** async tests are audited,
**Then** **no** test uses the `[UnityTest] + UniTask.ToCoroutine` bridge — a 2021-era workaround that forfeits 1.7's async fixes (Spine §Consistency Conventions),
**And** `[UnityTest] IEnumerator` remains permitted for frame-stepping tests only.

**Given** an **invalid** Definition,
**When** `PlayAsync` is awaited,
**Then** it **completes immediately as cancelled** and does not throw (FR-3, NFR-3).

**Given** UniTask,
**When** the dependency is inspected,
**Then** it resolves from the project manifest (Cysharp/UniTask, git) and is referenced by `A2BKit.Core` and `A2BKit.Unity` per the Spine's dependency graph.

### Story 7.2: Cancellation

As a **package consumer**,
I want cancelling a token to stop the effect cleanly,
So that quitting mid-flight does not leak 200 coins or throw from a destroyed host.

**Acceptance Criteria:**

**Given** an effect in flight,
**When** its `CancellationToken` is cancelled,
**Then** the effect stops, `Cancelled` is raised, and **all Items return to the pool** (FR-15),
**And** it funnels through the single `ReleaseEffect(slot, Cancelled)` exit (AD-9), with the named pool-occupancy test from Story 2.7's five-reason matrix.

**Given** a host `MonoBehaviour` that is destroyed mid-flight,
**When** cancellation arrives via `destroyCancellationToken`,
**Then** **no unhandled exception** is thrown (FR-15).

**Given** AD-8's rule,
**When** the async path's exception behavior is audited,
**Then** `OperationCanceledException` is the **sole** exception A2BKit allows to propagate, and only because it is the language's cancellation contract (AD-8),
**And** no other A2BKit API throws for a runtime or configuration fault (NFR-3).

**Given** cancellation,
**When** it fires,
**Then** **exactly one** of `Completed`/`Cancelled` is raised — never both, never neither (FR-14, structural via AD-9).

### Story 7.3: The Per-Play allocation contract and event/async consistency

As a **perf owner**,
I want the awaiter's cost bounded, constant, and honestly measured,
So that "zero allocation" survives contact with the async path instead of quietly becoming false.

**Acceptance Criteria:**

**Given** an awaited effect,
**When** it is ticked for 100 frames after the Play frame,
**Then** **Per-Frame Allocation is 0 B** (FR-15, NFR-1) — the awaiter cost lands at Play, not per frame,
**And** the measurement **excludes the Play frame** (PRD Glossary — Per-Frame Allocation is measured excluding the Play frame).

**Given** a 10-item and a 200-item awaited effect,
**When** Per-Play Allocation is measured for each,
**Then** the two are **equal** — the awaiter cost **does not scale with Item count** (FR-15, NFR-1, PRD §9.3),
**And** this is asserted as a byte-for-byte comparison, not a threshold.

**Given** PRD §9.3,
**When** Per-Play allocation contributors are audited,
**Then** the **UniTask awaiter machinery is the only permitted contributor**,
**And** a test asserts the non-async `Play` path's Per-Play allocation is strictly lower than `PlayAsync`'s, isolating the awaiter as the delta.

**Given** AD-11,
**When** an effect is awaited **and** has event subscribers,
**Then** **awaiting does not suppress events, and subscribing does not suppress the await** (FR-15, AD-11),
**And** a test asserts the full event sequence fires identically with and without an active await.

---

## Epic 8: Authoring and Editor Tooling — the Designer's Surface

Give the designer persona the whole product: create the asset, see only what matters, watch the real path against the real endpoints, preview without play mode. Consolidated into one epic because FR-1/19/20/21 are the same files. **FRs:** FR-1, FR-19, FR-20, FR-21. **ADs:** AD-13, AD-12, AD-1, AD-8, `[SerializeReference]` convention. **NFRs:** NFR-3, NFR-5.

### Story 8.1: The A2BEffectAsset

As a **designer**,
I want to create an effect asset from the Create menu and configure it without code,
So that I own the feel of the effect without opening a script.

**Acceptance Criteria:**

**Given** the Unity Editor,
**When** the Create menu is opened,
**Then** the asset appears under **`Assets > Create > A2BKit > Effect`** (FR-1).

**Given** a **default-constructed** asset,
**When** it is played with no further configuration,
**Then** it is **playable** — every field has a sane default (FR-1, and the enabling half of SM-3's five-minute first effect).

**Given** the asset,
**When** it is configured,
**Then** Space, Payload, Path, Emission, Easing, counts, and durations are all configurable **without code changes** (FR-1),
**And** **no scene references are baked into the asset** (FR-1) — endpoints are supplied at Play.

**Given** the asset,
**When** it is cloned and overridden in code,
**Then** the **source asset is not mutated** (FR-2) — the runtime-Definition assertion from Story 2.3, now re-asserted against the ScriptableObject.

**Given** AD-1,
**When** the asset is compiled,
**Then** it lives in `A2BKit.Unity` (`Runtime/Unity/Authoring/`) — `ScriptableObject` never enters `A2BKit.Core`, and the Story 1.2 guard asserts it.

### Story 8.2: The A2BEffectPlayer component

As a **designer**,
I want to drop a component on my chest prefab, point it at the wallet HUD, and call `Play()`,
So that I wire the canonical effect without computing a screen-space coordinate — UJ-1, end to end.

**Acceptance Criteria:**

**Given** `A2BEffectPlayer`,
**When** it is added to a GameObject,
**Then** it accepts an `A2BEffectAsset` and Origin/Destination endpoint references (including a `RectTransform` target) and exposes `Play()` (FR-1, UJ-1).

**Given** a Destination pointed at a Canvas `RectTransform` and an Origin on a world-space chest,
**When** `Play()` is called,
**Then** the cross-Space effect runs (FR-5) and the caller writes **no** coordinate conversion code,
**And** `FirstItemArrived` is exposed so the currency counter can start on the first landing (FR-14, UJ-1).

**Given** an unconfigured player component,
**When** `Play()` is called,
**Then** an actionable error naming the offending GameObject is logged and an invalid handle is returned — **no throw** (FR-3, FR-23, AD-8).

**Given** AD-6,
**When** `A2BEffectPlayer` is audited,
**Then** it declares **no** `Update`/`LateUpdate`/coroutine — only the single `A2BRunner` ticks (AD-6).

### Story 8.3: The context-aware inspector

As a **designer**,
I want to see only the fields my Space/Payload/Path selection makes relevant,
So that the asset is configurable without reading source — and without me guessing which of 40 fields apply.

**Acceptance Criteria:**

**Given** the `[SerializeReference]` drawer,
**When** a polymorphic field (`IA2BPath`, `IA2BPayload`, `IA2BEmission`, `IA2BEasing`) is drawn,
**Then** it offers a picker listing available implementations — **the one mechanism that yields both open/closed (FR-10) and the context-aware inspector (FR-19)** (Spine §Consistency Conventions),
**And** a custom implementation from any assembly appears in the picker with no registry edit (FR-6, FR-8, FR-10).

**Given** `Linear` is selected as the Path,
**When** the inspector draws,
**Then** **Bezier arc fields are hidden** (FR-19).

**Given** `Text` is selected as the Payload,
**When** the inspector draws,
**Then** **sprite fields are hidden** (FR-19).

**Given** SM-C2 (do not grow inspector fields to win SM-3),
**When** the inspector is reviewed,
**Then** the field count is not expanded to compensate for defaults — a five-minute first effect requires **good defaults, not more knobs**.

### Story 8.4: Inline validation messages

As a **designer**,
I want a bad configuration to tell me so in the inspector,
So that I find out at authoring time rather than as a runtime surprise.

**Acceptance Criteria:**

**Given** an invalid configuration (unset Destination, missing Payload prefab),
**When** the asset is selected,
**Then** an **inline, actionable message** is shown in the inspector (FR-19),
**And** it is **not** a runtime exception (FR-19, NFR-3).

**Given** the inline message,
**When** it is read,
**Then** it **names the offending field or object** and states the corrective action (FR-23).

**Given** a configuration that is invalid at authoring time,
**When** it is nonetheless played,
**Then** the runtime path still logs via `A2BLog.Error` and returns an invalid handle (FR-3) — the inspector message is an **addition** to the runtime contract, not a replacement for it.

### Story 8.5: Scene gizmos that draw the real path

As a **designer**,
I want to see the actual computed path in the Scene view against the actual endpoints,
So that what I tune is what ships — UJ-2.

**Acceptance Criteria:**

**Given** the gizmo,
**When** it draws the path,
**Then** it calls the **same `Evaluate`** as the runtime (AD-13, FR-20) — **a divergence is impossible by construction**,
**And** a test asserts gizmo-sampled points equal runtime-evaluated points bit-for-bit at the same `t` values.

**Given** an effect with real endpoints,
**When** the gizmo draws,
**Then** **Origin and Destination are drawn and visually distinguishable** (FR-20).

**Given** a parameter change (e.g. dragging arc height),
**When** the value changes,
**Then** the gizmo **updates live** without requiring play mode or reselection (FR-20, UJ-2).

**Given** AD-1,
**When** the gizmo is compiled,
**Then** it lives in `A2BKit.Editor` and references `A2BKit.Core` for `Evaluate` — the Editor assembly may reference Core directly per the Spine's dependency graph.

### Story 8.6: In-editor preview

As a **designer**,
I want to preview the effect without entering play mode,
So that iteration costs a click rather than a domain reload.

**Acceptance Criteria:**

**Given** the preview,
**When** it animates,
**Then** it advances via the **injected manual Time Source** — the **identical seam the tests use** (FR-21, AD-12),
**And** a test asserts preview and runtime produce identical positions for the same delta sequence (NFR-4) — which is why preview and tests cannot drift from runtime behavior.

**Given** an active preview,
**When** the user stops it,
**Then** it **cleans up fully — no leaked preview objects** (FR-21).

**Given** an active preview,
**When** the **scene changes** or a **domain reload** occurs,
**Then** it cleans up fully with no leaked objects and no orphaned static state (FR-21, NFR-5),
**And** each of the three teardown paths — stop, scene change, domain reload — has its **own named test** (these are the paths that leak in practice).

**Given** NFR-5,
**When** a domain reload occurs,
**Then** pools and registries **reinitialize cleanly** and no static mutable state survives incorrectly.

---

## Epic 9: Diagnostics

Answer "what is running and where did my pool go" without a profiler session, and make every misconfiguration name its culprit. **FRs:** FR-22, FR-23. **ADs:** AD-3, AD-8. **NFRs:** NFR-1, NFR-3.

### Story 9.1: The three named diagnostic messages

As a **package consumer**,
I want a misconfiguration to name the offending asset or GameObject,
So that I fix it in seconds instead of bisecting my scene.

**Acceptance Criteria:**

**Given** a **null Destination**,
**When** the effect is played,
**Then** a **distinct** message is logged identifying the source object (FR-23).

**Given** a **missing Payload prefab**,
**When** the effect is played,
**Then** a **distinct** message is logged identifying the source object (FR-23).

**Given** **pool exhaustion**,
**When** it occurs,
**Then** a **distinct** message is logged identifying the pool and the source object (FR-23),
**And** all three messages are distinguishable from one another — asserted by a test using `LogAssert.Expect` on each specific message.

**Given** all three,
**When** they are emitted,
**Then** each routes through `A2BLog.Error/Warn(object context, string message)` with the context populated so the console entry selects the culprit (FR-23),
**And** none throws (NFR-3, AD-8).

### Story 9.2: Pool exhaustion policy

As a **package consumer**,
I want an exhausted pool to degrade rather than throw,
So that a busy frame costs me a spike, not a lost reward the player earned.

**Acceptance Criteria:**

**Given** an exhausted pool,
**When** another item is requested,
**Then** it **degrades gracefully — grow or drop per policy — rather than throwing** (FR-23, NFR-3, AD-8).

**Given** the default policy,
**When** it is inspected,
**Then** it is **grow-with-warning** (FR-23). `[ASSUMPTION: PRD FR-23, its Assumptions Index, and Open Question 6 all tag this — dropping a reward the player earned is worse than a frame spike. Needs confirmation against a memory-constrained target.]`

**Given** the drop policy,
**When** it is configured and the pool exhausts,
**Then** items are dropped without throwing, and the drop is logged once (not per item per frame — AD-3 forbids per-frame log construction in the tick path).

**Given** pool growth,
**When** it occurs,
**Then** it is **warm-up-like allocation, explicitly outside the per-frame budget** (NFR-1) — and a test asserts steady-state per-frame allocation returns to **0 B** once the pool has grown.

### Story 9.3: The runtime debug overlay

As a **package consumer**,
I want an overlay showing what is running and where my pool went,
So that I diagnose 200 stuck coins without attaching a profiler — UJ-4.

**Acceptance Criteria:**

**Given** the overlay,
**When** it is enabled at runtime,
**Then** it reports **active Effects**, **in-flight Item counts**, and **pool occupancy (active / available / capacity) per pool** (FR-22),
**And** it is **toggleable at runtime** (FR-22).

**Given** a release build,
**When** the overlay is compiled,
**Then** it is **compiled out, or inert and non-allocating** (FR-22).

**Given** the overlay **while displayed**,
**When** 100 frames elapse,
**Then** it allocates **0 B per frame** (FR-22, AD-3),
**And** this is asserted with `Is.Not.AllocatingGCMemory()` under the `using Is =` alias — *a diagnostic that violates the package's own headline constraint would be embarrassing*. `[ASSUMPTION: PRD FR-22 tags non-allocating-overlay as achievable and worth the effort. This is the hardest AD-3 target in the package: rendering counts as text without ToString() or interpolation requires a non-allocating number formatter. Flagged in Open Questions.]`

**Given** AD-3's ban on `ToString()` on value types in the tick path,
**When** the overlay renders its counters,
**Then** it uses a non-allocating number-to-char-buffer writer rather than `int.ToString()` or string interpolation.

---

## Epic 10: The Seven Canonical Example Scenes

The examples are the documentation. Exactly seven scenes — not the 48-cell matrix, which E11 covers by test far more cheaply and reliably. **FRs:** FR-24. **ADs:** packaging samples-array rule. **Metrics:** SM-3, SM-5.

### Story 10.1: Samples infrastructure and art provenance

As a **package consumer**,
I want the samples importable from the Package Manager,
So that I can get to a running example without hand-copying folders.

**Acceptance Criteria:**

**Given** `package.json`,
**When** its `samples` array is inspected,
**Then** each entry's `path` is written **`"Samples/<Name>"` — no tilde** — despite the on-disk folder being `Samples~` (Spine §Structural Seed; Unity renames `Samples` → `Samples~` on export, and writing `Samples~/…` in the array is the usual mistake),
**And** the Story 1.1 no-tilde assertion covers every entry added here.

**Given** the Package Manager,
**When** a sample is imported,
**Then** it lands in the consuming project and **runs standalone with no external setup** (FR-24).

**Given** every art asset used by the samples,
**When** provenance is checked,
**Then** each is **either sourced with a compatible license or authored in Blender**, and **provenance is recorded** in a file under `Samples~/` (FR-24).

**Given** the samples,
**When** they are inspected,
**Then** they depend only on A2BKit, uGUI/TMP, and URP — no third-party dependency (PRD §5 Non-Goals).

### Story 10.2: Coin→wallet and floating score text

As a **developer new to A2BKit**,
I want a runnable coin→wallet scene,
So that I get the canonical effect running in under five minutes without reading source — SM-3.

**Acceptance Criteria:**

**Given** the **coin→wallet (Canvas)** scene,
**When** it is opened and played,
**Then** coins fly from a chest to a wallet HUD `RectTransform` and the counter **starts rolling on `FirstItemArrived`** (FR-24, UJ-1),
**And** it runs standalone with no external setup (FR-24).

**Given** the **floating score text** scene,
**When** it is opened and played,
**Then** a `"+250"` label floats off a kill using the `Text` Payload (FR-24, FR-7).

**Given** SM-3,
**When** a developer new to the package uses only the coin→wallet scene and the inspector,
**Then** they can reach a working effect **without reading source** — the scene is configured through the inspector, not through a bespoke script.

**Given** both scenes,
**When** they run,
**Then** no console errors or warnings are emitted.

### Story 10.3: XP orbs, 3D mesh collect, and particle burst

As a **developer**,
I want scenes covering the orb, mesh, and particle cases,
So that every Payload kind is reachable from a runnable example.

**Acceptance Criteria:**

**Given** the **XP orbs to a bar** scene,
**When** played,
**Then** orbs stream into a level bar and the bar advances on Arrival events (FR-24).

**Given** the **3D mesh collect** scene,
**When** played,
**Then** a `Mesh` Payload effect runs in `World3D` Space (FR-24, FR-7, FR-4).

**Given** the **particle burst** scene,
**When** played,
**Then** a `Particle` Payload effect runs and raises CPU-side Arrival events (FR-24, FR-7).

**Given** all three scenes,
**When** they run,
**Then** each is standalone and emits no console errors or warnings (FR-24).

### Story 10.4: Moving-target and cross-Space

As a **developer**,
I want scenes for the two cases that break naive implementations,
So that the package's two hardest structural claims are demonstrable, not just asserted.

**Acceptance Criteria:**

**Given** the **moving-target** scene,
**When** played,
**Then** the Destination moves continuously and in-flight items arrive at its **current** position (FR-24, FR-12).

**Given** the **cross-Space** scene,
**When** played,
**Then** a world-space Origin throws items to a Canvas Destination (FR-24, FR-5),
**And** the scene demonstrates the case **without any caller-side coordinate conversion**.

**Given** both scenes,
**When** they run,
**Then** each is standalone and emits no console errors or warnings (FR-24).

**Given** the seven-scene set,
**When** it is counted,
**Then** it is **exactly seven** — coin→wallet, floating score text, XP orbs, 3D mesh collect, particle burst, moving-target, cross-Space — **not** the 48-cell matrix (FR-24).

### Story 10.5: The scene coverage check

As a **package maintainer**,
I want an automated check that every advertised kind is reachable from a scene,
So that "the examples are the documentation" stays true as kinds are added.

**Acceptance Criteria:**

**Given** the seven scenes,
**When** the coverage check runs,
**Then** each of the **3 Spaces**, **4 Payloads**, and **4 Path kinds** is exercised by **at least one** scene (FR-24, SM-5),
**And** **any kind not reachable from a scene is reported as a gap** and fails the check (FR-24).

**Given** a newly added Space, Payload, or Path kind with no scene,
**When** the check runs,
**Then** it **fails** — proving the check measures something.

**Given** the check,
**When** it runs,
**Then** it runs in a **headless/batch-mode** run (FR-25).

---

## Epic 11: The Allocation Matrix and the Test Gate

The headline constraint, enforced rather than documented. This epic does **not** own testing generally — AD-3 forbids deferring allocation proof, so per-area tests ship inside the epic that creates the code under test. E11 owns only what cannot exist before every axis does. **FRs:** FR-18, FR-25. **ADs:** AD-3, AD-13, AD-14. **NFRs:** NFR-1. **Metrics:** SM-1, SM-5.

### Story 11.1: The allocation harness and the `using Is` alias audit

As a **perf owner**,
I want the allocation harness pinned to the right API and guarded against the shadowing trap,
So that I never ship a green suite that asserts nothing.

**Acceptance Criteria:**

**Given** the allocation harness,
**When** its API is inspected,
**Then** it uses **`UnityEngine.TestTools.Constraints`** — `Assert.That(() => { … }, Is.Not.AllocatingGCMemory())` — from the Test Framework already in the manifest (AD-3),
**And** the **Performance Testing package is not required** and is not added (AD-3).

**Given** AD-3's named gotcha,
**When** an EditMode test greps every test file containing `AllocatingGCMemory`,
**Then** each **must** contain `using Is = UnityEngine.TestTools.Constraints.Is;` (or use the fully-qualified name), and the audit **fails** otherwise (AD-3, FR-25),
**And** this audit exists because without the alias **NUnit's `Is` silently wins and the test passes while measuring nothing** — *a green allocation suite that asserts nothing is worse than no suite*.

**Given** the audit,
**When** a test file deliberately omits the alias,
**Then** the audit **fails** — proving the audit measures something.

**Given** a deliberately allocating fake,
**When** the harness measures it,
**Then** the harness **reports a failure** — proving the harness itself measures something.

### Story 11.2: The full Space × Payload × Path allocation matrix

As a **perf owner**,
I want every combination the package claims asserted at 0 B per frame,
So that the headline constraint is a gate, not a slogan — SM-1, UJ-5.

**Acceptance Criteria:**

**Given** the claimed matrix,
**When** the tests enumerate it,
**Then** **every Space × Payload × Path combination the package claims** is covered — 3 Spaces × 4 Payloads × 4 Path kinds (FR-18),
**And** the enumeration is derived from the registered kinds so a newly added kind **automatically** enters the matrix rather than being silently omitted.

**Given** each matrix cell,
**When** an Effect is played and ticked for N frames **after warm-up**,
**Then** **Per-Frame Allocation is asserted at 0 B** and the test **fails the build otherwise** (FR-18, NFR-1, SM-1).

**Given** the measurement window,
**When** frames are sampled,
**Then** measurement covers frames **after the Play frame**, so bounded Per-Play Allocation **does not mask or break** the per-frame assertion (FR-18, PRD Glossary).

**Given** CI time budget,
**When** the full matrix proves too slow,
**Then** the documented fallback is a **representative subset per axis in CI with the full matrix nightly** (FR-18). `[ASSUMPTION: PRD FR-18 and Open Question 7 tag the full matrix as affordable, with this as the named fallback. Unresolved until CI timings exist.]`

**Given** AD-3's shipping rule,
**When** this story is reviewed against E2–E7,
**Then** it **assembles** matrix entries those epics already shipped rather than introducing allocation testing for the first time — new tick-path code shipped with its entry passing, per AD-3.

### Story 11.3: Per-Play constancy and the warm-up measurement

As a **perf owner**,
I want Per-Play allocation proven constant with respect to Item count and warm-up measured rather than hidden,
So that per-Item allocation cannot hide inside setup.

**Acceptance Criteria:**

**Given** a **10-Item** and a **200-Item** Effect,
**When** Per-Play Allocation is measured for each,
**Then** the two allocate **the same bytes at Play** (FR-18, NFR-1, PRD §9.3),
**And** this is the assertion that **catches per-Item allocation hiding inside setup** — asserted byte-for-byte, not as a threshold.

**Given** AD-10,
**When** the constancy test runs against Emission,
**Then** it passes **because** per-item delay and scatter are computed from `(seed, index)` and no per-item collection is built at play time — the obvious `List<float>`/`List<Vector3>` implementation would fail this test.

**Given** **warm-up** allocation (pool fill, definition load, first-time caches),
**When** it is measured,
**Then** it is **explicitly outside the budget and measured separately rather than hidden** (FR-18, NFR-1),
**And** the test reports warm-up bytes as an informational figure, not a pass/fail threshold.

**Given** the async path,
**When** Per-Play constancy is measured,
**Then** the Story 7.3 assertion is included in this suite — the UniTask awaiter is the only permitted contributor and does not scale with Item count (FR-15, §9.3).

### Story 11.4: Event-ordering assertions and the headless gate

As a **perf owner**,
I want the whole suite to run headless and the FR-14 guarantees asserted explicitly,
So that CI is the enforcement point rather than a developer's memory.

**Acceptance Criteria:**

**Given** FR-14's ordering guarantees,
**When** the suite runs,
**Then** each is **asserted explicitly**: `FirstItemArrived` exactly once and before `Completed`; `ItemArrived` count equals Item count; exactly one of `Completed`/`Cancelled`; `Started` before any `ItemSpawned` (FR-25, FR-14).

**Given** stagger with an overtaking scattered Item,
**When** the effect runs,
**Then** `FirstItemArrived` fires on the **genuine first Arrival — which may not be the first Item spawned** (FR-26, AD-13),
**And** this has its own named test: *it is the case most likely to be implemented wrong*.

**Given** AD-13's Arrival definition,
**When** the implementation is audited,
**Then** Arrival is **`t >= 1` and only `t >= 1`** — never distance-to-Destination (AD-13),
**And** a test with a **moving Destination** asserts the two definitions would diverge, and that the `t >= 1` definition is the one implemented.

**Given** the test layout,
**When** it is audited,
**Then** **EditMode covers pure math with no scene**; **PlayMode covers integration and the allocation matrix** (Spine §Consistency Conventions, FR-25),
**And** Path, Easing, and Space Adapter math is tested in EditMode with no scene (FR-25).

**Given** CI,
**When** the full suite runs in **headless/batch mode**,
**Then** **all tests pass** (FR-25),
**And** a failing allocation assertion **fails the build** (FR-18, SM-1, UJ-5).

---

## Validation Summary

Mechanically verified against the document body (not asserted by hand):

- **FR coverage: 28/28.** Every FR-1..FR-28 is cited by at least one story, and every FR in the Coverage Map resolves to stories in its owning epic.
- **AD coverage: 17/17.** Every AD-1..AD-17 is cited as a binding constraint by at least one story.
- **NFR coverage: 6/6.** Every NFR-1..NFR-6 is cited.
- **56 stories across 11 epics** (3 / 9 / 7 / 3 / 7 / 6 / 3 / 6 / 3 / 5 / 4).
- **No forward dependencies.** Every story depends only on earlier stories in its epic and earlier epics. Two orderings exist specifically to hold this: Story 2.7 (release exit) precedes Story 2.8 (events) so that AD-9's "only place `Completed`/`Cancelled` are raised" is satisfiable without a circular dependency; and Epic 4 (endpoints) precedes Epic 5 (spaces) so an end-to-end Space test has a provider to resolve from.
- **Epic independence.** Each epic is verifiable at its own boundary. E2 is verifiable against in-test fakes of the seven ports with no scene, which is what AD-1's hexagonal structure was bought for — without it, E2 could not close before E5/E6 and the sequence would collapse into one undeliverable epic.
- **File-churn check.** FR-1/19/20/21 were **deliberately consolidated** into Epic 8 rather than split into four epics, because they are the same files (the asset, its `[SerializeReference]` drawer, its gizmo) with no feedback loop between them. Epics 2–6 target distinct folders (`Core/Simulation`, `Core/Paths`, `Unity/Endpoints`, `Unity/Spaces`, `Unity/Payloads`) and are split on genuine risk boundaries, not layers.
- **Starter template.** The Architecture Spine specifies no external starter, but does specify an exact UPM layout and assembly graph. That layout **is** Epic 1 Story 1, per the step-04 rule.
- **No upfront bulk creation.** Each story creates only what it needs. The seven ports land together in Story 2.1 because they are one contract that the kernel and every adapter compile against — splitting them would create forward dependencies in every subsequent story.

## Open Questions and Conflicts

This document was produced **headless**. This section is where review should start.

### Conflicts found in the source documents

1. **AD-1 vs AD-15 — `Transform` in `A2BKit.Core`. This is a real contradiction in the Spine, not an ambiguity.** AD-1 states Core's "only permitted Unity surface is value math (`Vector2/3/4`, `Quaternion`, `Mathf`, `Color`, `AnimationCurve`)" — a list that does **not** include `Transform`. But AD-15 places `Transform Root` and `ApplyToTransform(Transform t, in A2BVisualState s)` on `IA2BSpaceAdapter`, and the Spine's own layer table puts the ports in `A2BKit.Core`. As written, AD-15's port **cannot be declared where the Spine says it lives**. Story 2.1 resolves in favour of `UnityEngine.Transform` being permitted (it is in `UnityEngine.CoreModule`, carries no UI/render-pipeline coupling, and the alternative is deleting AD-15's port from Core). **Needs an explicit ruling** — the alternative resolution (ports split across assemblies, or `ApplyToTransform` moved behind an opaque item handle) would change Stories 2.1, 5.1, and every adapter story.

2. **World2D's Z rule is undefined.** The PRD names `World2D` as one of exactly three Spaces (Glossary, and the assumption it flags as "the cheapest to fix now and the most expensive later"), but **no FR or AD states how World2D differs from World3D**. If Z is free, World2D is behaviourally identical to World3D and does not warrant a separate Space or adapter. Story 5.3 assumes Z pins to the adapter Root's Z. **Needs confirmation** — this is the assumption the PRD itself flagged as highest-leverage, and it remains unresolved at the story layer.

3. **FR-22's non-allocating overlay is the hardest AD-3 target in the package.** AD-3 bans `ToString()` on value types and all string interpolation in the tick path; FR-22 requires the overlay to display live numeric counters at zero per-frame allocation. Satisfying both requires a non-allocating number-to-char-buffer writer — a real component nobody has scoped. Story 9.3 assumes it is achievable (as the PRD does). **If it is not affordable, the honest fallback is an overlay that allocates and is documented as debug-only**, which the PRD explicitly resists ("a diagnostic that violates the package's own headline constraint would be embarrassing").

### Assumptions made while drafting (tagged inline)

- **Story 3.2** — Bezier arc exposed as **arc height + direction vector**. The PRD tags the parameterization itself as an assumption.
- **Story 4.3** — Destroyed-endpoint default policy is **cancel-and-release** (PRD Assumptions Index).
- **Story 5.3** — Per-Effect sorting config suffices for mixed-Space draw order; no global sorting authority (PRD Assumptions Index).
- **Story 5.5** — Dedicated A2BKit Canvas is the right default, overridable (PRD + AD-14 both tag this).
- **Story 5.6** — Behind-camera Origins **clamp/suppress rather than throw** (PRD Open Question 5, unresolved).
- **Story 6.4** — `Text` content is set **at Play**, not recomputed per frame; numeric conversion happens off the tick path. FR-7 only requires per-Item content set at Play, so this is compatible — but it forecloses a live-counting label.
- **Story 6.5** — `Particle` payload reuses a persistent pre-sized particle buffer. The PRD requires zero per-frame allocation but names no mechanism, and Unity's `GetParticles`/`SetParticles` are the obvious allocation trap here.
- **Story 9.2** — Pool exhaustion defaults to **grow-with-warning** (PRD Open Question 6, unresolved).
- **Story 11.2** — The full 48-cell matrix fits the CI budget, with representative-subset-plus-nightly as the named fallback (PRD Open Question 7, unresolved).
- **Overview** — Editor-tooling UX is adequately specified by FR-19..FR-22; no separate UX design contract is warranted.

### Structural decisions taken headless (would normally be confirmed)

- **Epic 1 delivers no consumer-visible capability.** This is the one epic that would read as "technical layering" under the step-02 rule. Kept because AD-1 is a *shipped, tested constraint* and the enforcement point for NFR-6/SM-4 — deferring it means every later epic writes code against an unenforced boundary, which is the exact failure AD-1 exists to prevent. Kept deliberately minimal (3 stories).
- **Four FRs are split across epics** (FR-17, FR-18, FR-23, FR-25), each justified in the Coverage Map. The FR-18 split is the load-bearing one: **AD-3 forbids deferring allocation proof to a test epic** ("new code in the tick path ships with its FR-18 matrix entry passing, or it does not ship"), so Epic 11 assembles and gates what Epics 2–7 already shipped rather than introducing allocation testing for the first time. Reading Epic 11 as "where the allocation work happens" would violate AD-3.
- **Endpoints (E4) split from Spaces (E5)** rather than merged, on the AD-4 division of labour. Merging would give a 6-FR, ~10-story epic; splitting keeps both independently verifiable.

### Inherited from the PRD, still unresolved (do not block this breakdown)

1. **What is the reference device?** NFR-2/SM-2 are unmeasurable until "mid-tier Android" names a real phone. No story asserts NFR-2's 60 fps figure for exactly this reason — the Spine also defers the perf harness. FR-18's allocation gate is device-independent and lands regardless.
2. **Is 200 concurrent Items the right ceiling?** A wrong ceiling mis-sizes pool defaults (Stories 2.7, 6.1).
3. **Is Asset Store publication genuinely out for v1?** The PRD calls this its most expensive assumption. If it is in, API stability and docs become v1 requirements **now** and this breakdown needs a documentation epic it does not currently have.
4. **Is Unity 6000.5 the right floor**, or should the package target an earlier LTS? Story 1.1 hard-codes `6000.5`.
5. **Multi-hop paths** — deferral confirmed in the Spine; AD-13's two-endpoint context must stay compatible. No story hard-codes two endpoints below the port.
