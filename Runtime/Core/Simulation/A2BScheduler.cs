using System;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine;

namespace A2BKit.Core
{
    /// <summary>
    /// Owns every running effect and advances them all from one tick (AD-6).
    ///
    /// Nothing self-updates: there is no Update, coroutine or timer on items, effects, payloads or
    /// providers. That is partly performance (N MonoBehaviour.Update callbacks is the classic reason
    /// "200 of anything" is slow) and partly determinism — one ordered loop is reproducible, N engine
    /// callbacks are not (NFR-4).
    ///
    /// Tick() takes NO delta: each slot pulls from its own IA2BTimeSource, so a paused-menu effect
    /// (unscaled) and a gameplay effect (scaled) can advance correctly in the same frame (AD-6/AD-12).
    /// </summary>
    public sealed class A2BScheduler
    {
        private readonly List<EffectSlot> _slots = new List<EffectSlot>(32);
        private readonly List<int> _freeSlots = new List<int>(32);
        private readonly List<int> _activeSlots = new List<int>(32);

        // Effects created during a tick wait here (AD-17). Playing from an ItemArrived callback is
        // the documented use case, not an edge case, so re-entrancy has to be designed for.
        private readonly List<int> _pendingSlots = new List<int>(8);

        // Distinct time sources this tick, so each source is read exactly once per frame.
        // Reused buffers; linear scan because the count is realistically 1-3.
        private readonly IA2BTimeSource[] _tickSources = new IA2BTimeSource[8];
        private readonly float[] _tickDeltas = new float[8];
        private int _tickSourceCount;

        private bool _ticking;

        /// <summary>Effects currently playing. Drives the debug overlay (FR-22).</summary>
        public int ActiveEffectCount => _activeSlots.Count + _pendingSlots.Count;

        /// <summary>Slots ever created — active plus pooled. The high-water mark of concurrency.</summary>
        public int SlotCapacity => _slots.Count;

        /// <summary>Total items in flight across all active effects.</summary>
        public int ActiveItemCount
        {
            get
            {
                int total = 0;
                for (int i = 0; i < _activeSlots.Count; i++)
                {
                    EffectSlot slot = _slots[_activeSlots[i]];
                    total += slot.ItemCount - slot.ArrivedCount;
                }
                return total;
            }
        }

        /// <summary>
        /// Starts an effect. Returns a handle immediately.
        ///
        /// Never throws (AD-8, FR-3): invalid configuration logs one actionable error and returns an
        /// invalid handle, which is safe to hold, cancel and await.
        /// </summary>
        public A2BEffectHandle Play(A2BEffectDefinition definition, in A2BPlayArgs args, UnityEngine.Object context = null)
        {
            if (definition == null)
            {
                A2BLog.Error(context, "Play failed: effect definition is null.");
                return A2BEffectHandle.Invalid;
            }
            if (!definition.Validate(out string error))
            {
                A2BLog.Error(context, "Play failed: " + error);
                return A2BEffectHandle.Invalid;
            }
            if (args.Presenter == null)
            {
                A2BLog.Error(context, "Play failed: no presenter supplied.");
                return A2BEffectHandle.Invalid;
            }
            if (args.Origin == null)
            {
                A2BLog.Error(context, "Play failed: no origin endpoint supplied.");
                return A2BEffectHandle.Invalid;
            }
            if (args.Destination == null)
            {
                A2BLog.Error(context, "Play failed: no destination endpoint supplied.");
                return A2BEffectHandle.Invalid;
            }

            int index = AcquireSlot();
            EffectSlot slot = _slots[index];

            slot.Active = true;
            slot.Definition = definition;
            slot.Origin = args.Origin;
            slot.Destination = args.Destination;
            slot.Presenter = args.Presenter;
            slot.TimeSource = definition.UseUnscaledTime
                ? (IA2BTimeSource)A2BUnscaledTimeSource.Instance
                : A2BScaledTimeSource.Instance;
            slot.Text = args.Text;
            slot.Value = args.Value;
            slot.CancellationToken = args.CancellationToken;
            slot.Seed = args.Seed != 0u ? args.Seed : NextSeed();

            int count = Mathf.Max(1, definition.Emission.ResolveItemCount(slot.Seed));
            slot.EnsureCapacity(count);
            slot.ItemCount = count;

            for (int i = 0; i < count; i++)
            {
                ref ItemState item = ref slot.Items[i];
                item.PresenterItemId = -1;
                item.Delay = definition.Emission.ResolveDelay(slot.Seed, i, count);
                item.Duration = definition.ResolveItemDuration(slot.Seed, i);
                item.Elapsed = 0f;
                item.Spawned = false;
                item.Arrived = false;
                item.ScatterUnit = definition.Emission.ResolveScatter(slot.Seed, i, count);
                item.Seed = A2BRandom.DeriveSeed(slot.Seed, i);
                item.LastPosition = Vector3.zero;
            }

            if (_ticking) _pendingSlots.Add(index);
            else _activeSlots.Add(index);

            return new A2BEffectHandle(this, index, slot.Generation);
        }

