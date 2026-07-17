using A2BKit.Core;
using UnityEngine;

namespace A2BKit.Tests.PlayMode
{
    /// <summary>
    /// A pooling presenter stub that records Acquire/Apply/Release without allocating a byte.
    ///
    /// This exists because of AD-18: Core's presentation port speaks in item ids and value structs,
    /// so the allocation gate can measure the SIMULATION rather than a scene. Every buffer is
    /// pre-sized at construction and reused — if the stub allocated, the FR-18 measurements would be
    /// measuring the test double instead of the package.
    /// </summary>
    internal sealed class RecordingPresenter : IA2BPresenter
    {
        private readonly int[] _freeIds;
        private readonly bool[] _live;
        private int _freeCount;

        public int AcquireCount;
        public int ReleaseCount;
        public int ApplyCount;
        public int LiveCount;

        /// <summary>Release called for an id that was not live. AD-9 says this must stay zero.</summary>
        public int DoubleReleaseCount;

        /// <summary>Acquire called with the pool exhausted. Non-zero means the fixture is undersized.</summary>
        public int ExhaustedCount;

        public Vector3 LastPosition;
        public float LastProgress;

        public RecordingPresenter(int capacity = 1024)
        {
            _freeIds = new int[capacity];
            _live = new bool[capacity];
            for (int i = 0; i < capacity; i++) _freeIds[i] = capacity - 1 - i;
            _freeCount = capacity;
        }

        public int Acquire(in A2BItemSpawnInfo info)
        {
            if (_freeCount == 0)
            {
                ExhaustedCount++;
                return -1;
            }

            AcquireCount++;
            int id = _freeIds[--_freeCount];
            _live[id] = true;
            LiveCount++;
            return id;
        }

        public void Apply(int itemId, in A2BVisualState state)
        {
            ApplyCount++;
            LastPosition = state.Position;
            LastProgress = state.Progress;
        }

        /// <summary>Released because the item genuinely landed. Counters only — this presenter is
        /// used inside the measured tick, so it must not allocate (AD-3).</summary>
        public int ArrivedReleaseCount;

        /// <summary>Released because the item was recalled before landing.</summary>
        public int CancelledReleaseCount;

        public void Release(int itemId, A2BReleaseReason reason)
        {
            if (itemId < 0 || itemId >= _live.Length || !_live[itemId])
            {
                // The port contract requires tolerating a double release rather than throwing.
                DoubleReleaseCount++;
                return;
            }

            if (reason == A2BReleaseReason.Arrived) ArrivedReleaseCount++;
            else CancelledReleaseCount++;

            ReleaseCount++;
            _live[itemId] = false;
            LiveCount--;
            _freeIds[_freeCount++] = itemId;
        }

        public Vector3 ScaleScatter(in Vector3 unitOffset, float radius) => unitOffset * radius;

        /// <summary>
        /// Identity space. Takes the whole sample (not a bare Vector3) so the adapter can honour
        /// A2BEndpointSpace — reading a field off an `in` struct allocates nothing.
        /// </summary>
        public Vector3 ToWorkingSpace(in A2BEndpointSample sample) => sample.Position;

        public void ResetCounters()
        {
            AcquireCount = 0;
            ReleaseCount = 0;
            ApplyCount = 0;
            DoubleReleaseCount = 0;
            ExhaustedCount = 0;
        }
    }
}
