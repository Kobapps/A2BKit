---
title: "A2BKit Brief — Addendum"
status: draft
created: 2026-07-17
updated: 2026-07-17
---

# A2BKit Brief — Addendum

Depth that does not belong in the brief but that the PRD and architecture workflows will need. Sourced from a Discovery web-research pass on 2026-07-17; every claim below is either verified against a cited source or tagged as an assumption.

## 1. Competitive landscape (detail)

| Option | License / price | Relevance | Limitation that matters to us |
|---|---|---|---|
| [Coffee `UIParticle` / `UIParticleAttractor`](https://github.com/mob-sakai/ParticleEffectForUGUI) | MIT, v4.13.3 (Jul 2026) | Closest prior art. Ships destination radius, delay rate, max speed, Linear/Smooth/Sphere movement, and an `OnAttracted` event | **UI-only.** Cannot serve 3D/world-space. UI shaders only; 65,535 vertex mesh cap; zero `localScale` skips rendering |
| [Feel / MMFeedbacks](https://feel.moremountains.com/) | ~$50 | General game-feel, 150+ stackable feedbacks | Not an A→B system — the fly-to-target is still hand-written |
| [DOTween (Pro)](https://dotween.demigiant.com/) | $7.50 Pro | The de-facto motion layer teams reach for | **~734 B per animation start, ~584 B per delay.** This is the allocation problem we exist to solve |
| [PrimeTween](https://github.com/KyryloKuzyk/PrimeTween) | Free, zero-alloc | Proves zero-alloc tweening is achievable | Generic tween lib, not an A→B model; still a third-party dependency |
| [Damage Numbers Pro](https://assetstore.unity.com/packages/2d/gui/damage-numbers-pro-186447) | Paid | Floating text, done well | Text-only; solves one of our four payloads |
| [Dynamic Currency Flyout UI](https://assetstore.unity.com/packages/tools/gui/dynamic-currency-flyout-ui-seamless-in-game-currency-animation-290397) | $39.99 | Direct competitor | **URP-only, built on 2022.3, v1.0.1 (Dec 2024), zero ratings** |
| [CoinFX System](https://assetstore.unity.com/packages/tools/animation/coinfx-system-308265) | $15 | Direct competitor | v0.0.3 (Jul 2025), zero ratings |

**Read:** the paid direct competitors are thin, unrated, and stale. The strong free option is UI-only. The build decision rests on cross-surface coverage, not on out-featuring anyone.

**Corrections to prior assumptions:** "MagicLight FX" does not appear to exist. "Lofelt / Nice Vibrations" is haptics, not FX — [the repo was archived 2025-08-12](https://github.com/Lofelt/NiceVibrations) and pulled from the Asset Store; it folded into More Mountains/Feel. Neither is relevant prior art.

## 2. Unity 6 / URP technical constraints

These are verified facts the architecture must respect.

- **TextMeshPro.** `com.unity.textmeshpro` is **deprecated**; merged into [`com.unity.ugui` 2.0](https://docs.unity3d.com/Packages/com.unity.ugui@2.0/manual/TextMeshPro/index.html) as of 2023.2 and bundled with Unity 6. Already present in this project's manifest at `com.unity.ugui: 2.5.0`. **Namespace remains `TMPro`** — no code change, and no new dependency needed for the text payload.
- **Pooling.** [`UnityEngine.Pool.ObjectPool<T>`](https://docs.unity3d.com/6000.1/Documentation/ScriptReference/Pool.ObjectPool_1.html) is Unity's recommended default since 2021 LTS. Use it as the substrate. Note `collectionCheck: true` catches double-release but is **Editor-only** and costs performance — enable in Editor, disable in player builds.
- **UniTask.** Prefer MonoBehaviour's built-in `destroyCancellationToken` (2022.2+) over `GetCancellationTokenOnDestroy()`. Prefer `CancelAfterSlim` (zero-alloc, PlayerLoopTimer-based) over `CancelAfter`. Per [Cysharp's own guidance](https://github.com/Cysharp/UniTask/discussions/627), Unity's `Awaitable` is a *subset* of UniTask that still allocates and lacks `SuppressCancellationThrow()` and native `WhenAll`/`WhenAny` — UniTask is the correct choice for app-level code. It is already a project dependency, so the dependency cost is already paid.
- **VFX Graph.** Available in URP on Unity 6 (URP/HDRP only). **Deliberately out of scope:** GPU-simulated particles cannot cheaply feed CPU-side "arrived" events and do not render into a Canvas — the two capabilities that define this package. ParticleSystem (optionally + UIParticle) is the right substrate.
- **Canvas batching.** [Any drawable change re-runs batch building for every element on that Canvas.](https://unity.com/how-to/unity-ui-optimization-tips) Dynamic elements belong on their own sub-canvas — but [batches do not merge across Canvases](https://support.unity.com/hc/en-us/articles/115000355466-Split-canvas-for-dynamic-objects), so this is a rebuild-cost vs draw-call tradeoff to profile, not a free win. A2BKit should default to placing its spawned items on a dedicated canvas and make that overridable.
- **Testing.** `[UnityTest]` supports `IEnumerator` but **not** `async`. Bridge with `[UnityTest] public IEnumerator X() => UniTask.ToCoroutine(async () => { ... });` ([pattern reference](https://www.alexhall.org/2021/05/04/data-driven-async-tests-in-unity-with-unitytest-unitask/)). UTF 1.3+ adds async SetUp/TearDown. Project has `com.unity.test-framework: 1.7.0`.

## 3. Coordinate-conversion gotchas (must be encapsulated, not re-derived)

The single highest-value thing the package can hide from its users. Each of these is a real bug teams hit:

- **Overlay canvas:** pass **`null`** as the camera to `RectTransformUtility.ScreenPointToLocalPointInRectangle`. Passing a camera yields wrong positions — [a documented Unity issue](https://issuetracker.unity3d.com/issues/recttransformutility-screenpointtolocalpointinrectangle-returns-wrong-position-if-render-mode-is-set-to-screen-space-overlay).
- **Screen-Space-Camera canvas:** pass `canvas.worldCamera`. The canvas sits at `planeDistance`, so raw screen deltas are not canvas units.
- **World-Space canvas:** neither path applies — use `ScreenPointToWorldPointInRectangle` or direct world transforms.
- **Behind-camera:** `Camera.WorldToScreenPoint` projects through an infinite line, so objects *behind* the camera still return plausible on-screen coordinates. [Gate on `z > 0`](https://www.turiyaware.com/a-solution-to-unitys-camera-worldtoscreenpoint-causing-ui-elements-to-display-when-object-is-behind-the-camera/); `z` is world-unit distance and must fall between near and far clip.

**Design implication:** a `ISpaceAdapter` abstraction per render mode is not over-engineering — it is where these four bugs go to die, and it is directly testable.

## 4. Prevailing implementation pattern (what we are replacing)

DOTween sequence (`DOLocalMove` + `DOJump`/bezier + scale punch) on pooled UI Images; target from `Camera.WorldToScreenPoint` → `RectTransformUtility.ScreenPointToLocalPointInRectangle`. Recurring failures, in rough order of pain:

1. Per-item tween allocation on every burst (see §2).
2. Canvas rebuild storms from many moving drawables.
3. World→canvas conversion errors across render modes (§3).
4. Baked endpoints breaking when the HUD animates.
5. No "first item landed" signal, so counter roll-up desyncs from the visual.
6. Sorting chaos when 2D/3D/UI are mixed.

Items 1, 4, and 5 are the three the architecture must solve structurally rather than by documentation.

## 5. Open questions for the PRD

Carried forward rather than guessed:

- Concrete performance ceiling: is 200 concurrent items the right stress target, and which device is "mid-tier Android"? `[ASSUMPTION]` used in the brief.
- Is Asset Store publication genuinely out of scope, or a near-term goal that would change API-stability and documentation requirements now?
- Should the optional Coffee `UIParticle` adapter ship in v1 or be deferred?
- Does the team want a data-driven authoring asset (ScriptableObject) as the primary surface, a code-first fluent API, or both as peers?

**Resolved during drafting (recorded here so the PRD does not relitigate):**

- *Is "world" a space or a canvas render mode?* — **Resolved.** The source goal listed "2d, 3d, world, canvas" as peers, conflating two axes. The brief now defines three spaces (`World3D`, `World2D`, `Canvas`), with World-Space canvas as one of Canvas's three render modes. `[ASSUMPTION]` — reversible, but the whole adapter taxonomy hangs off it.
- *Is 2D SpriteRenderer-in-world or orthographic-camera world space?* — **Resolved as the former.** `World2D` means SpriteRenderer/sprite payloads on the XY plane; an orthographic camera is a camera setting that either world space supports, not a distinct space.