        /// <summary>
        /// Sets an explicit time source, overriding the definition's scaled/unscaled choice.
        /// This is the seam EditMode tests and the editor preview drive (AD-12).
        /// </summary>
        public void SetTimeSource(in A2BEffectHandle handle, IA2BTimeSource timeSource)
        {
            if (timeSource == null) return;
            EffectSlot slot = Resolve(handle.Slot, handle.Generation);
            if (slot != null) slot.TimeSource = timeSource;
        }

        /// <summary>
        /// Advances every active effect by one frame. Non-reentrant (AD-17): calling Tick from inside
        /// a listener is ignored rather than corrupting the loop.
        /// </summary>
        public void Tick()
        {
            if (_ticking) return;
            _ticking = true;
            try
            {
                _tickSourceCount = 0;

                // Index loop, not foreach: the collection must not be structurally mutated between
                // the first and last index, and Play/Cancel from a listener are both possible (AD-17).
                for (int i = 0; i < _activeSlots.Count; i++)
                {
                    EffectSlot slot = _slots[_activeSlots[i]];
                    if (!slot.Active || slot.ReleaseRequested) continue;
                    TickSlot(slot, _activeSlots[i]);
                }
            }
            finally
            {
                _ticking = false;
            }

            // Tick boundary: retire finished slots, then admit effects created during the tick.
            FlushReleases();
            FlushPending();
        }

