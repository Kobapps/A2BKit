using A2BKit.Core;
using UnityEngine;

namespace A2BKit.Unity
{
    /// <summary>
    /// Un-projects a screen point back into world space for the world adapters. Kept in one place so
    /// the depth choice is made once rather than guessed per adapter (AD-4).
    /// </summary>
    internal static class A2BScreenSpace
    {
        public static Vector3 ToWorld(Vector3 screenPoint, Camera camera)
        {
            Camera cam = camera != null ? camera : Camera.main;
            if (cam == null) return screenPoint;

            // Depth: project onto the plane the camera is already looking at rather than inventing a
            // distance. For an orthographic camera any positive depth works; for perspective this
            // keeps a UI-sourced point at a sensible scale instead of on the near plane.
            float depth = cam.orthographic ? Mathf.Abs(cam.transform.position.z) : 10f;
            return cam.ScreenToWorldPoint(new Vector3(screenPoint.x, screenPoint.y, depth));
        }
    }

    /// <summary>
    /// Full 3D world space. Working space is the Root's local space, so an effect can be moved or
    /// parented wholesale without touching the math.
    /// </summary>
    public sealed class A2BWorld3DAdapter : IA2BSpaceAdapter
    {
        private readonly Transform _root;

        /// <summary>Used only to un-project screen-space endpoints (a UI origin, a 3D target).</summary>
        public Camera Camera;

        public A2BWorld3DAdapter(Transform root, Camera camera = null) { _root = root; Camera = camera; }

        public A2BSpaceKind Space => A2BSpaceKind.World3D;
        public Transform Root => _root;

        public Vector3 ToWorkingSpace(in A2BEndpointSample sample)
        {
            // A UI endpoint arrives as a screen point; un-project it before entering world space,
            // otherwise a Canvas origin flying to a 3D target lands at (960, 540, 0) in metres.
            Vector3 world = sample.Space == A2BEndpointSpace.Screen
                ? A2BScreenSpace.ToWorld(sample.Position, Camera)
                : sample.Position;
            return _root != null ? _root.InverseTransformPoint(world) : world;
        }

        public Vector3 ScaleScatter(in Vector3 unitOffset, float radius) => unitOffset * radius;

        public void ApplyToTransform(Transform target, in A2BVisualState state)
        {
            target.localPosition = state.Position;
            target.localRotation = state.Rotation;
            target.localScale = state.Scale;
        }
    }

    /// <summary>
    /// 2D world space: the XY plane.
    ///
    /// Z is pinned to the Root's Z and depth is expressed through sorting order, never position
    /// (AD-19). Without that rule World2D is just World3D under a different name, and the whole
    /// three-space taxonomy buys nothing — so the Z discard here is the feature, not a limitation.
    /// </summary>
    public sealed class A2BWorld2DAdapter : IA2BSpaceAdapter
    {
        private readonly Transform _root;

        /// <summary>Used only to un-project screen-space endpoints (a UI origin, a 2D target).</summary>
        public Camera Camera;

        public A2BWorld2DAdapter(Transform root, Camera camera = null) { _root = root; Camera = camera; }

        public A2BSpaceKind Space => A2BSpaceKind.World2D;
        public Transform Root => _root;

        public Vector3 ToWorkingSpace(in A2BEndpointSample sample)
        {
            Vector3 world = sample.Space == A2BEndpointSpace.Screen
                ? A2BScreenSpace.ToWorld(sample.Position, Camera)
                : sample.Position;
            Vector3 local = _root != null ? _root.InverseTransformPoint(world) : world;
            local.z = 0f;   // AD-19: depth is sorting order, never position
            return local;
        }

        public Vector3 ScaleScatter(in Vector3 unitOffset, float radius)
            => new Vector3(unitOffset.x * radius, unitOffset.y * radius, 0f);

        public void ApplyToTransform(Transform target, in A2BVisualState state)
        {
            // Z discarded deliberately (AD-19) — a World2D effect whose items differ in Z is a defect.
            Vector3 p = state.Position;
            p.z = 0f;
            target.localPosition = p;
            target.localRotation = state.Rotation;
            target.localScale = state.Scale;
        }
    }
}
