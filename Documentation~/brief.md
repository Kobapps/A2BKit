---
title: "Product Brief: A2BKit"
status: draft
created: 2026-07-17
updated: 2026-07-17
---

# Product Brief: A2BKit

> Drafted headless from a goal statement, without a live elicitation session. Inferences are tagged `[ASSUMPTION]` and are the first things to correct on review.

## Executive Summary

A2BKit is a Unity 6 package for **A-to-B effects**: the burst of coins that flies from a chest into the wallet HUD, the score text that floats up off a kill, the XP orbs that stream into a level bar. Nearly every f2p game ships this moment, and most teams rebuild it from scratch — as a pile of tween sequences, ad-hoc pools, and camera-space math that works until the target starts moving or the counter needs to know when the first coin actually landed.

The mechanic looks varied but is uniform underneath. Whether the payload is a sprite, a mesh, a particle, or a text label, and whether it travels in world space or across a canvas, the shape is identical: *spawn N items at A, move them to B along some path over some time, and tell me when things happen.* A2BKit treats that shape as the product — one configuration model and one runtime for every combination of space, payload, and path.

It is worth building because the alternatives are thin. The paid Asset Store flyout packages we surveyed are unrated, single-pipeline, and unmaintained, while the strongest free option — Coffee UIParticle's `UIParticleAttractor` — is excellent but UI-only, so it cannot serve the 3D half of the problem. And the hard parts here are not the motion; they are **allocation, endpoint tracking, and event timing**, which are exactly what the ad-hoc implementations get wrong.

## The Problem

A gameplay or UI developer wiring up a reward moment hits the same wall every time:

- **It allocates.** The common recipe is a DOTween sequence per item, and DOTween allocates about 730 B on each animation start. Multiply by a 50-coin burst on a mid-tier Android phone and the reward moment — one of the most-repeated moments in the game — is also one that stutters. The usual workaround, caching tweens, leaves them ticking every frame forever.
- **The endpoint moves.** The wallet HUD bounces, the canvas is anchored, the camera pans. Precompute the destination once and the coins land next to the target instead of on it.
- **Coordinate conversion is a minefield.** Converting between world, screen, and canvas space has several non-obvious failure modes — including a documented Unity bug and an API that cheerfully returns valid-looking coordinates for objects behind the camera. Every team rediscovers them. (Specifics in `addendum.md` §3.)
- **The events are missing.** The counter roll-up should start when the *first* item lands — not when the burst starts, not when it ends. Ad-hoc implementations rarely expose that seam, so the number and the visual drift apart.
- **It resists testing and debugging.** Time-based, frame-coupled, coroutine-driven code with no seam for injecting time is verified by launching the game and squinting.

The cost of the status quo is not that it cannot be done, but that it is redone per project, per space, and per payload — and each rebuild re-earns the same bugs.

## The Solution

A single configurable effect definition, authored as an asset and driven by a pooled runtime. Three ideas carry it:

- **Space, payload, and path compose independently.** Adding a path does not touch the spawner; adding a payload does not touch the motion.
- **Endpoints are live, not baked.** They resolve through a provider queried as the effect runs, so a moving wallet or a panning camera is the default case rather than the edge case.
- **Time is injected, and the seams are events.** Injected time makes motion verifiable in EditMode without entering play mode; the event set — with `FirstItemArrived` as a first-class hook — is what lets the counter stay in sync with the visual.

The performance target is not "fast enough" but a hard budget: **zero steady-state GC allocation** during playback, enforced by an automated allocation test rather than asserted in a README. That enforcement is the difference between this and a performance bullet point.

## Who This Serves

**Primary — the Unity gameplay/UI developer on a mobile-first f2p title.** They need the coin burst to look good, cost near-zero frame time, and land on a moving HUD, and they need it working today. Success is deleting their bespoke `CoinFlyController.cs` and configuring an asset instead. `[ASSUMPTION]`

**Secondary — the technical artist / designer.** They tune feel: arc height, spread, stagger, easing. Success is tuning it in the inspector with a live preview and never opening the script. `[ASSUMPTION]`

## Success Criteria

**Correctness and cost**
- Zero steady-state GC allocation during playback, enforced by an automated allocation test.
- 200 concurrent items hold 60 fps on a mid-tier Android device. `[ASSUMPTION]`
- Automated coverage across every space × payload × path combination the package claims to support.

**Adoption**
- A coin-to-wallet effect works from a standing start in under five minutes, without reading source.
- A new path type is added without modifying any existing class.
- Every advertised case ships as a runnable example scene.

## Scope

**Vocabulary.** Three **spaces**: `World3D`, `World2D`, and `Canvas`. Canvas covers all three render modes — Overlay, Screen-Space-Camera, and World-Space canvas. "World-space canvas" is therefore a Canvas render mode, not a fourth space. `[ASSUMPTION — resolves an ambiguity in the source goal, which listed "2d, 3d, world, canvas" as peers]`

**In, for v1**
- Spaces: `World3D`, `World2D`, `Canvas` (all three render modes).
- Payloads: sprite, mesh, particle, text.
- Paths: linear, bezier, procedural, custom.
- Live endpoint providers (moving A and B).
- Full event set: `Started`, `ItemSpawned`, `FirstItemArrived`, `ItemArrived`, `Completed`, `Cancelled`.
- UniTask async + cancellation.
- Pooling and caching throughout.
- Editor tooling: custom inspectors, gizmos, preview widgets, and a runtime debug overlay reporting active effects and pool occupancy.
- Example scenes for every case; EditMode + PlayMode tests including the allocation guard.

**Explicitly out**
- **Third-party tween/UI dependencies in the core.** No DOTween, no Coffee UIParticle — both because a dependency is a tax on adopters and because DOTween's allocation profile is the problem being solved. Coffee UIParticle is a candidate optional adapter.
- **VFX Graph payloads.** GPU-simulated particles cannot raise CPU-side arrival events and do not render into a Canvas — the two things this package is for. VFX Graph remains suitable for ambient sparkle only; revisit later.
- **Asset Store packaging, marketing, and store art.** `[ASSUMPTION]`
- **Non-URP render pipeline validation.** Built pipeline-agnostic where free, validated on URP only.
- **Netcode, save/load, or currency/economy logic.** A2BKit animates the reward; it does not own the number.

## Vision

The mechanic generalizes well beyond coins: any *transfer* the player must see and believe — inventory items to a bag, cards to a hand, resources to a depot, damage numbers off a target — is the same A-to-B shape with different dressing. The plausible three-year end state is a small, reliable package covering that whole family, with an Asset Store release as the distribution step once the internal version has earned it.