        private void TickSlot(EffectSlot slot, int slotIndex)
        {
            if (slot.CancellationToken.IsCancellationRequested)
            {
                RequestRelease(slot, slotIndex, A2BCompletionReason.Cancelled);
                return;
            }

            float dt = ResolveDelta(slot.TimeSource);
            A2BEffectDefinition def = slot.Definition;

            if (!slot.StartedRaised)
            {
                slot.StartedRaised = true;
                RaiseStarted(slot, slotIndex);
                if (slot.ReleaseRequested) return;
            }

            // Endpoints resolve ONCE per effect per frame, not once per item — the difference between
            // 1 and N Transform reads for a 200-item burst (AD-4).
            A2BEndpointSample originSample = SafeResolve(slot.Origin);
            A2BEndpointSample destSample = SafeResolve(slot.Destination);

            if (originSample.IsValid) { slot.LastKnownOrigin = originSample; slot.HasLastKnownOrigin = true; }
            if (destSample.IsValid) { slot.LastKnownDestination = destSample; slot.HasLastKnownDestination = true; }

            bool originLost = !originSample.IsValid;
            bool destLost = !destSample.IsValid;

            if (originLost || destLost)
            {
                bool canFallBack = def.EndpointLostPolicy == A2BEndpointLostPolicy.UseLastKnownPosition
                                   && slot.HasLastKnownOrigin && slot.HasLastKnownDestination;
                if (!canFallBack)
                {
                    // A destroyed target must never surface as a MissingReferenceException in
                    // gameplay code — it degrades to a clean cancel (AD-8, FR-13).
                    RequestRelease(slot, slotIndex, A2BCompletionReason.EndpointLost);
                    return;
                }
            }

            // Fall back to the last known sample verbatim — including its space. Rebuilding it as a
            // world sample would mis-project a screen-space endpoint on the frame it is lost.
            A2BEndpointSample originResolved = originSample.IsValid ? originSample : slot.LastKnownOrigin;
            A2BEndpointSample destResolved = destSample.IsValid ? destSample : slot.LastKnownDestination;

            IA2BPresenter presenter = slot.Presenter;
            Vector3 originWorking = presenter.ToWorkingSpace(in originResolved);
            Vector3 destWorking = presenter.ToWorkingSpace(in destResolved);

            // Read through the port, never downcast: a custom IA2BEmission must get its scatter too
            // (FR-10), and a type check has no business in the tick path (AD-3).
            float scatterRadius = def.Emission.ScatterRadius;

            for (int i = 0; i < slot.ItemCount; i++)
            {
                ref ItemState item = ref slot.Items[i];
                if (item.Arrived) continue;

                item.Elapsed += dt;
                if (item.Elapsed < item.Delay) continue;

                if (!item.Spawned)
                {
                    var info = new A2BItemSpawnInfo(i, slot.ItemCount, item.Seed, slot.Text, slot.Value);
                    item.PresenterItemId = presenter.Acquire(in info);
                    item.Spawned = true;
                    slot.SpawnedCount++;
                    RaiseItemSpawned(slot, slotIndex, i);
                    if (slot.ReleaseRequested) return;
                }

                float travelled = item.Elapsed - item.Delay;
                float t = item.Duration <= 0f ? 1f : Mathf.Clamp01(travelled / item.Duration);
                float eased = def.Easing.Evaluate(t);

                // Scatter is unitless from emission; the presenter assigns units, because only it
                // knows whether "radius" means pixels or metres (AD-16).
                Vector3 scatterOffset = scatterRadius > 0f
                    ? presenter.ScaleScatter(in item.ScatterUnit, scatterRadius)
                    : Vector3.zero;

                // Scatter decays as the item converges, so every item still lands ON the destination
                // and the t>=1 arrival rule stays honest (AD-13).
                Vector3 scatteredOrigin = originWorking + scatterOffset;
                var ctx = new A2BPathContext(scatteredOrigin, destWorking, i, slot.ItemCount, item.Seed);
                Vector3 position = def.Path.Evaluate(in ctx, eased);

                Vector3 scale = Vector3.one * (def.ScaleOverProgress?.Evaluate(t) ?? 1f);
                Color color = def.ColorOverProgress?.Evaluate(t) ?? Color.white;

                Quaternion rotation = Quaternion.identity;
                if (def.AlignToVelocity)
                {
                    Vector3 delta = position - item.LastPosition;
                    if (item.Spawned && delta.sqrMagnitude > 1e-8f)
                        rotation = Quaternion.LookRotation(delta.normalized, Vector3.up);
                }
                item.LastPosition = position;

                var visual = new A2BVisualState(position, rotation, scale, color, t);
                presenter.Apply(item.PresenterItemId, in visual);

                // Arrival is t >= 1 and only t >= 1 — never distance-to-destination. The path is
                // pinned to the destination at t=1 (AD-13), so a distance test is redundant, and
                // with a moving destination the two definitions fire on different frames (AD-13).
                if (t >= 1f)
                {
                    item.Arrived = true;
                    slot.ArrivedCount++;
                    presenter.Release(item.PresenterItemId, A2BReleaseReason.Arrived);
                    item.PresenterItemId = -1;

                    if (!slot.FirstArrivedRaised)
                    {
                        slot.FirstArrivedRaised = true;
                        RaiseFirstItemArrived(slot, slotIndex, i);
                        if (slot.ReleaseRequested) return;
                    }

                    RaiseItemArrived(slot, slotIndex, i);
                    if (slot.ReleaseRequested) return;
                }
            }

            if (slot.ArrivedCount >= slot.ItemCount)
                RequestRelease(slot, slotIndex, A2BCompletionReason.Completed);
        }

        /// <summary>Reads each distinct time source exactly once per tick (AD-6).</summary>
        private float ResolveDelta(IA2BTimeSource source)
        {
            if (source == null) return 0f;

            for (int i = 0; i < _tickSourceCount; i++)
                if (ReferenceEquals(_tickSources[i], source))
                    return _tickDeltas[i];

            float delta = source.DeltaTime;
            if (_tickSourceCount < _tickSources.Length)
            {
                _tickSources[_tickSourceCount] = source;
                _tickDeltas[_tickSourceCount] = delta;
                _tickSourceCount++;
            }
            return delta;
        }

        private static A2BEndpointSample SafeResolve(IA2BEndpointProvider provider)
        {
            // A provider is user-extensible code; a throw here must not escape into gameplay (AD-8).
            try
            {
                return provider.Resolve();
            }
            catch (Exception e)
            {
                A2BLog.Exception(null, e);
                return A2BEndpointSample.Invalid;
            }
        }

