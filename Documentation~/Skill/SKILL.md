---
name: a2bkit
description: >-
  Use when building A-to-B / fly-to-target / reward-collection effects with the A2BKit Unity package —
  coins flying to a wallet HUD, floating "+250" score text, XP orbs into a bar, items to an inventory
  slot, damage numbers, cards to a hand. Covers the API surface, the recommended patterns, the
  performance rules that keep it allocation-free, and the gotchas that are easy to hit. Load this
  before writing A2BKit code so you use the real API instead of guessing at it.
---

# A2BKit

A2BKit animates the mechanic *spawn N items at A, move them to B along some path over some time, and
raise events when things happen* — the coin burst that flies into the wallet, the score text that
floats off a hit, the XP orbs that stream into a level bar. One model covers 2D, 3D and Canvas space.

Namespaces: runtime API is `A2BKit.Unity`; the pure model (paths, easing, enums, the handle) is
`A2BKit.Core`. You almost always need both `using`s.

## Mental model — three things compose independently

- **Space** — where it plays: `A2BSpaceKind.World3D`, `World2D`, or `Canvas` (Canvas covers all three
  render modes, and cross-space works: a world chest → a UI wallet).
- **Payload** — what each item *is*: sprite, uGUI image, mesh, TMP text, particle, or a prefab.
- **Path** — how it travels: linear, arc, burst-then-gather, procedural, or your own.

Plus **emission** (how many, when, how scattered), **easing**, **feedback** (trails/impacts/audio),
and **events**. Changing one never forces a change to another.

## The fastest correct path

```csharp
using A2BKit.Core;
using A2BKit.Unity;

// Coins fly from a world chest to a UI wallet icon. Origin/Destination are plain Transforms;
// a RectTransform is auto-detected as a UI target — no camera math at the call site.
A2B.Play(coinEffectAsset, chest.transform, walletIcon);
```

`A2B.Play` returns an `A2BEffectHandle` (a struct — safe to ignore, copy, or keep). It **never throws**:
bad config logs one actionable error and returns an invalid handle.

`coinEffectAsset` is an `A2BEffectAsset` — create one via `Assets ▸ Create ▸ A2BKit ▸ Effect`, or the
**Tools ▸ A2BKit ▸ A2BKit Window**. Set its Space, pick a Payload, tune the Path in the inspector.

## Tune it visually — no play mode

