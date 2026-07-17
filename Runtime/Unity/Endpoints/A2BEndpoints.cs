using A2BKit.Core;
using UnityEngine;

namespace A2BKit.Unity
{
    /// <summary>
    /// Follows a Transform. Resolved every frame, never cached at play — which is what makes a
    /// moving target the default case rather than the edge case (FR-12).
    ///
    /// A destroyed target resolves as invalid rather than throwing a MissingReferenceException into
    /// gameplay code (FR-13, AD-8). Note the `== null` comparison: that is Unity's fake-null lifetime
    /// check, and using `is null` here would miss a destroyed object entirely.
    /// </summary>
    public sealed class A2BTransformEndpoint : IA2BEndpointProvider
    {
        public Transform Target;
        public Vector3 WorldOffset;

        public A2BTransformEndpoint() { }

        public A2BTransformEndpoint(Transform target, Vector3 worldOffset = default)
        {
            Target = target;
            WorldOffset = worldOffset;
        }

        public A2BEndpointSample Resolve()
        {
            if (Target == null) return A2BEndpointSample.Invalid;
            return A2BEndpointSample.At(Target.position + WorldOffset);
        }
    }

    /// <summary>
    /// Follows a RectTransform's centre — the wallet HUD case. Uses the rect centre rather than the
    /// pivot, so an anchored or animating HUD still lands the coins on the icon rather than its
    /// bottom-left corner.
    /// </summary>
    public sealed class A2BRectTransformEndpoint : IA2BEndpointProvider
    {
        public RectTransform Target;
        public Vector2 LocalOffset;

        public A2BRectTransformEndpoint() { }

        public A2BRectTransformEndpoint(RectTransform target, Vector2 localOffset = default)
        {
            Target = target;
            LocalOffset = localOffset;
        }

        /// <summary>
        /// Reports a SCREEN point, not a world position — this is the fix for the canonical
        /// coin-to-wallet path.
        ///
        /// A RectTransform on a screen-space canvas has no meaningful world position: its `position`
        /// is already screen pixels (~960, 540, 0). Returning that as a world sample made the canvas
        /// adapter run Camera.WorldToScreenPoint over it, projecting a point 960 world-units away, and
        /// the coins missed the wallet entirely. RectTransformUtility.WorldToScreenPoint is the
        /// correct API and handles every render mode, including a world-space canvas.
        /// </summary>
        public A2BEndpointSample Resolve()
        {
            if (Target == null) return A2BEndpointSample.Invalid;

            Vector3 localCentre = Target.rect.center + LocalOffset;
            Vector3 world = Target.TransformPoint(localCentre);

            Canvas canvas = Target.GetComponentInParent<Canvas>();
            if (canvas == null)
            {
                // A RectTransform outside any canvas really is just a world transform.
                return A2BEndpointSample.At(world);
            }

            Camera cam = canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : canvas.worldCamera;
            Vector2 screen = RectTransformUtility.WorldToScreenPoint(cam, world);
            return A2BEndpointSample.AtScreen(new Vector3(screen.x, screen.y, 0f));
        }
    }

    /// <summary>
    /// A fixed world point that can still be moved by the caller between plays.
    /// </summary>
    public sealed class A2BWorldPointEndpoint : IA2BEndpointProvider
    {
        public Vector3 WorldPosition;

        public A2BWorldPointEndpoint() { }
        public A2BWorldPointEndpoint(Vector3 worldPosition) { WorldPosition = worldPosition; }

        public A2BEndpointSample Resolve() => A2BEndpointSample.At(WorldPosition);
    }

    /// <summary>
    /// Follows a screen position (e.g. a touch point), projected into world space at a fixed depth.
    /// </summary>
    public sealed class A2BScreenPointEndpoint : IA2BEndpointProvider
    {
        public Vector2 ScreenPosition;
        public Camera Camera;
        public float Depth = 10f;

        public A2BEndpointSample Resolve()
        {
            Camera cam = Camera != null ? Camera : Camera.main;
            if (cam == null) return A2BEndpointSample.Invalid;
            Vector3 world = cam.ScreenToWorldPoint(new Vector3(ScreenPosition.x, ScreenPosition.y, Depth));
            return A2BEndpointSample.At(world);
        }
    }
}
