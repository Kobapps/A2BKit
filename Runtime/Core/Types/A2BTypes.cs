using System;
using UnityEngine;

namespace A2BKit.Core
{
    /// <summary>Why an effect stopped. Exactly one terminal reason is raised per effect (AD-9).</summary>
    public enum A2BCompletionReason
    {
        Completed,
        Cancelled,
        EndpointLost,
        Invalid
    }

    /// <summary>
    /// Why a single item's visual is going back to the pool.
    ///
    /// The presenter needs this to tell "landed" from "cut short": an arrival should spark, punch and
    /// play a sound; a cancelled item should vanish quietly. Without the reason, every feedback would
    /// fire on teardown too — a cancelled 50-coin burst would emit 50 impacts at once.
    /// </summary>
    public enum A2BReleaseReason
    {
        /// <summary>The item reached the destination (t &gt;= 1). Feedback fires.</summary>
        Arrived,

        /// <summary>The item was recalled before arriving — cancelled, endpoint lost, host destroyed.</summary>
        Cancelled
    }

    /// <summary>
    /// The coordinate domain an effect plays in. Canvas covers all three render modes —
    /// "world-space canvas" is a render mode, not a fourth space.
    /// </summary>
    public enum A2BSpaceKind
    {
        World3D,
        World2D,
        Canvas
    }

    /// <summary>
    /// Which coordinate domain an endpoint reported itself in.
    ///
    /// This distinction is load-bearing, not bookkeeping. A UI target's natural coordinate is NOT a
    /// world position: on a Screen-Space-Overlay canvas a RectTransform's `position` is already in
    /// screen pixels (~960, 540, 0). Treating that as a world position and running it through
    /// Camera.WorldToScreenPoint projects a point 960 world-units to the right, and the items land
    /// nowhere near the target — the exact failure the canonical coin-to-wallet path used to have.
    ///
    /// Both members are plain Vector3 coordinate spaces, so Core stays free of scene-graph types (AD-1).
    /// </summary>
    public enum A2BEndpointSpace
    {
        /// <summary>A position in world space. Needs projecting for a screen-space canvas.</summary>
        World,

        /// <summary>A screen-pixel coordinate. Already projected — must NOT be projected again.</summary>
        Screen
    }

    /// <summary>A resolved endpoint, the domain it is expressed in, and whether it resolved at all.</summary>
    public readonly struct A2BEndpointSample
    {
        /// <summary>The coordinate. World units or screen pixels per <see cref="Space"/>.</summary>
        public readonly Vector3 Position;

        public readonly A2BEndpointSpace Space;
        public readonly bool IsValid;

        public A2BEndpointSample(Vector3 position, A2BEndpointSpace space, bool isValid)
        {
            Position = position;
            Space = space;
            IsValid = isValid;
        }

        /// <summary>Kept for callers that only ever deal in world space.</summary>
        public Vector3 WorldPosition => Position;

        public static A2BEndpointSample Invalid => default;

        /// <summary>A world-space endpoint (a chest, a 3D enemy).</summary>
        public static A2BEndpointSample At(Vector3 world)
            => new A2BEndpointSample(world, A2BEndpointSpace.World, true);

        /// <summary>
        /// A screen-space endpoint (a UI target). The space adapter converts it without projecting,
        /// because it is already projected.
        /// </summary>
        public static A2BEndpointSample AtScreen(Vector3 screenPoint)
            => new A2BEndpointSample(screenPoint, A2BEndpointSpace.Screen, true);
    }

    /// <summary>
    /// Everything a path needs to place an item, in working space. Passed by <c>in</c> — never copied per item.
    /// Paths are pure functions of this plus t (AD-13).
    /// </summary>
    public readonly struct A2BPathContext
    {
        public readonly Vector3 Origin;
        public readonly Vector3 Destination;

        /// <summary>Item index within the effect. Lets a path fan items out deterministically.</summary>
        public readonly int ItemIndex;

        /// <summary>Item count. Lets a path normalize <see cref="ItemIndex"/> without extra state.</summary>
        public readonly int ItemCount;

        /// <summary>Deterministic per-item seed derived from (effectSeed, itemIndex) — see AD-10.</summary>
        public readonly uint Seed;

        public A2BPathContext(Vector3 origin, Vector3 destination, int itemIndex, int itemCount, uint seed)
        {
            Origin = origin;
            Destination = destination;
            ItemIndex = itemIndex;
            ItemCount = itemCount;
            Seed = seed;
        }
    }