        // ---- lifecycle ------------------------------------------------------------------------

        private int AcquireSlot()
        {
            if (_freeSlots.Count > 0)
            {
                int index = _freeSlots[_freeSlots.Count - 1];
                _freeSlots.RemoveAt(_freeSlots.Count - 1);
                return index;
            }
            _slots.Add(new EffectSlot());
            return _slots.Count - 1;
        }

        private void RequestRelease(EffectSlot slot, int slotIndex, A2BCompletionReason reason)
        {
            if (slot.ReleaseRequested) return;
            slot.ReleaseRequested = true;
            slot.PendingReason = reason;

            // Bump immediately so stale handles are invalid the instant the effect ends, even though
            // the slot itself is not reusable until the tick boundary (AD-7 + AD-17).
            slot.Generation++;

            if (!_ticking) FlushReleases();
        }

        private void FlushReleases()
        {
            for (int i = _activeSlots.Count - 1; i >= 0; i--)
            {
                int index = _activeSlots[i];
                EffectSlot slot = _slots[index];
                if (!slot.ReleaseRequested) continue;

                _activeSlots.RemoveAt(i);
                ReleaseEffect(slot, index, slot.PendingReason);
            }
        }

        private void FlushPending()
        {
            for (int i = 0; i < _pendingSlots.Count; i++)
                _activeSlots.Add(_pendingSlots[i]);
            _pendingSlots.Clear();
        }

        /// <summary>
        /// The single exit (AD-9). Completion, cancellation, a lost endpoint, and a listener throwing
        /// all funnel here — which is what makes "exactly one of Completed/Cancelled, never both,
        /// never neither" true by construction rather than by discipline, and why no terminal path
        /// can leak a pooled item.
        /// </summary>
        private void ReleaseEffect(EffectSlot slot, int slotIndex, A2BCompletionReason reason)
        {
            // Return every still-live item before anything else can go wrong.
            for (int i = 0; i < slot.ItemCount; i++)
            {
                ref ItemState item = ref slot.Items[i];
                if (item.Spawned && !item.Arrived && item.PresenterItemId >= 0)
                {
                    // Cancelled, not Arrived: these items never landed, so arrival feedback must not
                    // fire for them (AD-9 funnels every terminal path through here).
                    try { slot.Presenter?.Release(item.PresenterItemId, A2BReleaseReason.Cancelled); }
                    catch (Exception e) { A2BLog.Exception(null, e); }
                    item.PresenterItemId = -1;
                }
            }

            // The handle passed to terminal events uses the PRE-bump generation, so listeners can
            // still read ItemCount/ArrivedCount off it. It is inert for mutation either way.
            var handle = new A2BEffectHandle(this, slotIndex, slot.Generation - 1);

            if (reason == A2BCompletionReason.Completed) RaiseCompleted(slot, in handle);
            else RaiseCancelled(slot, in handle, reason);

            AutoResetUniTaskCompletionSource<A2BCompletionReason> completion = slot.Completion;
            slot.ResetForReuse();
            _freeSlots.Add(slotIndex);

            // Resolved last: an awaiting continuation may Play() again and reuse this very slot.
            completion?.TrySetResult(reason);
        }

        internal void Cancel(int slotIndex, uint generation)
        {
            EffectSlot slot = Resolve(slotIndex, generation);
            if (slot == null) return;
            RequestRelease(slot, slotIndex, A2BCompletionReason.Cancelled);
        }

        /// <summary>Cancels every running effect. Used on teardown and domain reload (NFR-5).</summary>
        public void CancelAll()
        {
            for (int i = _activeSlots.Count - 1; i >= 0; i--)
            {
                int index = _activeSlots[i];
                EffectSlot slot = _slots[index];
                if (slot.Active && !slot.ReleaseRequested)
                    RequestRelease(slot, index, A2BCompletionReason.Cancelled);
            }
            if (!_ticking) { FlushReleases(); FlushPending(); }
        }

        // ---- handle resolution ----------------------------------------------------------------

        private EffectSlot Resolve(int slotIndex, uint generation)
        {
            if (slotIndex < 0 || slotIndex >= _slots.Count) return null;
            EffectSlot slot = _slots[slotIndex];
            // The stamp check. Everything about handle safety reduces to this line (AD-7).
            if (!slot.Active || slot.Generation != generation) return null;
            return slot;
        }

