using System.Collections.Generic;
using A2BKit.Core;
using UnityEngine;
using UnityEngine.Pool;

namespace A2BKit.Unity
{
    /// <summary>
    /// Pooled one-shot <see cref="AudioSource"/>s for spawn and arrival, with the rising pitch that
    /// makes a coin cascade feel like a reward instead of a rattle.
    ///
    /// **The rising pitch has no counter, and that is the whole design.** The obvious implementation
    /// — a <c>_landedCount</c> field bumped on each arrival — is broken here for a reason that is
    /// easy to miss and impossible to hear until it ships: a feedback instance is cloned per
    /// *presenter*, and a presenter is shared by every concurrent play of its effect. Two bursts
    /// overlapping would share one counter, so the second burst would start where the first left off
    /// (already at the ceiling, sounding flat) and the first would keep climbing on the second's
    /// coins. The counter would also never reset, because no hook says "an effect ended" — only
    /// items are visible from here.
    ///
    /// So the ramp is a pure function of <c>A2BItemSpawnInfo.ItemIndex</c>, which is per-effect by
    /// construction: item 7 of burst A and item 0 of burst B compute their own pitches and cannot
    /// contaminate each other. This is AD-10's rule ("variation is computed from (seed, index), never
    /// stored") applied to sound, and it buys determinism for free — the same play makes the same
    /// noise, which is what makes it testable (NFR-4). The base pitch jitter comes from
    /// <see cref="A2BRandom"/> seeded with the item's own seed for the same reason;
    /// <c>UnityEngine.Random</c> is banned package-wide as global mutable state.
    ///
    /// The one thing that *must* be stored is the index itself: <see cref="Arrived"/> is handed a
    /// Transform and a state, not the spawn info, so the index is parked in a table at spawn and read
    /// back on arrival. That is per-item state, not shared state — it cannot be contaminated by a
    /// concurrent effect, because the Transform key is unique while the item is alive.
    ///
    /// Voices are 2D (<c>spatialBlend = 0</c>). A 3D voice would have to be positioned, and the only
    /// position available here is working space — which on a Canvas effect is canvas-local pixels,
    /// and panning a coin by its pixel X is not audio design, it is an accident. A project wanting
    /// positional audio should drive it from a prefab payload's own AudioSource.
    /// </summary>
    [System.Serializable]
    public sealed class A2BAudioFeedback : A2BFeedbackBase, IA2BFeedbackPumped
    {
        /// <summary>Played as each item appears. Null plays nothing.</summary>
        public AudioClip SpawnClip;

        /// <summary>Played as each item LANDS — arrivals only, never on a cancelled item.</summary>
        public AudioClip ArriveClip;

        /// <summary>Linear volume for every voice.</summary>
        public float Volume = 1f;

        /// <summary>Base pitch jitter, min to max. Kills the machine-gun sameness of one repeated clip.</summary>
        public Vector2 PitchRange = new Vector2(0.95f, 1.05f);

        /// <summary>Pitch added per item index — the rising cascade. 0 disables the ramp.</summary>
        public float PitchRampPerItem = 0.02f;

        /// <summary>
        /// Hard ceiling on simultaneous voices. 200 coins landing over three frames would otherwise
        /// stack 200 correlated copies of one clip: that is not louder, it is clipped and muddy, and
        /// it costs 200 voices to sound worse than eight.
        /// </summary>
        public int MaxConcurrent = 16;

        /// <summary>One playing voice, aged by the pump. A struct, so no allocation per sound (AD-3).</summary>
        private struct Voice
        {
            public Transform Root;
            public AudioSource Source;
            public float Remaining;
        }

        /// <summary>What arrival needs but is not given: which item of which effect this is.</summary>
        private struct ItemVoice
        {
            public int Index;
            public uint Seed;
        }

        // Runtime state — built in OnInitialized, never in a field initializer, or two clones of this
        // prototype would share one AudioSource pool (AD-14).
        private ObjectPool<Transform> _pool;
        private Transform _poolRoot;
        private List<Transform> _all;
        private Dictionary<Transform, AudioSource> _sources;
        private Dictionary<Transform, ItemVoice> _items;
        private List<Voice> _live;
        private bool _pumped;
        private bool _disposed;

