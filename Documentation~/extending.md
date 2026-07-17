# Extending A2BKit

A2BKit is built so the thing you want to change is a small class you write, not a fork. Everything variable enters through one of seven seams.

The rule the package holds itself to: **adding a variant must not require editing a shipped file.** If you ever have to, that's a bug — open an issue.

## The seams

| You want to change… | Implement | Where it's picked |
| --- | --- | --- |
| The trajectory | `IA2BPath` | Effect asset → Path |
| The timing curve | `IA2BEasing` | Effect asset → Easing |
| How many items, when, how scattered | `IA2BEmission` | Effect asset → Emission |
| What an item *is* | `IA2BPayloadRenderer` | Effect asset → Payload |
| Trails, impacts, sound — "what else happens" | `IA2BFeedback` | Effect asset → Feedbacks |
| Where an endpoint is | `IA2BEndpointProvider` | Passed to `Play` |
| Coordinate space / item placement | `IA2BSpaceAdapter` | via `IA2BSpaceAdapterFactory` |
| The clock | `IA2BTimeSource` | Definition, or `SetTimeSource` |

Six of the eight need **no registration at all**. They're `[SerializeReference]` fields, and the inspector's type picker finds every implementation in your project through `TypeCache`. Write the class, reopen the dropdown, it's there.

## Write a path

```csharp
using A2BKit.Core;
using UnityEngine;

[System.Serializable]                       // so the inspector can serialize it
public sealed class ZigZagPath : IA2BPath   // a CLASS, never a struct — see "The three rules"
{
    public float Amplitude = 1f;
    public int Zigs = 4;

    public Vector3 Evaluate(in A2BPathContext ctx, float t)
    {
        Vector3 straight = Vector3.LerpUnclamped(ctx.Origin, ctx.Destination, t);
        float envelope = Mathf.Sin(t * Mathf.PI);              // 0 at both ends — see rule 3
        float offset = Mathf.Sin(t * Zigs * Mathf.PI * 2f) * Amplitude * envelope;
        return straight + Vector3.right * offset;
    }
}
```

Then assert it holds the contract — the same check the built-ins run:

```csharp
var ctx = new A2BPathContext(Vector3.zero, Vector3.up * 5f, 0, 1, 12345u);
Assert.IsTrue(A2BPathConformance.SatisfiesEndpointInvariant(new ZigZagPath(), in ctx));
```

## Write a feedback (trails, impacts, juice)

```csharp
[System.Serializable]
public sealed class ShakeOnArrival : A2BFeedbackBase
{
    public override string FeedbackKey => "ShakeOnArrival";

    protected override void Arrived(Transform item, in A2BVisualState finalState)
        => CameraShaker.Shake(0.1f);   // fires ONLY on a real landing, never on a cancel
}
```

`A2BFeedbackBase` gives you the prototype clone, the try/catch, and empty hooks so you override only the moment you care about. Hooks: `Spawned`, `Updated` (per frame — treat as hot), `Arrived`, `Released`, `Disposed`.

`Arrived` fires **only** on a genuine landing. `Released` fires for landings *and* cancellations — undo anything you attached there, or the next effect inherits it (a pooled coin that keeps a stale trail draws a streak across the screen).

Know which space your feedback can live in. Anything built on a world-space renderer — `TrailRenderer`, `SpriteRenderer`, `ParticleSystem` — will not be drawn by a Screen-Space canvas, no matter how it's configured. That isn't an A2BKit limitation; it's how Unity renders UI. Spawn uGUI objects for canvas effects, and say so in your docs rather than letting someone debug an invisible trail.

## Write a space adapter

The one seam that needs registering, because `Space` is an enum and you can't add a value to an enum from outside.

```csharp
public sealed class MyAdapterFactory : IA2BSpaceAdapterFactory
{
    public IA2BSpaceAdapter Create(in A2BSpaceContext ctx) => new MyAdapter(ctx.HostCanvas);
}
```

Two ways to install it, by blast radius:

```csharp
// Per asset — local and visible. Set "Space Override" in the effect asset's inspector.

// Globally — every Canvas effect in the game, from a bootstrap:
A2BAdapters.SetFactory(A2BSpaceKind.Canvas, new MyAdapterFactory());
A2BAdapters.SetFactory(A2BSpaceKind.Canvas, null);   // back to the built-in
```

Precedence is **asset override → global registry → built-in**. A factory that throws or returns null falls back to the built-in and logs — a broken extension degrades the effect, it doesn't kill the frame.

An adapter owns two things nothing else may touch: **coordinate conversion** and **the item's Transform**.

## The three rules

**1. Strategies are classes, never structs.** A struct in an interface-typed field boxes on assignment and again on every call — allocating in the hot loop while the code still reads as allocation-free. `[SerializeReference]` refuses value types anyway, so the compiler mostly saves you.

**2. Only the space adapter writes the item's Transform.** Payloads set drawables (sprite, colour, text, mesh). Feedbacks add their own child objects. Neither touches `item.position`, `localScale` or `parent`. Two payload authors who both ignore this will disagree about `position` vs `localPosition`, and one of them puts your Canvas effect at the world origin.

**3. A path must land on both ends.** `Evaluate(ctx, 0) == Origin` and `Evaluate(ctx, 1) == Destination`. Arrival is defined as `t >= 1` — so a path that drifts off its destination makes `FirstItemArrived` fire for a coin that visibly isn't there. Use an envelope that decays to zero at both ends (`Mathf.Sin(t * PI)` is the cheap one).

## Configuration is shared — copy before you mutate

An asset holds **one** instance of each strategy, shared by every effect that uses it. They are immutable-after-authoring, not stateless. If you change a setting at runtime, copy first:

```csharp
// WRONG — edits the asset on disk and every other effect using it
((A2BBurstEmission)asset.Definition.Emission).MinCount = 50;

// Right — the builder copies on write for you
var boosted = A2BEffectBuilder.From(asset.Definition).Count(50).Build();
```

Payload renderers and feedbacks own pools, so they can't be shared at all — they hand out clones via `CreateRuntimeInstance()`. Derive from `A2BPooledPayloadRenderer` / `A2BFeedbackBase` and this is handled; build runtime state in `OnInitialized()`, never in a field initializer, or your clone will share it.

## Performance rules for anything in the tick

`Evaluate`, `UpdateItem`, `OnItemUpdated` and `Resolve` run per item per frame. In them: no LINQ, no closures, no `GetComponent`, no string work (`StringBuilder.Append(int)` allocates on Unity's Mono — use `A2BNumberFormat`), no `foreach` over an interface-typed collection.

Prove it rather than assume it:

```csharp
using Is = UnityEngine.TestTools.Constraints.Is;   // MANDATORY — NUnit's Is otherwise shadows this
                                                   // and your test passes while measuring nothing
Assert.That(() => { scheduler.Tick(); }, Is.Not.AllocatingGCMemory());
```

## Never throw

No A2BKit API throws for a runtime or config fault, and your extension shouldn't either. A cosmetic system that throws into a reward-granting call stack can cost a player their purchase. Log through `A2BLog.Error(context, message)` — it takes the offending object so clicking the console entry selects it — and degrade.
