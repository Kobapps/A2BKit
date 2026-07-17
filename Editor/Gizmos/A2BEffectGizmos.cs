using System.Collections.Generic;
using A2BKit.Core;
using A2BKit.Unity;
using UnityEditor;
using UnityEngine;

namespace A2BKit.Editor
{
    /// <summary>
    /// Draws the effect's real trajectory in the scene view (FR-20).
    ///
    /// The whole value of this gizmo rests on one property: it must show what will actually happen.
    /// A gizmo that re-derives the curve — the tempting shortcut, since "it's just a bezier" — is a
    /// second implementation of AD-13, and the two drift the first time anyone touches either. The
    /// arc a designer positions against would then be a lie, which is worse than no gizmo at all.
    ///
    /// So this calls <see cref="IA2BPath.Evaluate"/> — the identical method, on the identical
    /// instance, that <c>A2BScheduler.TickSlot</c> calls at runtime. Divergence is not "tested for",
    /// it is unreachable: there is only one implementation to diverge from. That extends to custom
    /// user paths for free (FR-10) — they get a correct gizmo without writing one.
    /// </summary>
    public static class A2BEffectGizmos
    {
        /// <summary>
        /// Samples per curve. 48 is where a hard arc stops looking faceted at typical scene-view
        /// zoom; past that the polyline costs repaint time and buys sub-pixel differences.
        /// </summary>
        internal const int SampleCount = 48;

        /// <summary>
        /// A fixed seed so the drawn curve holds still. Paths take per-item jitter from ctx.Seed
        /// (AD-10), so sampling with a fresh seed each repaint would make the gizmo shimmer while
        /// nothing changed — reading as instability in the effect rather than in the preview.
        /// </summary>
        internal const uint GizmoSeed = 0x2B2B2B2Bu;

        // Reused across repaints. Editor code may allocate freely (AD-3 governs the runtime tick),
        // but this runs on every scene-view repaint of a selected player, so a fresh 48-element array
        // per repaint is pure garbage for the editor GC to chase for no benefit.
        private static readonly Vector3[] Samples = new Vector3[SampleCount];
        private static readonly List<A2BVisualState> PreviewItems = new List<A2BVisualState>(64);

        private static readonly Color PathColor = new Color(0.35f, 0.85f, 1f, 0.9f);
        private static readonly Color OriginColor = new Color(0.4f, 1f, 0.5f, 1f);
        private static readonly Color DestinationColor = new Color(1f, 0.45f, 0.75f, 1f);
        private static readonly Color WarningColor = new Color(1f, 0.7f, 0.2f, 1f);

        /// <summary>
        /// Fills and returns the shared sample buffer by walking t across [0,1] through the path's
        /// own Evaluate. The returned array is the shared buffer — read it before the next call.
        /// </summary>
        /// <remarks>
        /// t is sampled RAW, not through the easing. Easing reparameterizes when an item is at a
        /// given point (AD-13), never where that point is; feeding eased t here would redistribute
        /// the samples along the identical curve and draw the same shape with uneven spacing.
        /// </remarks>
        internal static Vector3[] SamplePath(IA2BPath path, in A2BPathContext ctx)
        {
            if (path == null)
            {
                for (int i = 0; i < SampleCount; i++) Samples[i] = ctx.Origin;
                return Samples;
            }

            for (int i = 0; i < SampleCount; i++)
            {
                float t = i / (float)(SampleCount - 1);

                // A user path is arbitrary code running on the editor's repaint thread; a throw here
                // would spam the console and can break scene-view rendering (AD-8).
                try
                {
                    Samples[i] = path.Evaluate(in ctx, t);
                }
                catch
                {
                    Samples[i] = ctx.Origin;
                }
            }

            return Samples;
        }

        /// <summary>
        /// Scene-view gizmo for a selected player. Redrawn from live field values every repaint, so
        /// dragging ArcHeight moves the curve as the slider moves — there is nothing cached to
        /// invalidate.
        /// </summary>
        [DrawGizmo(GizmoType.Selected | GizmoType.Active)]
        private static void DrawEffectPlayerGizmo(A2BEffectPlayer player, GizmoType gizmoType)
        {
            if (player == null) return;

            A2BEffectAsset asset = player.Effect;
            Transform originTransform = player.Origin != null ? player.Origin : player.transform;
            Vector3 origin = originTransform.position;

            // Nothing to draw a path to. Still mark the origin — that alone tells the designer the
            // component is live and where it would fire from.
            if (player.Destination == null)
            {
                DrawEndpoint(origin, OriginColor, "Origin");
                Handles.color = WarningColor;
                Handles.Label(origin + Vector3.up * HandleUtility.GetHandleSize(origin) * 0.35f,
                    "A2B: no destination");
                return;
            }

            Vector3 destination = player.Destination.position;

            DrawEndpoint(origin, OriginColor, "Origin");
            DrawEndpoint(destination, DestinationColor, "Destination");

            IA2BPath path = asset != null && asset.Definition != null ? asset.Definition.Path : null;
            if (path == null)
            {
                // A dashed straight line is honest about being a placeholder: it says "these are the
                // endpoints" without implying a trajectory that no path has defined.
                Handles.color = WarningColor;
                Handles.DrawDottedLine(origin, destination, 4f);
                return;
            }

            var ctx = new A2BPathContext(origin, destination, 0, 1, GizmoSeed);
            Vector3[] points = SamplePath(path, in ctx);

            Handles.color = PathColor;
            Handles.DrawAAPolyLine(3f, SampleCount, points);

            DrawPreviewItems(player);
        }

        /// <summary>
        /// Overlays the live preview items (FR-21). They are drawn from the same simulation the
        /// runtime uses, so they sit on the polyline by construction rather than by agreement.
        /// </summary>
        private static void DrawPreviewItems(A2BEffectPlayer player)
        {
            if (!A2BEffectPreview.IsPlaying) return;
            if (A2BEffectPreview.Context != null && !ReferenceEquals(A2BEffectPreview.Context, player)) return;

            PreviewItems.Clear();
            A2BEffectPreview.CopyActiveItems(PreviewItems);

            for (int i = 0; i < PreviewItems.Count; i++)
            {
                A2BVisualState state = PreviewItems[i];
                Handles.color = state.Color;
                float size = HandleUtility.GetHandleSize(state.Position) * 0.05f * Mathf.Max(0.05f, state.Scale.x);
                Handles.SphereHandleCap(0, state.Position, Quaternion.identity, size, EventType.Repaint);
            }
        }

        /// <summary>
        /// Origin and destination must be told apart at a glance — they are the two things a
        /// designer is positioning, and a symmetric arc gives no clue which end is which. Colour
        /// plus a label, because colour alone fails for the ~8% of male users with a red-green
        /// deficiency and this pair is green/pink.
        /// </summary>
        private static void DrawEndpoint(Vector3 position, Color color, string label)
        {
            float size = HandleUtility.GetHandleSize(position);
            Handles.color = color;
            Handles.SphereHandleCap(0, position, Quaternion.identity, size * 0.12f, EventType.Repaint);
            Handles.Label(position + Vector3.up * size * 0.18f, label);
        }
    }
}