        public override string FeedbackKey => "Audio";

        protected override void OnInitialized()
        {
            _disposed = false;

            int capacity = Mathf.Max(1, MaxConcurrent);

            _all = new List<Transform>(capacity);
            _sources = new Dictionary<Transform, AudioSource>(capacity, A2BTransformComparer.Instance);
            _items = new Dictionary<Transform, ItemVoice>(32, A2BTransformComparer.Instance);
            _live = new List<Voice>(capacity);

            if (SpawnClip == null && ArriveClip == null)
            {
                A2BLog.Error(null, "A2BAudioFeedback has no SpawnClip and no ArriveClip; it will play nothing.");
                return;
            }

            if (Root == null)
            {
                A2BLog.Error(null, "A2BAudioFeedback initialized with a null root; it will play nothing.");
                return;
            }

            // Under the adapter Root, so tearing the effect's hierarchy down takes the voices with it
            // rather than leaving AudioSources playing over a dead scene.
            var poolRootObject = new GameObject("A2B Audio Pool");
            poolRootObject.SetActive(false);
            _poolRoot = poolRootObject.transform;
            _poolRoot.SetParent(Root, false);

            _pool = new ObjectPool<Transform>(
                createFunc: CreateVoice,
                actionOnGet: null,
                actionOnRelease: null,
                actionOnDestroy: DestroyVoice,
#if UNITY_EDITOR
                collectionCheck: true,
#else
                collectionCheck: false,
#endif
                defaultCapacity: capacity,
                // The pool never needs more than the concurrency ceiling: a voice is only handed out
                // while it is audible, and MaxConcurrent bounds how many that can be at once.
                maxSize: capacity);

            A2BFeedbackPump.Register(this);
            _pumped = true;
        }

        protected override void Spawned(Transform item, in A2BItemSpawnInfo info)
        {
            if (_items == null || _disposed) return;

            var voice = new ItemVoice { Index = info.ItemIndex, Seed = info.Seed };
            _items[item] = voice;

            if (SpawnClip != null) Play(SpawnClip, PitchFor(in voice));
        }

        protected override void Arrived(Transform item, in A2BVisualState finalState)
        {
            if (_items == null || ArriveClip == null || _disposed) return;

            // Absent index means the item spawned while this feedback was disabled. Pitch from index
            // 0 rather than skipping the sound: a silent landing is a worse bug than a flat one.
            ItemVoice voice = _items.TryGetValue(item, out ItemVoice found) ? found : default;
            Play(ArriveClip, PitchFor(in voice));
        }

        protected override void Released(Transform item, A2BReleaseReason reason)
        {
            // The Transform goes back to the payload pool and will key a different item next time;
            // leaving the entry would both leak and mis-pitch that future item.
            _items?.Remove(item);
        }

        /// <summary>
        /// Retires voices whose clip has finished. This exists for the same reason
        /// <see cref="A2BImpactFeedback"/> is pumped: the last coin's "ting" is started as its item
        /// is released, after which no item is live and no feedback hook will ever run again. Ageing
        /// voices from <c>OnItemUpdated</c> would strand exactly that source — permanently handed
        /// out, never released, one voice of the ceiling gone for the rest of the session, and after
        /// enough effects the audio silently stops (AD-9).
        ///
        /// Always unscaled: an AudioSource plays in real seconds and ignores <c>Time.timeScale</c>,
        /// so ageing a voice on scaled time would recycle a still-audible source during slow motion
        /// and cut the sound off mid-clip.
        /// </summary>
        void IA2BFeedbackPumped.PumpTick()
        {
            if (_live == null || _live.Count == 0 || _disposed) return;

            float delta = UnityEngine.Time.unscaledDeltaTime;

            // Backwards, so retiring by swapping the tail into the slot cannot skip a neighbour.
            for (int i = _live.Count - 1; i >= 0; i--)
            {
                Voice voice = _live[i];
                voice.Remaining -= delta;

                if (voice.Remaining > 0f && voice.Root != null && voice.Source != null)
                {
                    _live[i] = voice;
                    continue;
                }

                int last = _live.Count - 1;
                _live[i] = _live[last];
                _live.RemoveAt(last);
                Retire(in voice);
            }
        }

