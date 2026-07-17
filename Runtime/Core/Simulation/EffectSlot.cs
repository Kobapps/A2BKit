using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine;

namespace A2BKit.Core
{
    /// <summary>Per-item simulation state. A struct in a reused array — never individually allocated.</summary>
    internal struct ItemState
    {
        public int PresenterItemId;
        public float Delay;
        public float Duration;
        public float Elapsed;
        public bool Spawned;
        public bool Arrived;
        public Vector3 ScatterUnit;
        public uint Seed;
        public Vector3 LastPosition;
    }

    /// <summary>
    /// One running effect. A pooled class whose ItemState[] is reused across plays — which is how
    /// zero per-frame allocation falls out of slot pooling rather than needing an array pool (AD-3).
    /// </summary>
    internal sealed class EffectSlot
    {
        // Bumped on every release. A handle whose stamp no longer matches is stale (AD-7).
        public uint Generation = 1;
        public bool Active;

        public A2BEffectDefinition Definition;
        public IA2BEndpointProvider Origin;
        public IA2BEndpointProvider Destination;
        public IA2BPresenter Presenter;
        public IA2BTimeSource TimeSource;

        public ItemState[] Items = new ItemState[16];
        public int ItemCount;
        public int ArrivedCount;
        public int SpawnedCount;
        public bool FirstArrivedRaised;
        public bool StartedRaised;

        public uint Seed;
        public string Text;
        public float Value;

        public A2BEndpointSample LastKnownOrigin;
        public A2BEndpointSample LastKnownDestination;
        public bool HasLastKnownOrigin;
        public bool HasLastKnownDestination;

        public CancellationToken CancellationToken;

        // Reused; only grows when a caller adds more listeners than before.
        public readonly List<IA2BEffectListener> Listeners = new List<IA2BEffectListener>(4);

        // Created only when someone actually awaits, so the sync path pays nothing.
        public AutoResetUniTaskCompletionSource<A2BCompletionReason> Completion;

        /// <summary>Deferred release, so a listener calling Cancel() mid-tick cannot recycle a slot under the loop (AD-17).</summary>
        public bool ReleaseRequested;
        public A2BCompletionReason PendingReason;

        public void EnsureCapacity(int count)
        {
            if (Items.Length >= count) return;

            // Grow to the next power of two and keep it. Steady state stops growing almost immediately,
            // which is what keeps the tick allocation-free.
            int capacity = Items.Length;
            while (capacity < count) capacity <<= 1;
            Items = new ItemState[capacity];
        }

        public void ResetForReuse()
        {
            Active = false;
            Definition = null;
            Origin = null;
            Destination = null;
            Presenter = null;
            TimeSource = null;
            ItemCount = 0;
            ArrivedCount = 0;
            SpawnedCount = 0;
            FirstArrivedRaised = false;
            StartedRaised = false;
            Text = null;
            Value = 0f;
            HasLastKnownOrigin = false;
            HasLastKnownDestination = false;
            CancellationToken = default;
            Listeners.Clear();
            Completion = null;
            ReleaseRequested = false;
            PendingReason = A2BCompletionReason.Completed;
        }
    }
}
