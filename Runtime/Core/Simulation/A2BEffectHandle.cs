using System;
using System.Threading;
using Cysharp.Threading.Tasks;

namespace A2BKit.Core
{
    /// <summary>
    /// The caller's reference to a running effect.
    ///
    /// A readonly struct carrying a generation stamp (AD-7). The stamp is not ceremony: slots are
    /// pooled and reused, so without it a stale copy of a handle would silently address a DIFFERENT
    /// effect that had since taken the slot — cancelling someone else's coins. That bug is silent,
    /// intermittent, and near-impossible to diagnose in the field.
    ///
    /// Every operation re-validates the stamp and degrades to a safe no-op on mismatch, so copying a
    /// handle by value, holding one past completion, or double-cancelling are all harmless.
    /// </summary>
    public readonly struct A2BEffectHandle : IEquatable<A2BEffectHandle>
    {
        private readonly A2BScheduler _scheduler;
        private readonly int _slot;
        private readonly uint _generation;

        internal A2BEffectHandle(A2BScheduler scheduler, int slot, uint generation)
        {
            _scheduler = scheduler;
            _slot = slot;
            _generation = generation;
        }

        internal int Slot => _slot;
        internal uint Generation => _generation;
        internal A2BScheduler Scheduler => _scheduler;

        /// <summary>An explicitly invalid handle. Returned by Play when configuration is bad (AD-8, FR-3).</summary>
        public static A2BEffectHandle Invalid => default;

        /// <summary>True while this handle still refers to the effect it was issued for.</summary>
        public bool IsValid => _scheduler != null && _scheduler.IsHandleValid(_slot, _generation);

        /// <summary>Items that have arrived so far. Zero for an invalid handle.</summary>
        public int ArrivedCount => _scheduler != null ? _scheduler.GetArrivedCount(_slot, _generation) : 0;

        /// <summary>Items this effect is playing. Zero for an invalid handle.</summary>
        public int ItemCount => _scheduler != null ? _scheduler.GetItemCount(_slot, _generation) : 0;

        /// <summary>Stops the effect and returns its items to the pool. A no-op if already finished.</summary>
        public void Cancel() => _scheduler?.Cancel(_slot, _generation);

        /// <summary>
        /// Registers a listener. This is the allocation-free event path (AD-11): pass one reusable
        /// implementer and dispatch costs a plain interface call.
        /// </summary>
        public void AddListener(IA2BEffectListener listener) => _scheduler?.AddListener(_slot, _generation, listener);

        public void RemoveListener(IA2BEffectListener listener) => _scheduler?.RemoveListener(_slot, _generation, listener);

        /// <summary>
        /// Awaits completion. Resolves when the effect completes OR is cancelled — it does not throw
        /// on cancellation, because a cosmetic effect being cut short is not exceptional (AD-8).
        /// Returns the terminal reason so the caller can branch.
        ///
        /// Costs bounded Per-Play allocation (the awaiter), never per-frame (NFR-1). An invalid handle
        /// completes immediately as Invalid, so awaiting a failed Play is safe.
        /// </summary>
        public UniTask<A2BCompletionReason> ToUniTask(CancellationToken cancellationToken = default)
        {
            if (_scheduler == null)
                return UniTask.FromResult(A2BCompletionReason.Invalid);
            return _scheduler.AwaitCompletion(_slot, _generation, cancellationToken);
        }

        public bool Equals(A2BEffectHandle other)
            => _scheduler == other._scheduler && _slot == other._slot && _generation == other._generation;

        public override bool Equals(object obj) => obj is A2BEffectHandle other && Equals(other);

        public override int GetHashCode()
        {
            unchecked
            {
                int hash = _slot;
                hash = (hash * 397) ^ (int)_generation;
                hash = (hash * 397) ^ (_scheduler != null ? _scheduler.GetHashCode() : 0);
                return hash;
            }
        }

        public static bool operator ==(A2BEffectHandle a, A2BEffectHandle b) => a.Equals(b);
        public static bool operator !=(A2BEffectHandle a, A2BEffectHandle b) => !a.Equals(b);
    }
}