    /// <summary>
    /// What the presenter needs to show one item this frame. Value type, passed by <c>in</c>.
    /// Position is in working space — the presenter's space adapter owns the conversion to a Transform (AD-15).
    /// </summary>
    public readonly struct A2BVisualState
    {
        public readonly Vector3 Position;
        public readonly Quaternion Rotation;
        public readonly Vector3 Scale;
        public readonly Color Color;

        /// <summary>Normalized progress in [0,1]. Handy for payload-side fades without recomputing.</summary>
        public readonly float Progress;

        public A2BVisualState(Vector3 position, Quaternion rotation, Vector3 scale, Color color, float progress)
        {
            Position = position;
            Rotation = rotation;
            Scale = scale;
            Color = color;
            Progress = progress;
        }
    }

    /// <summary>Describes one item at spawn time. The presenter acquires a visual for it and returns an id.</summary>
    public readonly struct A2BItemSpawnInfo
    {
        public readonly int ItemIndex;
        public readonly int ItemCount;
        public readonly uint Seed;

        /// <summary>Optional per-item text (e.g. "+250"). Null for non-text payloads.</summary>
        public readonly string Text;

        /// <summary>Optional per-item numeric value. Text payloads format this without allocating (AD-20).</summary>
        public readonly float Value;

        public A2BItemSpawnInfo(int itemIndex, int itemCount, uint seed, string text, float value)
        {
            ItemIndex = itemIndex;
            ItemCount = itemCount;
            Seed = seed;
            Text = text;
            Value = value;
        }
    }

    /// <summary>
    /// Deterministic, allocation-free RNG. A struct so it lives on the stack; xorshift32 because
    /// per-item variation must be *computed* from (seed, index), never stored in a list (AD-10).
    /// UnityEngine.Random is forbidden package-wide: it is global mutable state and would break NFR-4.
    /// </summary>
    public struct A2BRandom
    {
        private uint _state;

        public A2BRandom(uint seed)
        {
            // A zero state is absorbing for xorshift — every subsequent value would be zero.
            _state = seed == 0u ? 0x9E3779B9u : seed;
        }

        public uint NextUInt()
        {
            uint x = _state;
            x ^= x << 13;
            x ^= x >> 17;
            x ^= x << 5;
            _state = x;
            return x;
        }

        /// <summary>Uniform float in [0,1).</summary>
        public float NextFloat() => (NextUInt() >> 8) * (1.0f / 16777216.0f);

        /// <summary>Uniform float in [min,max).</summary>
        public float NextFloat(float min, float max) => min + (max - min) * NextFloat();

        /// <summary>Uniform int in [min,max).</summary>
        public int NextInt(int min, int max) => max <= min ? min : min + (int)(NextUInt() % (uint)(max - min));

        /// <summary>Uniform point in the unit sphere. Rejection-free so the cost is constant.</summary>
        public Vector3 NextUnitSphere()
        {
            float z = NextFloat(-1f, 1f);
            float a = NextFloat(0f, Mathf.PI * 2f);
            float r = Mathf.Sqrt(Mathf.Max(0f, 1f - z * z));
            return new Vector3(r * Mathf.Cos(a), r * Mathf.Sin(a), z);
        }

        /// <summary>Derives a stable per-item seed from an effect seed and an item index (AD-10).</summary>
        public static uint DeriveSeed(uint effectSeed, int itemIndex)
        {
            unchecked
            {
                uint h = effectSeed ^ (uint)(itemIndex * 0x9E3779B9);
                h ^= h >> 16;
                h *= 0x7FEB352Du;
                h ^= h >> 15;
                h *= 0x846CA68Bu;
                h ^= h >> 16;
                return h == 0u ? 0x9E3779B9u : h;
            }
        }
    }
}
