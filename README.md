# A2BKit

A-to-B reward and feedback effects for Unity 6: coins flying to a wallet HUD, floating score text, XP orbs streaming into a level bar.

Every f2p game ships this moment, and most teams rebuild it per project as tween sequences, ad-hoc pools, and camera-space math that breaks the moment the target starts moving. A2BKit treats the shape as the product — *spawn N items at A, move them to B along some path, and tell me when things happen* — with one model across 2D, 3D and Canvas space.

## Why this exists

The hard parts of this mechanic are not the motion. They are:

- **Allocation.** The usual recipe allocates per item per burst; A2BKit holds a **zero per-frame allocation** budget, enforced by an automated test rather than a README claim.
- **Moving endpoints.** The wallet HUD bounces, the camera pans. Endpoints resolve *every frame*, so a moving target is the default case.
- **Event timing.** The counter roll-up should start when the **first** item lands — not when the burst starts, not when it ends. `FirstItemArrived` is a first-class hook.

## Install

**Install UniTask first.** A2BKit requires it, and a package manifest can only declare *registry*
dependencies — UniTask ships as a Git package, so it cannot be listed and pulled in automatically.
Add it via **Package Manager → + → Add package from git URL**:

```
https://github.com/Cysharp/UniTask.git?path=src/UniTask/Assets/Plugins/UniTask
```

Then add A2BKit the same way:

```
https://github.com/Kobapps/A2BKit.git
```

`com.unity.ugui` is a normal dependency and resolves itself. It supplies TextMeshPro (namespace
`TMPro`) — no separate TMP package is needed on Unity 6.

Requires **Unity 6000.0+**. Built pipeline-agnostic where free; validated on URP.

## Samples

**Package Manager → A2BKit → Samples → A2BKit Examples → Import.** Eight scenes: open one and press
Play. They are authored scenes, not scripts that build a world — select the Effect Player in any of
them and the wiring is right there in the inspector.

## Quick start

Create an effect: `Assets > Create > A2BKit > Effect`. Set Space, pick a Payload, tune the path. Then either drop an `A2BEffectPlayer` on a GameObject and wire Origin/Destination in the inspector, or:

```csharp
using A2BKit.Unity;

// Coins fly from the chest to the wallet HUD.
A2B.Play(coinEffect, chest.transform, walletIcon);
```

Await it:

```csharp
var reason = await A2B.PlayAsync(coinEffect, chest.transform, walletIcon,
                                 cancellationToken: destroyCancellationToken);
if (reason == A2BCompletionReason.Completed) ShowLevelUpPanel();
```

The two-beat reward — coins **burst outward**, hang, then the wallet drags them in:

```csharp
_burst ??= A2BEffectBuilder.Create()
    .Count(20, 30).AllAtOnce()                       // a chest pops once; don't stagger a burst
    .BurstThenGather(radius: 190f, burstFraction: 0.34f, hold: 0.14f)
    .Ease(A2BEaseKind.Linear)                        // the path eases each beat internally
    .Duration(1.15f)
    .AsSpec(new A2BImagePayloadRenderer { Sprite = coinSprite });

A2B.Play(_burst, chest, walletIcon);
```

This is **not** an arc with a big scatter. Scatter moves where a coin *starts*, so it still reads as one continuous flight. `BurstThenGather` sends coins *away* from the wallet first and pauses at the apex — and that pause is what sells the second beat. It's still one effect on one path, so `FirstItemArrived` and cancellation behave exactly as usual.

Start the counter when the **first** coin lands — the hook the mechanic exists for:

```csharp
sealed class WalletListener : A2BEffectListenerBase   // a class: never boxes
{
    public override void OnFirstItemArrived(in A2BEffectHandle h, int i) => counter.BeginRollUp();
    public override void OnItemArrived(in A2BEffectHandle h, int i) => counter.Add(1);
}

var handle = A2B.Play(coinEffect, chest.transform, walletIcon);
handle.AddListener(_walletListener);   // reuse one instance — this path is allocation-free
```

Build in code instead of an asset — a true peer, same one-liner to play:

```csharp
// Cache the spec. Rebuilding per play allocates and defeats pool sharing.
_coins ??= A2BEffectBuilder.Create()
    .Count(12, 20).Stagger(0.03f).Scatter(0.6f)
    .Arc(height: 2f).Ease(A2BEaseKind.InOutCubic)
    .Duration(0.8f)
    .AsSpec(new A2BImagePayloadRenderer { Sprite = coinSprite })
    .Feedback(new A2BTrailFeedback())
    .Feedback(new A2BImpactFeedback { Sprite = sparkSprite });

A2B.Play(_coins, chest, walletIcon);          // identical call shape to the asset path
await A2B.PlayAsync(_coins, chest, walletIcon, cancellationToken: destroyCancellationToken);
```

## What you get

