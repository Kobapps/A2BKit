using System.Threading;
using UnityEngine;

namespace A2BKit.Core
{
    /// <summary>
    /// Everything that varies per play, as opposed to per definition. Passed by <c>in</c>.
    ///
    /// Endpoints live here rather than on the definition on purpose: a definition is a reusable asset
    /// and must not bake scene references (FR-1).
    /// </summary>
    public readonly struct A2BPlayArgs
    {
        public readonly IA2BEndpointProvider Origin;
        public readonly IA2BEndpointProvider Destination;
        public readonly IA2BPresenter Presenter;

        /// <summary>Optional text for text payloads (e.g. "+250").</summary>
        public readonly string Text;

        /// <summary>Optional numeric value; text payloads format it without allocating (AD-20).</summary>
        public readonly float Value;

        /// <summary>Seed for all per-item variation. Pass a fixed value to make a play reproducible (NFR-4).</summary>
        public readonly uint Seed;

        public readonly CancellationToken CancellationToken;

        public A2BPlayArgs(
            IA2BEndpointProvider origin,
            IA2BEndpointProvider destination,
            IA2BPresenter presenter,
            string text = null,
            float value = 0f,
            uint seed = 0u,
            CancellationToken cancellationToken = default)
        {
            Origin = origin;
            Destination = destination;
            Presenter = presenter;
            Text = text;
            Value = value;
            Seed = seed;
            CancellationToken = cancellationToken;
        }
    }

    /// <summary>An endpoint that never moves.</summary>
    public sealed class A2BStaticEndpoint : IA2BEndpointProvider
    {
        public Vector3 WorldPosition;

        public A2BStaticEndpoint() { }
        public A2BStaticEndpoint(Vector3 worldPosition) { WorldPosition = worldPosition; }

        public A2BEndpointSample Resolve() => A2BEndpointSample.At(WorldPosition);
    }
}
