using System;
using UnityEngine;

namespace A2BKit.Core
{
    public enum A2BEaseKind
    {
        Linear,
        InQuad, OutQuad, InOutQuad,
        InCubic, OutCubic, InOutCubic,
        InQuart, OutQuart, InOutQuart,
        InSine, OutSine, InOutSine,
        InExpo, OutExpo, InOutExpo,
        InBack, OutBack, InOutBack,
        OutElastic,
        OutBounce
    }

    /// <summary>
    /// Easing functions as pure statics. No allocation, no state — safe to call from the tick path.
    /// </summary>
    public static class A2BEase
    {
        private const float Back = 1.70158f;

        public static float Evaluate(A2BEaseKind kind, float t)
        {
            t = t < 0f ? 0f : (t > 1f ? 1f : t);
            switch (kind)
            {
                case A2BEaseKind.Linear: return t;

                case A2BEaseKind.InQuad: return t * t;
                case A2BEaseKind.OutQuad: return 1f - (1f - t) * (1f - t);
                case A2BEaseKind.InOutQuad: return t < 0.5f ? 2f * t * t : 1f - 2f * (1f - t) * (1f - t);

                case A2BEaseKind.InCubic: return t * t * t;
                case A2BEaseKind.OutCubic: { float u = 1f - t; return 1f - u * u * u; }
                case A2BEaseKind.InOutCubic:
                    return t < 0.5f ? 4f * t * t * t : 1f - Mathf.Pow(-2f * t + 2f, 3f) * 0.5f;

                case A2BEaseKind.InQuart: return t * t * t * t;
                case A2BEaseKind.OutQuart: { float u = 1f - t; return 1f - u * u * u * u; }
                case A2BEaseKind.InOutQuart:
                    return t < 0.5f ? 8f * t * t * t * t : 1f - Mathf.Pow(-2f * t + 2f, 4f) * 0.5f;

                case A2BEaseKind.InSine: return 1f - Mathf.Cos(t * Mathf.PI * 0.5f);
                case A2BEaseKind.OutSine: return Mathf.Sin(t * Mathf.PI * 0.5f);
                case A2BEaseKind.InOutSine: return -(Mathf.Cos(Mathf.PI * t) - 1f) * 0.5f;

                case A2BEaseKind.InExpo: return t <= 0f ? 0f : Mathf.Pow(2f, 10f * t - 10f);
                case A2BEaseKind.OutExpo: return t >= 1f ? 1f : 1f - Mathf.Pow(2f, -10f * t);
                case A2BEaseKind.InOutExpo:
                    if (t <= 0f) return 0f;
                    if (t >= 1f) return 1f;
                    return t < 0.5f
                        ? Mathf.Pow(2f, 20f * t - 10f) * 0.5f
                        : (2f - Mathf.Pow(2f, -20f * t + 10f)) * 0.5f;

                case A2BEaseKind.InBack: return (Back + 1f) * t * t * t - Back * t * t;
                case A2BEaseKind.OutBack:
                    { float u = t - 1f; return 1f + (Back + 1f) * u * u * u + Back * u * u; }
                case A2BEaseKind.InOutBack:
                    {
                        const float c = Back * 1.525f;
                        return t < 0.5f
                            ? (Mathf.Pow(2f * t, 2f) * ((c + 1f) * 2f * t - c)) * 0.5f
                            : (Mathf.Pow(2f * t - 2f, 2f) * ((c + 1f) * (t * 2f - 2f) + c) + 2f) * 0.5f;
                    }

                case A2BEaseKind.OutElastic:
                    {
                        if (t <= 0f) return 0f;
                        if (t >= 1f) return 1f;
                        const float p = (2f * Mathf.PI) / 3f;
                        return Mathf.Pow(2f, -10f * t) * Mathf.Sin((t * 10f - 0.75f) * p) + 1f;
                    }

                case A2BEaseKind.OutBounce: return OutBounce(t);

                default: return t;
            }
        }

        private static float OutBounce(float t)
        {
            const float n = 7.5625f;
            const float d = 2.75f;
            if (t < 1f / d) return n * t * t;
            if (t < 2f / d) { t -= 1.5f / d; return n * t * t + 0.75f; }
            if (t < 2.5f / d) { t -= 2.25f / d; return n * t * t + 0.9375f; }
            t -= 2.625f / d;
            return n * t * t + 0.984375f;
        }
    }

    /// <summary>Easing from the built-in library.</summary>
    [Serializable]
    public sealed class A2BStandardEasing : IA2BEasing
    {
        public A2BEaseKind Kind = A2BEaseKind.OutCubic;

        public A2BStandardEasing() { }
        public A2BStandardEasing(A2BEaseKind kind) { Kind = kind; }

        public float Evaluate(float t) => A2BEase.Evaluate(Kind, t);
    }

    /// <summary>
    /// Easing from a designer-drawn AnimationCurve. AnimationCurve is value-math surface, not scene
    /// graph, so it is permitted in Core (AD-1). Evaluate does not allocate.
    /// </summary>
    [Serializable]
    public sealed class A2BCurveEasing : IA2BEasing
    {
        public AnimationCurve Curve = AnimationCurve.EaseInOut(0f, 0f, 1f, 1f);

        public float Evaluate(float t) => Curve == null ? t : Curve.Evaluate(t);
    }
}
