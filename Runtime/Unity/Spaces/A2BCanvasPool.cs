using System.Collections.Generic;
using UnityEngine;

namespace A2BKit.Unity
{
    /// <summary>
    /// Supplies the dedicated canvas A2BKit parents its items to (AD-14).
    ///
    /// Why this exists at all: any drawable change re-runs batch building for EVERY element on that
    /// canvas. Put 200 moving coins on the game's HUD canvas and the rebuild — not the motion — is
    /// the frame spike. This is the #2 recurring failure in the survey behind the package.
    ///
    /// The trade is real and not free: batches do not merge across canvases, so rebuild isolation
    /// costs a draw call. That is the right default (rebuild storms are the failure teams actually
    /// hit) but a team that has profiled the opposite case overrides it via A2BEffectPlayer.CanvasRoot.
    ///
    /// One dedicated canvas is created per host canvas, so items inherit the right render mode,
    /// camera and scaler without the caller configuring anything.
    /// </summary>
    public static class A2BCanvasPool
    {
        private static readonly Dictionary<Canvas, RectTransform> _roots = new Dictionary<Canvas, RectTransform>();

        /// <summary>
        /// Returns a dedicated overlay child canvas matching the host's render mode. Creates one on
        /// first use. Pass null to get a standalone overlay canvas.
        /// </summary>
        public static RectTransform GetDedicatedRoot(Canvas hostCanvas)
        {
            if (hostCanvas != null && _roots.TryGetValue(hostCanvas, out RectTransform existing) && existing != null)
                return existing;

            var go = new GameObject("[A2BKit Canvas]", typeof(RectTransform), typeof(Canvas));
            var rect = (RectTransform)go.transform;
            var canvas = go.GetComponent<Canvas>();

            if (hostCanvas != null)
            {
                // Sibling of the host, not a child: a nested canvas would still be rebuilt with its
                // parent in some paths, which would defeat the entire point.
                rect.SetParent(hostCanvas.transform.parent, false);
                canvas.renderMode = hostCanvas.renderMode;
                canvas.worldCamera = hostCanvas.worldCamera;
                canvas.planeDistance = hostCanvas.planeDistance;

                // Draw above the host by default so rewards read as landing "on top of" the HUD.
                canvas.sortingLayerID = hostCanvas.sortingLayerID;
                canvas.sortingOrder = hostCanvas.sortingOrder + 1;

                CopyScaler(hostCanvas, canvas);
            }
            else
            {
                canvas.renderMode = RenderMode.ScreenSpaceOverlay;
                canvas.sortingOrder = 100;
            }

            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;

            if (hostCanvas != null) _roots[hostCanvas] = rect;
            return rect;
        }

        private static void CopyScaler(Canvas host, Canvas target)
        {
            var hostScaler = host.GetComponent<UnityEngine.UI.CanvasScaler>();
            if (hostScaler == null) return;

            var scaler = target.gameObject.AddComponent<UnityEngine.UI.CanvasScaler>();
            scaler.uiScaleMode = hostScaler.uiScaleMode;
            scaler.referenceResolution = hostScaler.referenceResolution;
            scaler.screenMatchMode = hostScaler.screenMatchMode;
            scaler.matchWidthOrHeight = hostScaler.matchWidthOrHeight;
            scaler.referencePixelsPerUnit = hostScaler.referencePixelsPerUnit;
            scaler.scaleFactor = hostScaler.scaleFactor;
        }

        /// <summary>Drops cached roots. Called on domain reload so a stale canvas is not reused (NFR-5).</summary>
        public static void Clear() => _roots.Clear();

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics() => _roots.Clear();
    }
}
