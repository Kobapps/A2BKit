using System;
using System.Collections.Generic;
using A2BKit.Core;
using NUnit.Framework;
using UnityEngine;

namespace A2BKit.Tests.EditMode
{
    /// <summary>Records the exact event sequence so ordering rules (FR-14) are assertable, not assumed.</summary>
    internal sealed class RecordingListener : A2BEffectListenerBase
    {
        public const string Started = "Started";
        public const string ItemSpawned = "ItemSpawned";
        public const string FirstItemArrived = "FirstItemArrived";
        public const string ItemArrived = "ItemArrived";
        public const string Completed = "Completed";
        public const string Cancelled = "Cancelled";

        public readonly List<string> Events = new List<string>();
        public readonly List<int> ArrivedIndices = new List<int>();
        public readonly List<int> SpawnedIndices = new List<int>();

        public int StartedCount;
        public int SpawnedCount;
        public int FirstArrivedCount;
        public int ArrivedCount;
        public int CompletedCount;
        public int CancelledCount;

        public int FirstArrivedIndex = -1;
        public A2BCompletionReason LastCancelReason = A2BCompletionReason.Invalid;

        /// <summary>Set by the fixture before each Tick so tests can assert WHICH tick an event landed on.</summary>
        public TickClock Clock;
        public int StartedTick = -1;
        public int CompletedTick = -1;

        public int TerminalCount => CompletedCount + CancelledCount;

        public override void OnStarted(in A2BEffectHandle handle)
        {
            StartedCount++;
            StartedTick = Clock?.Tick ?? -1;
            Events.Add(Started);
        }

        public override void OnItemSpawned(in A2BEffectHandle handle, int itemIndex)
        {
            SpawnedCount++;
            SpawnedIndices.Add(itemIndex);
            Events.Add(ItemSpawned);
        }

        public override void OnFirstItemArrived(in A2BEffectHandle handle, int itemIndex)
        {
            FirstArrivedCount++;
            FirstArrivedIndex = itemIndex;
            Events.Add(FirstItemArrived);
        }

        public override void OnItemArrived(in A2BEffectHandle handle, int itemIndex)
        {
            ArrivedCount++;
            ArrivedIndices.Add(itemIndex);
            Events.Add(ItemArrived);
        }

        public override void OnCompleted(in A2BEffectHandle handle)
        {
            CompletedCount++;
            CompletedTick = Clock?.Tick ?? -1;
            Events.Add(Completed);
        }

        public override void OnCancelled(in A2BEffectHandle handle, A2BCompletionReason reason)
        {
            CancelledCount++;
            LastCancelReason = reason;
            Events.Add(Cancelled);
        }
    }

    /// <summary>A listener that throws from every callback. AD-8 says it must be contained.</summary>
    internal sealed class ThrowingListener : A2BEffectListenerBase
    {
        public int CallCount;

        public override void OnStarted(in A2BEffectHandle handle) => Boom();
        public override void OnItemSpawned(in A2BEffectHandle handle, int itemIndex) => Boom();
        public override void OnFirstItemArrived(in A2BEffectHandle handle, int itemIndex) => Boom();
        public override void OnItemArrived(in A2BEffectHandle handle, int itemIndex) => Boom();
        public override void OnCompleted(in A2BEffectHandle handle) => Boom();
        public override void OnCancelled(in A2BEffectHandle handle, A2BCompletionReason reason) => Boom();

        private void Boom()
        {
            CallCount++;
            throw new InvalidOperationException("Deliberate listener failure (AD-8 containment test).");
        }
    }

    /// <summary>An endpoint that can be invalidated mid-flight — a destroyed target, without a scene (FR-13).</summary>
    internal sealed class ToggleEndpoint : IA2BEndpointProvider
    {
        public Vector3 WorldPosition;
        public bool IsValid = true;
        public int ResolveCount;

        public ToggleEndpoint(Vector3 worldPosition) => WorldPosition = worldPosition;

        public A2BEndpointSample Resolve()
        {
            ResolveCount++;
            return IsValid ? A2BEndpointSample.At(WorldPosition) : A2BEndpointSample.Invalid;
        }
    }

    /// <summary>An endpoint provider that throws. AD-8: a throw here must degrade to "invalid", not escape.</summary>
    internal sealed class ThrowingEndpoint : IA2BEndpointProvider
    {
        public A2BEndpointSample Resolve() => throw new InvalidOperationException("Deliberate endpoint failure.");
    }

    /// <summary>Shared mutable tick counter, so listeners can record the tick an event landed on.</summary>
    internal sealed class TickClock
    {
        public int Tick;
    }

    /// <summary>Common fixture helpers. Every test builds its own scheduler — no bootstrapping, no A2BRunner.</summary>
    internal static class A2BTestHarness
    {
        public const float Dt = 0.1f;
        public const float Duration = 0.4f;

        /// <summary>A fully deterministic definition: no jitter, no scatter, linear path, linear easing.</summary>
        public static A2BEffectDefinition Deterministic(int count = 4, float duration = Duration)
            => A2BEffectBuilder.Create()
                .Linear()
                .Ease(A2BEaseKind.Linear)
                .Duration(duration)
                .DurationJitter(0f)
                .Count(count)
                .AllAtOnce()
                .Scatter(0f)
                .Build();

        /// <summary>Advances the injected clock and ticks until every effect retires. Fails rather than hangs.</summary>
        public static int RunToCompletion(A2BScheduler scheduler, A2BManualTimeSource time, TickClock clock = null,
            float dt = Dt, int maxTicks = 1000)
        {
            int ticks = 0;
            while (scheduler.ActiveEffectCount > 0)
            {
                if (ticks >= maxTicks)
                {
                    Assert.Fail("Effects did not retire within " + maxTicks + " ticks.");
                    break;
                }
                Step(scheduler, time, clock, dt);
                ticks++;
            }
            return ticks;
        }

        public static void Step(A2BScheduler scheduler, A2BManualTimeSource time, TickClock clock = null, float dt = Dt)
        {
            if (clock != null) clock.Tick++;
            time.Advance(dt);
            scheduler.Tick();
        }
    }
}