        protected override void Disposed()
        {
            // Tolerates a second call: teardown and domain reload can both land here (NFR-5).
            if (_disposed) return;
            _disposed = true;

            if (_pumped)
            {
                A2BFeedbackPump.Unregister(this);
                _pumped = false;
            }

            _live?.Clear();
            _items?.Clear();

            if (_pool != null)
            {
                _pool.Clear();
                _pool = null;
            }

            // Voices still playing were handed out, so the pool does not know them; _all does.
            if (_all != null)
            {
                for (int i = _all.Count - 1; i >= 0; i--)
                {
                    Transform voice = _all[i];
                    if (voice != null) A2BFeedbackKit.Destroy(voice.gameObject);
                }
                _all.Clear();
            }

            _sources?.Clear();

            if (_poolRoot != null)
            {
                A2BFeedbackKit.Destroy(_poolRoot.gameObject);
                _poolRoot = null;
            }
        }

        /// <summary>
        /// Pitch for one item: jitter from its own seed, plus the ramp from its own index. Pure —
        /// no field is read or written, which is what makes two concurrent bursts independent.
        /// </summary>
        private float PitchFor(in ItemVoice voice)
        {
            var random = new A2BRandom(voice.Seed);
            float min = Mathf.Min(PitchRange.x, PitchRange.y);
            float max = Mathf.Max(PitchRange.x, PitchRange.y);

            float pitch = random.NextFloat(min, max) + PitchRampPerItem * voice.Index;

            // AudioSource accepts -3..3; a pitch at or below zero would also make the voice's
            // remaining-time estimate infinite or negative and strand the source (AD-8: clamp,
            // don't throw).
            return Mathf.Clamp(pitch, 0.05f, 3f);
        }

        private void Play(AudioClip clip, float pitch)
        {
            if (_pool == null || clip == null) return;

            // At the ceiling the newest sound is dropped rather than stealing an older voice. Cutting
            // a clip the player is currently hearing is audible; not adding the 17th correlated copy
            // of the same clip in the same frame is not.
            if (_live.Count >= Mathf.Max(1, MaxConcurrent)) return;

            Transform root = _pool.Get();
            if (root == null) return;

            if (!_sources.TryGetValue(root, out AudioSource source) || source == null)
            {
                _pool.Release(root);
                return;
            }

            root.SetParent(Root, false);
            root.localPosition = Vector3.zero;
            root.gameObject.SetActive(true);

            source.clip = clip;
            source.pitch = pitch;
            source.volume = Mathf.Max(0f, Volume);
            source.Play();

            // Length in real seconds: pitch is a playback rate, so a voice at 2.0 finishes in half
            // the clip's authored length. Estimating this beats polling isPlaying, which is an
            // interop call per voice per frame.
            float remaining = clip.length / pitch;

            _live.Add(new Voice { Root = root, Source = source, Remaining = remaining });
        }

        private void Retire(in Voice voice)
        {
            if (_pool == null || voice.Root == null) return;

            if (voice.Source != null)
            {
                voice.Source.Stop();

                // Dropping the clip reference matters: a pooled AudioSource holding the last clip
                // keeps it loaded for the life of the pool.
                voice.Source.clip = null;
            }

            voice.Root.gameObject.SetActive(false);
            voice.Root.SetParent(_poolRoot, false);
            _pool.Release(voice.Root);
        }

        private Transform CreateVoice()
        {
            var instance = new GameObject("A2B Voice");
            var source = instance.AddComponent<AudioSource>();

            source.playOnAwake = false;
            source.loop = false;
            source.spatialBlend = 0f;   // 2D — see the class remarks.
            source.volume = Mathf.Max(0f, Volume);

            Transform root = instance.transform;
            root.gameObject.SetActive(false);
            root.SetParent(_poolRoot, false);

            _all.Add(root);
            _sources[root] = source;
            return root;
        }

        private void DestroyVoice(Transform voice)
        {
            if (voice == null) return;

            _all?.Remove(voice);
            _sources?.Remove(voice);
            A2BFeedbackKit.Destroy(voice.gameObject);
        }
    }
}
