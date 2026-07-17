# Third-Party Notices

A2BKit ships no third-party art, audio, or code.

Every sprite and mesh in the samples is generated procedurally at author time from the code in
`Samples~/Examples/Scripts` — coins, chests, wallets, gems, orbs, stars and sparks are drawn from
simple signed-distance functions, and the 3D coin is derived from Unity's built-in cylinder primitive.
Nothing was downloaded, and there is no license to reproduce here.

## Dependencies

These are *referenced*, not bundled. A2BKit contains no copy of either.

- **UniTask** — MIT. https://github.com/Cysharp/UniTask
  Required, and installed separately: a package manifest's `dependencies` may only name registry
  packages, and UniTask ships as a Git package.
- **com.unity.ugui** — declared as a package dependency and resolved by the Unity Package Manager.
  Supplies TextMeshPro (namespace `TMPro`), which is bundled into uGUI 2.x from Unity 2023.2 onward.