**Tools ▸ A2BKit ▸ A2B Effect Editor** opens a visual editor for one asset. Assign the asset, set
Origin and Destination (a scene Transform, or a virtual point you drag with a Scene handle), then
Play / Pause / Loop or drag the **Time** slider to scrub. Playback and scrub both run the real
scheduler on the editor clock, so the frame you see is the frame the game shows. With **Show payload
visuals** on (default) the actual sprite/image/mesh draws in both the Scene and the Game view with no
play mode — it runs the shipping presenter on a throwaway `DontSave` stage; turn it off for lightweight
motion dots. A Bezier path also gets a **draggable arc handle** in the Scene — grab the control point
and `ArcHeight`/`ArcDirection`/`ArcBias` follow. A **spline path** (`A2BSplinePath`) gets a handle per
control point, with **+**/**−** to add and remove points in the Scene. Edit the definition inline and it
updates live. This is the fastest way to dial in Path, Easing, Emission stagger and scatter before
wiring anything up.

For a multi-bend trajectory use **`A2BSplinePath`** — a Bézier over any number of control points, still
pinned to both endpoints. Its offsets are fractions of the endpoint distance, so the same curve arcs
visibly in world space AND on a Canvas (a plain `ArcHeight` is in working units, which are pixels on a
Canvas — set it in the hundreds there, or use the spline). To make an arc read as depth, raise
`ArcLiftScale`: items grow in proportion to how far they bulge off the straight line — a coin popping up
toward the camera and settling on arrival. It's off by default and works in any space.

**Scale** has two sources that multiply: `ScaleOverProgress` (an animation curve — scale over the
duration) and, if you enable `ScaleFromPathDepth`, the path's **Z** channel (its depth, as a fraction of
the endpoint distance, `× PathDepthScaleStrength`). On a 2D/Canvas effect Z moves nothing visible, so a
spline's Z offsets become a scale curve you sculpt with the same handles — the item scales instead of
drifting in depth.

**Burst timing:** `A2BBurstEmission` releases items `AllAtOnce`, on a `FixedStagger`, or
`SpreadOverDuration`. Give the spread a shape with `ReleaseEasing` (any `IA2BEasing`) — ease-out
front-loads the spray, ease-in-out softens it. In the A2B Effect Editor the origin shows an editable
**scatter/burst radius** ring; drag it to size the spread.

## Two authoring surfaces, both peers

**Asset** (designer-facing): configure an `A2BEffectAsset` in the inspector, play it with `A2B.Play`.

**Code** (`A2BEffectBuilder` → `A2BEffectSpec`): same one-liner, no asset.

```csharp
// Cache the spec — building it allocates and defeats pool sharing. Never build per-play.
_coins ??= A2BEffectBuilder.Create()
    .Count(12, 20).Stagger(0.03f).Scatter(0.6f)   // 12–20 coins, staggered, spread at the origin
    .Arc(height: 2f).Ease(A2BEaseKind.InOutCubic) // a visible lob
    .Duration(0.8f)
    .AsSpec(new A2BImagePayloadRenderer { Sprite = coinSprite }, A2BSpaceKind.Canvas)
    .Feedback(new A2BTrailFeedback())             // world-space only — see gotchas
    .Feedback(new A2BImpactFeedback { Prefab = sparkPrefab });

A2B.Play(_coins, chest, walletIcon);
await A2B.PlayAsync(_coins, chest, walletIcon, cancellationToken: destroyCancellationToken);
```

Or drop an **`A2BEffectPlayer`** component on a GameObject, assign the asset + Origin + Destination in
the inspector, and wire its UnityEvents — zero code. Call `player.Play()` (or `Play("+250")` /
`Play(250f)` for text payloads). `PlayOnEnable` makes it fire automatically.

## The event you actually care about: FirstItemArrived

The counter roll-up must start when the **first** coin *lands* — not when the burst starts (the number
finishes before anything arrives) and not when it ends (the reward reads as already over). That timing
is the whole reason the package exists, so `FirstItemArrived` is a first-class hook.

Allocation-free path — implement `IA2BEffectListener` on a **reusable** object (a class, so it never
boxes) and add it to the handle:

```csharp
sealed class Wallet : A2BEffectListenerBase   // base leaves every hook empty; override what you need
{
    public override void OnFirstItemArrived(in A2BEffectHandle h, int i) => _counter.BeginRollUp();
    public override void OnItemArrived(in A2BEffectHandle h, int i) => _counter.Add(1);
}

var handle = A2B.Play(coinEffect, chest, walletIcon);
handle.AddListener(_wallet);   // reuse one instance — do not `new` a listener per play
```

Full event set, in order: `Started` → `ItemSpawned` → `FirstItemArrived` (exactly once) →
`ItemArrived` (once per item) → exactly one of `Completed` / `Cancelled`.

`A2BEffectPlayer` exposes the same events as inspector-wired `UnityEvent`s (convenient, but UnityEvent
dispatch is not allocation-free — use `IA2BEffectListener` for the hot path).

## Option quick-reference

| Axis | Options |
| --- | --- |
| Space | `World3D`, `World2D`, `Canvas` (+ cross-space) |
| Payload | `A2BSpritePayloadRenderer`, `A2BImagePayloadRenderer` (uGUI), `A2BMeshPayloadRenderer`, `A2BTextPayloadRenderer` (TMP), `A2BParticlePayloadRenderer`, `A2BPrefabPayloadRenderer` |
| Path | `.Linear()`, `.Arc(height, bias, jitter)`, `.BurstThenGather(radius, burstFraction, hold)`, `.Spiral(amplitude, frequency)`, `.Path(new A2BSplinePath{…})` (multi-point Bézier), or `.Path(myPath)` |
| Easing | `.Ease(A2BEaseKind.X)` — 21 kinds — or a custom `IA2BEasing` |
| Emission | `.Count(n)` / `.Count(min,max)`, `.AllAtOnce()` / `.Stagger(interval)` / `.SpreadOver(seconds)`, `.Scatter(radius)`; shape the spread with `A2BBurstEmission.ReleaseEasing` |
| Scale | `ScaleOverProgress` (curve over duration) × `ScaleFromPathDepth` (path Z, `× PathDepthScaleStrength`) × `ArcLiftScale` (grow off the chord) — all optional, they multiply |
| Feedback | `A2BTrailFeedback`, `A2BImpactFeedback` (on-hit), `A2BSpawnPopFeedback`, `A2BAudioFeedback` |
| Endpoints | pass a `Transform` (auto: `RectTransform`→UI), or an `IA2BEndpointProvider` for anything custom |

## Recipes

**Burst-then-gather** (the two-beat reward — pop outward, hang, get pulled in). This is *not* an arc
with big scatter: scatter moves where a coin *starts*, so an arc never reverses; a burst does.

```csharp
_burst ??= A2BEffectBuilder.Create()
    .Count(20, 30).AllAtOnce()                    // a chest pops once — do not stagger a burst
    .BurstThenGather(radius: 190f, burstFraction: 0.34f, hold: 0.14f)
    .Ease(A2BEaseKind.Linear)                     // the path eases each beat internally; don't fight it
    .Duration(1.15f)
    .AsSpec(new A2BImagePayloadRenderer { Sprite = coinSprite });
```

**Floating score text** — one label, value formatted without allocating:

```csharp
_score ??= A2BEffectBuilder.Create()
    .Count(1).Linear().Ease(A2BEaseKind.OutCubic).Duration(1.1f)
    .ColorOverProgress(fadeOutGradient)
    .AsSpec(new A2BTextPayloadRenderer { FontSize = 54f }, A2BSpaceKind.Canvas);

A2B.Play(_score, hitPoint, floatTarget, text: "+250");   // or pass value: 250f
```

**Moving target** — the destination can move *while items are in flight*; they still land on it,
because endpoints resolve every frame. There is nothing extra to do — just move the destination
Transform. (Caching the destination position at play time is the classic bug: coins land where it
*was*.)

## The rules that matter

1. **Never allocate per frame.** Anything reachable from the tick — a custom `IA2BPath.Evaluate`, an
   `IA2BFeedback.OnItemUpdated`, an `IA2BEndpointProvider.Resolve` — runs per item per frame. No LINQ,
   no closures, no `GetComponent`, no boxing, no string work. Prove it, don't assume it:
   ```csharp
   using Is = UnityEngine.TestTools.Constraints.Is;   // MANDATORY alias — NUnit's Is otherwise
                                                       // shadows it and the test measures nothing
   Assert.That(() => scheduler.Tick(), Is.Not.AllocatingGCMemory());
   ```
2. **`StringBuilder.Append(int)` allocates on Unity's Mono** (it routes through `ToString()`). For
   per-frame numbers, emit digits by hand into a reused `StringBuilder` and hand it to TMP via
   `SetText(sb)`. Text payloads already do this; your own overlays must too.
3. **Nothing throws.** No A2BKit call throws for a runtime or config fault — it logs and degrades. Your
   custom paths/feedbacks/providers must do the same (`A2BLog.Error(context, msg)`), or you break the
   guarantee that a cosmetic system can't take down gameplay.
4. **Cache the spec/definition; play it forever.** The builder is setup code, not tick code.
5. **Handles are safe to misuse.** An `A2BEffectHandle` is a struct with a generation stamp: holding
   one past completion, copying it, or double-cancelling are all no-ops. `handle.IsValid` tells you if
   it still refers to a live effect.

## Extending it — no shipped file gets edited

Paths, easings, emissions, payloads and feedbacks are `[SerializeReference]` fields, so the inspector's
type picker finds your implementation automatically — just write the class (a **class**, never a
struct: a struct behind an interface boxes every call and silently breaks the allocation budget).

```csharp
[System.Serializable]
public sealed class ZigZagPath : IA2BPath
{
    public float Amplitude = 1f;
    public int Zigs = 4;
    public Vector3 Evaluate(in A2BPathContext ctx, float t)
    {
        Vector3 straight = Vector3.LerpUnclamped(ctx.Origin, ctx.Destination, t);
        float envelope = Mathf.Sin(t * Mathf.PI);          // 0 at both ends — REQUIRED, see below
        return straight + Vector3.right * (Mathf.Sin(t * Zigs * Mathf.PI * 2f) * Amplitude * envelope);
    }
}
```

A path **must land on both endpoints**: `Evaluate(ctx,0)==Origin` and `Evaluate(ctx,1)==Destination`.
Arrival is defined as `t>=1`, so a path that drifts off its destination makes `FirstItemArrived` fire
for a coin that visibly isn't there. Assert it with the shipped contract:

```csharp
Assert.IsTrue(A2BPathConformance.SatisfiesEndpointInvariant(new ZigZagPath(), in ctx));
```

Custom feedbacks derive from `A2BFeedbackBase`; custom endpoints implement `IA2BEndpointProvider`.
Space adapters are the one exception — `Space` is an enum, so a custom space adapter installs through a
factory: `A2BAdapters.SetFactory(A2BSpaceKind.Canvas, myFactory)`, or per-asset via its Space Override.

## Gotchas / anti-patterns

- **UniTask is required and installed separately.** A2BKit references it, but a package manifest can't
  declare a Git-package dependency — so if `Cysharp.Threading.Tasks` won't resolve, install UniTask
  first: `https://github.com/Cysharp/UniTask.git?path=src/UniTask/Assets/Plugins/UniTask`.
- **Trails don't work on a Canvas, and no setting fixes it.** `TrailRenderer` is a world-space mesh
  renderer; a screen-space Canvas won't draw one. Use trails in World2D/World3D. For a canvas impact,
  set `A2BImpactFeedback.Prefab` to a uGUI object rather than using the `Sprite` fallback (which builds
  a `SpriteRenderer`, same problem).
- **Don't chain two effects for burst-then-gather.** Use `A2BBurstGatherPath` / `.BurstThenGather()`.
  Chaining means inventing an "arrival" for a coin that merely stopped, and losing `FirstItemArrived`.
- **Don't mutate an asset's strategy at runtime.** `((A2BBurstEmission)asset.Definition.Emission).MinCount = 50`
  edits the asset on disk and every effect using it. Use `A2BEffectBuilder.From(asset.Definition)` — it
  copies on write.
- **Don't `new` a listener per play** and don't capture a lambda for the hot path — reuse an
  `IA2BEffectListener` instance.
- **A2BEffectPlayer's UnityEvents are safe from code** (initialized, not null) — `player.OnItemArrivedEvent
  .AddListener(...)` works even on a component you just `AddComponent`'d.

## Where to look

- Package README — install + quick start.
- `Documentation~/extending.md` — the full extension guide.
- Package Manager ▸ A2BKit ▸ Samples ▸ **A2BKit Examples** — nine authored scenes; open one, press Play.
- **Tools ▸ A2BKit ▸ A2B Effect Editor** — preview and scrub an effect in the Scene, no play mode.
