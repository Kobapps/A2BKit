# A2BKit Examples

Eleven scenes. Open one, press Play.

Install them through **Package Manager → A2BKit → Samples → A2BKit Examples → Import**, then open a
scene from `Assets/Samples/A2BKit/<version>/A2BKit Examples/Scenes/`.

## Hop between examples in one session

Press Play in any example and a small **A2BKit Examples** panel appears top-left with a button per
scene plus Prev/Next — click to jump straight to another example without leaving Play mode. It sets
itself up (no wiring in the scenes) and only ever shows in these sample scenes, never in your own game.

Runtime scene switching needs the scenes in Build Settings; the panel adds them the first time you
switch in the Editor, or run **Tools ▸ A2BKit ▸ Samples ▸ Add Example Scenes to Build Settings** once.
A standalone build must include them like any other scene.

## They are scenes, not scripts

Everything is authored in the scene: the camera, the canvas, the chest, the wallet, the effect. Select
the **Effect Player** object in any scene and the whole thing is visible in the inspector — the
`A2BEffectAsset` it plays, its Origin and Destination, and the UnityEvents wired to the counter and the
punch. Nothing is constructed at runtime, so what you read in the hierarchy is what you get.

The only scripts here are small reactions you wire up yourself:

| Script | Wire it to | What it does |
| --- | --- | --- |
| `A2BDemoAutoPlay` | — | Fires the player on a timer, so a scene is watchable with no input |
| `A2BDemoClickToPlay` | — (or a Button's `onClick`) | Fires the player when you click a target rect. No EventSystem needed — it hit-tests the pointer directly and reads whichever input backend is active |
| `A2BDemoMultiPlay` | — | Fires several players at once on a timer, so one reward can send coins and xp to different places at the same time |
| `A2BDemoCounter` | `OnFirstItemArrivedEvent` → `BeginRollUp`, `OnItemArrivedEvent` → `Increment` | The wallet number |
| `A2BDemoPunch` | `OnItemArrivedEvent` → `Punch` | Kicks the wallet icon on each landing |
| `A2BDemoXpBar` | `OnItemArrivedEvent` → `AddXp` | Fills a bar from arrivals |
| `A2BDemoOscillate` / `A2BDemoOrbit` | — | Moves a target while items are in flight |

## The scenes

| # | Scene | What it shows |
| --- | --- | --- |
| 1 | Coin To Wallet | **The flagship.** Coins arc from a chest to the wallet HUD. `FirstItemArrived` starts the counter; each `ItemArrived` increments it and punches the icon. |
| 2 | Coin Burst To Wallet | **The two-beat reward.** Coins explode *outward*, hang, then get pulled in — `A2BBurstGatherPath`. Not scene 1 with more scatter: scatter moves where a coin *starts*, so it never reverses. |
| 3 | Floating Score Text | **Click the star** and a "+N" pops off it, floats up and fades — each click plays a fresh popup, tallied into a running score. The text is passed as a value and the payload writes it through a reused StringBuilder. `A2BDemoClickToPlay` detects the click with no EventSystem. |
| 4 | XP Orbs | Orbs stream into a bar that fills **on arrival**. A parallel timer would desync the moment anything staggered or got cancelled; the bar only knows what landed. |
| 5 | Mesh Collect 3D | World3D, mesh payload, `AlignToVelocity`, with a trail. Trails work here and not on a Canvas — a `TrailRenderer` is a world-space mesh renderer. |
| 6 | Particle Burst | Each item is a pooled `ParticleSystem`. It is still a CPU-side Transform, which is why `ItemArrived` still fires — GPU particles could not tell you when anything landed. |
| 7 | Moving Target | The wallet slides across the screen **while coins are in flight**, and they still land on it. The only code doing that is `A2BDemoOscillate`: endpoints resolve every frame and are never cached at play time. |
| 8 | Cross Space | A 3D chest, a UI wallet. Origin is a plain world Transform; the effect plays in Canvas space and the adapter projects it. No camera math at the call site. |
| 9 | Multiple Effects | **Two effects at once, to different places.** One reward sends coins to the wallet HUD *and* xp orbs to the level bar. They run concurrently and never disturb each other — one scheduler advances every effect, and each has its own pool. Guarded by `A2BConcurrentEffectsTests`. |
| 10 | UI Particles | **Particles on a screen-space canvas.** `A2BUIParticle` bakes a world-space `ParticleSystem` into a `CanvasRenderer` — a glow behind a card, a confetti fountain in front, sortable and maskable like any UI element, in one draw call and no extra camera. The standalone, A2B-independent use of the UI-particle module. |
| 11 | UI Trails | **A2B effects with trails over a Canvas.** Comets streak from the chest to the wallet leaving long bright tails *on the HUD*. A `TrailRenderer` is world-space and can't draw on a screen-space canvas — `A2BTrailFeedback` detects the canvas and bakes every trail into one `CanvasRenderer` via `A2BUIParticle`. Sortable, one draw call. |

## The art

Every sprite here is a real PNG in `Art/`, and the 3D coin comes from Unity's cylinder primitive.
They were drawn procedurally at author time rather than downloaded, so the package ships no
third-party assets and there is nothing to license — but they are ordinary assets now, and you can
replace any of them by dropping a different sprite into the effect asset.