| Axis | Options |
| --- | --- |
| **Spaces** | `World3D`, `World2D`, `Canvas` (Overlay, Screen-Space-Camera, World-Space) — plus cross-space (world chest → UI wallet) |
| **Payloads** | Sprite, Image (uGUI), Mesh, Text (TMP), Particle, Prefab |
| **Paths** | Linear, Bezier (designer-facing arc, not raw control points), **BurstGather** (explode out, hang, get pulled in), Procedural (spiral/wave), Custom |
| **Easing** | 21 built-in kinds + AnimationCurve, composable with any path |
| **Emission** | Count range, all-at-once / fixed stagger / spread-over-duration, scatter |
| **Feedback** | Trails, impact-on-hit, spawn flash, audio with rising pitch — stackable, or write your own |
| **Events** | `Started`, `ItemSpawned`, `FirstItemArrived`, `ItemArrived`, `Completed`, `Cancelled` |

## Feedback — trails, impacts, juice

Add embellishments to the **Feedbacks** list on the effect asset; they stack. Each one answers "what *else* happens because of this item", which is why they're a separate seam from the payload — adding a trail to text shouldn't mean editing the text payload.

| Feedback | What it does | Works in |
| --- | --- | --- |
| `A2BTrailFeedback` | A pooled `TrailRenderer` behind each item | **World only** — see below |
| `A2BImpactFeedback` | **On hit:** spawns a pooled flash/prefab where an item lands | Any space |
| `A2BSpawnPopFeedback` | A colour/alpha flash as an item appears | Any space |
| `A2BAudioFeedback` | Pooled one-shots, with pitch that rises per item — the classic coin-cascade sound | Any space |

Impacts fire on a **genuine arrival only**. Cancel a 50-coin burst mid-flight and you get silence, not 50 sparks — the presenter is told *why* an item was released, not just that it was.

**Trails don't work on a Canvas, and no setting will fix it.** `TrailRenderer` is a world-space mesh renderer; a Screen-Space canvas won't draw one. Use trails in `World2D`/`World3D`. For a canvas impact, set `A2BImpactFeedback.Prefab` to a uGUI object rather than using the `Sprite` field — the sprite fallback builds a `SpriteRenderer`, which has the same problem.

Writing your own is a few lines:

```csharp
[System.Serializable]
public sealed class ShakeOnArrival : A2BFeedbackBase
{
    public override string FeedbackKey => "ShakeOnArrival";
    protected override void Arrived(Transform item, in A2BVisualState s) => CameraShaker.Shake(0.1f);
}
```

## Extending it

Full guide: **[EXTENDING.md](EXTENDING.md)**.

Add a path, payload, feedback, space adapter, endpoint provider or time source **without editing a single shipped file** — that is the open/closed check the test suite enforces. Most seams need no registration at all: they're `[SerializeReference]` fields, so the inspector's picker finds your class automatically the moment you write it.

Space adapters are the one exception — `Space` is an enum, and you can't add an enum value from outside — so they install through a factory, either per-asset (**Space Override** in the inspector) or globally:

```csharp
A2BAdapters.SetFactory(A2BSpaceKind.Canvas, new MyAdapterFactory());
```

```csharp
public sealed class MyPath : IA2BPath   // a class, never a struct: a struct behind an
{                                       // interface boxes on every call and would break
                                        // the allocation budget silently
    public Vector3 Evaluate(in A2BPathContext ctx, float t) => /* ... */;
}
```

Your path must land on both endpoints — assert it with the shipped contract:

```csharp
Assert.IsTrue(A2BPathConformance.SatisfiesEndpointInvariant(new MyPath(), in ctx));
```

That invariant is not pedantry: arrival is defined as `t >= 1`, so a path that misses the destination makes `FirstItemArrived` meaningless.

## Things worth knowing

- **Nothing throws.** Misconfiguration logs one actionable error naming the offending object and returns an invalid handle. A cosmetic system should never take down the reward-granting call stack.
- **Handles are structs with a generation stamp.** Holding one past completion, copying it, or double-cancelling are all safe — a stale handle can never address the effect that reused its slot.
- **Canvas items get their own canvas by default.** Any drawable change re-runs batch building for every element on that canvas, so 200 moving coins on your HUD canvas makes the *rebuild* the frame spike. Override via `A2BEffectPlayer.CanvasRoot` if you have profiled the other way.
- **Time is injected.** Tests and the editor preview drive the same `IA2BTimeSource` seam the runtime does, which is why preview cannot drift from real behavior.

## The A2BKit window

`Tools ▸ A2BKit ▸ A2BKit Window` is the front door: live runtime counts (active effects, items in
flight, pool occupancy), one-click **Create Effect Asset**, a toggle for the in-game overlay, and the
**Install AI Skill** button. In-game, press **F3** for the same counts as an overlay — which itself
does not allocate per frame, because a diagnostic that broke the package's headline constraint while
displaying its numbers would be self-refuting.

## AI skill

A2BKit ships a skill that teaches an AI assistant its API, patterns, performance rules and gotchas.
Click **Install AI Skill** in the window (or `Tools ▸ A2BKit ▸ Install AI Skill`) and it copies into
`.claude/skills/a2bkit/`, so Claude Code working in your project writes against the real API instead
of guessing. The skill ships in the package, so it stays matched to the version you have installed.

## Not in scope

Not a tweening library, not an economy system (A2BKit animates the reward; your game owns the number), no VFX Graph payloads (GPU particles cannot raise CPU-side arrival events or render into a Canvas), and no third-party dependency in the core.

## Docs

`Documentation~/` holds the full design record: the brief, the PRD (28 FRs), the architecture spine
(the ADs an implementer must not violate), the extending guide, and the AI skill.
