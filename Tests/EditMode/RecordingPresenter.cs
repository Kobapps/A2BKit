using System.Collections.Generic;
using A2BKit.Core;
using UnityEngine;

namespace A2BKit.Tests.EditMode
{
    /// <summary>
    /// The reason AD-18 made Core's presentation port index-based: a full test double for
    /// <see cref="IA2BPresenter"/> that needs no scene, no Transform and no GameObject.
    ///
    /// Records every Acquire/Apply/Release plus the item ids involved, so pool-leak assertions
    /// (AD-9) reduce to "every acquired id came back exactly once".
    /// </summary>
    internal sealed class RecordingPresenter : IA2BPresenter
    {
        private int _nextId = 1;

        public readonly List<int> AcquiredIds = new List<int>();
        public readonly List<int> ReleasedIds = new List<int>();
        public readonly HashSet<int> LiveIds = new HashSet<int>();
        public readonly List<A2BItemSpawnInfo> SpawnInfos = new List<A2BItemSpawnInfo>();
        public readonly Dictionary<int, A2BVisualState> StatesById = new Dictionary<int, A2BVisualState>();

        public int ApplyCount;

        /// <summary>Release called for an id that was not live. AD-9 says this must never happen.</summary>
        public int DoubleReleaseCount;

        /// <summary>Apply called with an id the presenter never issued.</summary>
        public int UnknownApplyCount;

        public int AcquireCount => AcquiredIds.Count;
        public int ReleaseCount => ReleasedIds.Count;
        public int LiveCount => LiveIds.Count;

        public int Acquire(in A2BItemSpawnInfo info)
        {
            int id = _nextId++;
            AcquiredIds.Add(id);
            LiveIds.Add(id);
            SpawnInfos.Add(info);
            return id;
        }

        public void Apply(int itemId, in A2BVisualState state)
        {
            ApplyCount++;
            if (!LiveIds.Contains(itemId)) UnknownApplyCount++;
            StatesById[itemId] = state;
        }

        /// <summary>Reasons in call order, so a test can assert an arrival sparked and a cancel did not.</summary>
        public readonly List<A2BReleaseReason> ReleaseReasons = new List<A2BReleaseReason>();

        /// <summary>Items released because they genuinely landed (t &gt;= 1).</summary>
        public int ArrivedReleaseCount;

        /// <summary>Items recalled before landing — cancel, endpoint lost, host destroyed.</summary>
        public int CancelledReleaseCount;

        public void Release(int itemId, A2BReleaseReason reason)
        {
            // The port contract says Release must tolerate an id it already released, so this
            // double-counts rather than throws — and the count is asserted to be zero.
            if (!LiveIds.Remove(itemId))
            {
                DoubleReleaseCount++;
                return;
            }
            ReleasedIds.Add(itemId);
            ReleaseReasons.Add(reason);
            if (reason == A2BReleaseReason.Arrived) ArrivedReleaseCount++;
            else CancelledReleaseCount++;
        }

        public int ScaleScatterCallCount;
        public Vector3 LastScatterUnit;
        public float LastScatterRadius = float.NaN;

        /// <summary>
        /// Identity units so tests can assert working-space math directly. The radius is recorded
        /// because AD-16 makes assigning units the PRESENTER's job — this is the seam where a
        /// unitless [-1,1]^3 offset becomes working-space distance.
        /// </summary>
        public Vector3 ScaleScatter(in Vector3 unitOffset, float radius)
        {
            ScaleScatterCallCount++;
            LastScatterUnit = unitOffset;
            LastScatterRadius = radius;
            return unitOffset * radius;
        }

        public int ToWorkingSpaceCallCount;
        public A2BEndpointSpace LastEndpointSpace = A2BEndpointSpace.World;

        /// <summary>
        /// Identity space, so working space == world space in Core tests (AD-4).
        ///
        /// The whole sample arrives rather than a bare Vector3 so the adapter can honour
        /// <see cref="A2BEndpointSpace"/> — a screen-space endpoint is already projected and must not
        /// be projected again. The Space is recorded so tests can assert it survives the trip.
        /// </summary>
        public Vector3 ToWorkingSpace(in A2BEndpointSample sample)
        {
            ToWorkingSpaceCallCount++;
            LastEndpointSpace = sample.Space;
            return sample.Position;
        }

        public A2BVisualState StateForItem(int itemIndex) => StatesById[AcquiredIds[itemIndex]];

        public void Reset()
        {
            _nextId = 1;
            AcquiredIds.Clear();
            ReleasedIds.Clear();
            LiveIds.Clear();
            SpawnInfos.Clear();
            StatesById.Clear();
            ApplyCount = 0;
            DoubleReleaseCount = 0;
            ReleaseReasons.Clear();
            ArrivedReleaseCount = 0;
            CancelledReleaseCount = 0;
            UnknownApplyCount = 0;
            ScaleScatterCallCount = 0;
            LastScatterUnit = Vector3.zero;
            LastScatterRadius = float.NaN;
            ToWorkingSpaceCallCount = 0;
            LastEndpointSpace = A2BEndpointSpace.World;
        }
    }
}