        internal bool IsHandleValid(int slotIndex, uint generation) => Resolve(slotIndex, generation) != null;

        internal int GetArrivedCount(int slotIndex, uint generation) => Resolve(slotIndex, generation)?.ArrivedCount ?? 0;

        internal int GetItemCount(int slotIndex, uint generation) => Resolve(slotIndex, generation)?.ItemCount ?? 0;

        internal void AddListener(int slotIndex, uint generation, IA2BEffectListener listener)
        {
            if (listener == null) return;
            EffectSlot slot = Resolve(slotIndex, generation);
            slot?.Listeners.Add(listener);
        }

        internal void RemoveListener(int slotIndex, uint generation, IA2BEffectListener listener)
        {
            if (listener == null) return;
            EffectSlot slot = Resolve(slotIndex, generation);
            slot?.Listeners.Remove(listener);
        }

        internal UniTask<A2BCompletionReason> AwaitCompletion(int slotIndex, uint generation, CancellationToken cancellationToken)
        {
            EffectSlot slot = Resolve(slotIndex, generation);

            // Already finished (or never started): resolve immediately rather than hanging forever.
            if (slot == null) return UniTask.FromResult(A2BCompletionReason.Invalid);

            if (cancellationToken.CanBeCanceled && slot.CancellationToken == default)
                slot.CancellationToken = cancellationToken;

            slot.Completion ??= AutoResetUniTaskCompletionSource<A2BCompletionReason>.Create();
            return slot.Completion.Task;
        }

        // ---- event dispatch -------------------------------------------------------------------
        //
        // Index loops and plain interface calls: no delegate combination, no params arrays, no
        // boxing (AD-11). Each callback is isolated so one bad listener cannot leak pooled items or
        // suppress the rest of the event set (AD-8).

        private void RaiseStarted(EffectSlot slot, int slotIndex)
        {
            var handle = new A2BEffectHandle(this, slotIndex, slot.Generation);
            for (int i = 0; i < slot.Listeners.Count; i++)
            {
                try { slot.Listeners[i].OnStarted(in handle); }
                catch (Exception e) { A2BLog.Exception(null, e); }
            }
        }

        private void RaiseItemSpawned(EffectSlot slot, int slotIndex, int itemIndex)
        {
            var handle = new A2BEffectHandle(this, slotIndex, slot.Generation);
            for (int i = 0; i < slot.Listeners.Count; i++)
            {
                try { slot.Listeners[i].OnItemSpawned(in handle, itemIndex); }
                catch (Exception e) { A2BLog.Exception(null, e); }
            }
        }

        private void RaiseFirstItemArrived(EffectSlot slot, int slotIndex, int itemIndex)
        {
            var handle = new A2BEffectHandle(this, slotIndex, slot.Generation);
            for (int i = 0; i < slot.Listeners.Count; i++)
            {
                try { slot.Listeners[i].OnFirstItemArrived(in handle, itemIndex); }
                catch (Exception e) { A2BLog.Exception(null, e); }
            }
        }

        private void RaiseItemArrived(EffectSlot slot, int slotIndex, int itemIndex)
        {
            var handle = new A2BEffectHandle(this, slotIndex, slot.Generation);
            for (int i = 0; i < slot.Listeners.Count; i++)
            {
                try { slot.Listeners[i].OnItemArrived(in handle, itemIndex); }
                catch (Exception e) { A2BLog.Exception(null, e); }
            }
        }

        private void RaiseCompleted(EffectSlot slot, in A2BEffectHandle handle)
        {
            for (int i = 0; i < slot.Listeners.Count; i++)
            {
                try { slot.Listeners[i].OnCompleted(in handle); }
                catch (Exception e) { A2BLog.Exception(null, e); }
            }
        }

        private void RaiseCancelled(EffectSlot slot, in A2BEffectHandle handle, A2BCompletionReason reason)
        {
            for (int i = 0; i < slot.Listeners.Count; i++)
            {
                try { slot.Listeners[i].OnCancelled(in handle, reason); }
                catch (Exception e) { A2BLog.Exception(null, e); }
            }
        }

        private uint _seedCounter = 0x2545F491u;

        private uint NextSeed()
        {
            unchecked
            {
                _seedCounter = _seedCounter * 1664525u + 1013904223u;
                return _seedCounter == 0u ? 0x9E3779B9u : _seedCounter;
            }
        }
    }
}
