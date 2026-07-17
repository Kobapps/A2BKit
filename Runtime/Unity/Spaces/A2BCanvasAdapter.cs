using A2BKit.Core;
using UnityEngine;

namespace A2BKit.Unity
{
    /// <summary>
    /// Canvas space, for all three render modes (Overlay, Screen-Space-Camera, World-Space canvas).
    ///
    /// This class exists so that the four documented coordinate traps are unreachable from user code
    /// (AD-5). Each one reads as correct code and is wrong:
    ///
    ///   1. Overlay canvases must pass NULL as the camera to ScreenPointToLocalPointInRectangle.
    ///      Passing the real camera returns wrong positions — a documented Unity bug.
    ///   2. Screen-Space-Camera must pass canvas.worldCamera.
    ///   3. World-Space canvases use neither path.
    ///   4. Camera.WorldToScreenPoint projects along an infinite line, so an object BEHIND the camera
    ///      still returns plausible on-screen coordinates. Every result is gated on z > 0.
    ///
    /// Render mode is read live from the Canvas every call, never cached — a canvas can be switched
    /// at runtime and a cached mode would silently apply the wrong rule (AD-5c).
    /// </summary>
    public sealed class A2BCanvasAdapter : IA2BSpaceAdapter
    {
        private readonly RectTransform _root;
        private readonly Canvas _canvas;
        private Camera _worldCamera;

        /// <summary>
        /// Camera used to project world positions to screen. Defaults to Camera.main.
        /// Distinct from the canvas's own camera: the coin starts at a world chest seen by the game
        /// camera and lands on a canvas that may be Overlay and have no camera at all.
        /// </summary>
        public Camera SourceCamera;

        public A2BCanvasAdapter(RectTransform root, Camera sourceCamera = null)
        {
            _root = root;
            _canvas = root != null ? root.GetComponentInParent<Canvas>() : null;
            SourceCamera = sourceCamera;
        }

        public A2BSpaceKind Space => A2BSpaceKind.Canvas;
        public Transform Root => _root;
        public Canvas Canvas => _canvas;

        /// <summary>The last ToWorkingSpace call resolved an endpoint that was behind the camera.</summary>
        public bool LastConversionWasBehindCamera { get; private set; }

        public Vector3 ToWorkingSpace(in A2BEndpointSample sample)
        {
            LastConversionWasBehindCamera = false;

            if (_root == null || _canvas == null)
                return sample.Position;

            // A UI endpoint reports a screen point, which is ALREADY projected. Projecting it again
            // is the bug this branch exists to prevent: a RectTransform on an overlay canvas has
            // position ~(960,540,0), and running that through WorldToScreenPoint projects a point
            // 960 world-units to the right — items land nowhere near the wallet.
            if (sample.Space == A2BEndpointSpace.Screen)
                return ScreenToWorking(sample.Position);

            // World-space canvas: no screen round-trip at all (trap 3).
            if (_canvas.renderMode == RenderMode.WorldSpace)
                return _root.InverseTransformPoint(sample.Position);

            Camera source = SourceCamera != null ? SourceCamera : Camera.main;
            if (source == null)
            {
                // No camera to project through; treat the position as already canvas-local rather
                // than throwing (AD-8).
                return _root.InverseTransformPoint(sample.Position);
            }

            Vector3 screen = source.WorldToScreenPoint(sample.Position);

            // Trap 4: z is world-unit distance along the camera's forward axis. Negative means the
            // point is behind the camera, and the x/y beside it are a mirage.
            if (screen.z <= 0f)
            {
                LastConversionWasBehindCamera = true;
                // Mirror the point back in front so it clamps to a plausible off-screen direction
                // instead of snapping to the opposite side of the screen.
                screen.x = Screen.width - screen.x;
                screen.y = Screen.height - screen.y;
                screen.z = 0f;
            }

            return ScreenToWorking(screen);
        }

        /// <summary>
        /// Screen pixels to this canvas's local space. Traps 1 and 2 live here: the camera argument
        /// depends on the LIVE render mode, and getting it backwards is a documented Unity bug.
        /// </summary>
        private Vector3 ScreenToWorking(Vector3 screen)
        {
            if (_canvas.renderMode == RenderMode.WorldSpace)
            {
                Camera cam = _canvas.worldCamera != null ? _canvas.worldCamera : Camera.main;
                if (cam != null && RectTransformUtility.ScreenPointToWorldPointInRectangle(
                        _root, screen, cam, out Vector3 world))
                    return _root.InverseTransformPoint(world);
                return Vector3.zero;
            }

            Camera uiCamera = _canvas.renderMode == RenderMode.ScreenSpaceOverlay
                ? null                     // trap 1: MUST be null for Overlay
                : _canvas.worldCamera;     // trap 2: MUST be the canvas camera otherwise

            if (RectTransformUtility.ScreenPointToLocalPointInRectangle(
                    _root, screen, uiCamera, out Vector2 local))
                return new Vector3(local.x, local.y, 0f);

            return Vector3.zero;
        }

        public Vector3 ScaleScatter(in Vector3 unitOffset, float radius)
            => new Vector3(unitOffset.x * radius, unitOffset.y * radius, 0f);

        public void ApplyToTransform(Transform target, in A2BVisualState state)
        {
            Vector3 p = state.Position;
            p.z = 0f;
            target.localPosition = p;
            target.localRotation = state.Rotation;
            target.localScale = state.Scale;
        }

    }
}
