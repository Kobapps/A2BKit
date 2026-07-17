using System;
using System.Collections.Generic;
using System.Text;
using A2BKit.Core;
using TMPro;
using UnityEngine;

namespace A2BKit.Unity
{
    /// <summary>
    /// A pooled <see cref="TextMeshProUGUI"/> per item — the "+250" payload (FR-7).
    ///
    /// AD-20 in full: content is built into one reused <see cref="StringBuilder"/> and handed to TMP
    /// through <c>SetText(StringBuilder)</c>, the only overload that does not take ownership of a
    /// managed string. It is built at Acquire and never rebuilt per frame — the text of an item does
    /// not change while it flies, so a dirty flag is not needed; the spawn *is* the dirty edge.
    ///
    /// Per frame this type writes one colour, and only when it changed. A TMP colour write forces a
    /// mesh rebuild, which at 200 items is the frame, not a fraction of it.
    /// </summary>
    [Serializable]
    public sealed class A2BTextPayloadRenderer : A2BPooledPayloadRenderer
    {
        /// <summary>Font asset. Null falls back to TMP's default, which is a warning, not a crash.</summary>
        public TMP_FontAsset Font;

        /// <summary>Optional font material override.</summary>
        public Material FontMaterial;

        public float FontSize = 36f;

        /// <summary>Item size in canvas units, written once at create time.</summary>
        public Vector2 Size = new Vector2(160f, 48f);

        public TextAlignmentOptions Alignment = TextAlignmentOptions.Center;

        /// <summary>Prepended to formatted values. "+" is the reward-popup default.</summary>
        public string Prefix = "+";

        /// <summary>Decimal places used when formatting <see cref="A2BItemSpawnInfo.Value"/>. Clamped to [0,6].</summary>
        public int Decimals;

        /// <summary>Component refs resolved once at pool-create time; UpdateItem never calls GetComponent (AD-3).</summary>
        private sealed class Binding
        {
            public TextMeshProUGUI Text;
            public Color LastColor;
        }

        private Dictionary<Transform, Binding> _bindings;
        private StringBuilder _builder;

        public override string PayloadKey => "Text";

        protected override void OnInitialized()
        {
            _bindings = new Dictionary<Transform, Binding>(Mathf.Max(8, DefaultCapacity), A2BTransformComparer.Instance);

            // One builder for the whole renderer, for the whole session. Sized past any plausible
            // "+1,234,567.89" so the first few spawns do not pay for chunk growth.
            _builder = new StringBuilder(32);

            if (Font == null)
                A2BLog.Warn(null, "A2BTextPayloadRenderer has no Font assigned; items will fall back to TMP's default font asset.");
        }

        protected override Transform CreateInstance()
        {
            // RectTransform must be named on the constructor; TMP pulls in its own CanvasRenderer.
            var instance = new GameObject("A2B Text Item", typeof(RectTransform), typeof(TextMeshProUGUI));
            var rect = (RectTransform)instance.transform;

            // Geometry, not placement (AD-15).
            rect.anchorMin = new Vector2(0.5f, 0.5f);
            rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.sizeDelta = Size;

            var text = instance.GetComponent<TextMeshProUGUI>();
            if (Font != null) text.font = Font;
            if (FontMaterial != null) text.fontSharedMaterial = FontMaterial;
            text.fontSize = FontSize;
            text.alignment = Alignment;
            text.raycastTarget = false;
            text.overflowMode = TextOverflowModes.Overflow;

            _bindings[rect] = new Binding { Text = text, LastColor = text.color };
            return rect;
        }

        protected override void OnAcquired(Transform item, in A2BItemSpawnInfo info)
        {
            if (_bindings == null || !_bindings.TryGetValue(item, out Binding binding)) return;

            // A faded-out item must not come back faded out.
            binding.Text.color = Color.white;
            binding.LastColor = Color.white;

            if (info.Text != null)
            {
                // The caller already owns this string; SetText copies it into TMP's internal buffer
                // and we build nothing (AD-20).
                binding.Text.SetText(info.Text);
                return;
            }

            _builder.Clear();
            if (!string.IsNullOrEmpty(Prefix)) _builder.Append(Prefix);
            A2BNumberFormat.AppendFloat(_builder, info.Value, Decimals);
            binding.Text.SetText(_builder);
        }

        public override void UpdateItem(Transform item, in A2BVisualState state)
        {
            if (_bindings == null || !_bindings.TryGetValue(item, out Binding binding)) return;
            if (binding.Text == null) return;
            if (binding.LastColor == state.Color) return;

            binding.LastColor = state.Color;
            binding.Text.color = state.Color;
        }

        protected override void OnInstanceDestroyed(Transform item)
        {
            _bindings?.Remove(item);
        }

        protected override void OnDisposed()
        {
            _bindings?.Clear();
            _builder = null;
        }
    }

    /// <summary>
    /// Fixed-point number formatting that appends digits one char at a time.
    ///
    /// AD-20 names <c>StringBuilder.Append(int/float)</c> as the non-allocating path, and on modern
    /// .NET it is. On Unity's Mono corlib it is not: Append(float) routes through
    /// <c>value.ToString(CultureInfo)</c> and allocates the very string the rule exists to prevent.
    /// Emitting digits by hand is what makes AD-20's intent true on the runtime we actually ship on,
    /// and it is culture-invariant for free — a German locale must not turn "+2.5" into "+2,5" and
    /// desync a designer's expectations from the build.
    ///
    /// The scratch buffer is static and shared. That is safe precisely because AD-6 puts every tick
    /// on one thread; it would be a data race the moment that stopped being true.
    /// </summary>
    internal static class A2BNumberFormat
    {
        private static readonly float[] Pow10 = { 1f, 10f, 100f, 1000f, 10000f, 100000f, 1000000f };

        /// <summary>Long enough for ulong.MaxValue's 20 digits.</summary>
        private static readonly char[] Scratch = new char[20];

        internal static void AppendFloat(StringBuilder builder, float value, int decimals)
        {
            if (builder == null) return;

            // NaN and Infinity have no digit expansion, and a cosmetic label is not the place to
            // surface that (AD-8).
            if (float.IsNaN(value) || float.IsInfinity(value))
            {
                builder.Append('0');
                return;
            }

            if (decimals < 0) decimals = 0;
            if (decimals > 6) decimals = 6;

            bool negative = value < 0f;
            if (negative) value = -value;

            float scale = Pow10[decimals];

            // Round half away from zero in double, then never touch floating point again.
            double scaled = Math.Floor((double)value * scale + 0.5d);
            if (scaled > long.MaxValue) scaled = long.MaxValue;

            long units = (long)scaled;
            long divisor = (long)scale;
            long whole = units / divisor;
            long fraction = units - whole * divisor;

            // "-0" is not a number anyone wants to read: only sign a value that survived rounding.
            if (negative && (whole != 0L || fraction != 0L)) builder.Append('-');

            AppendDigits(builder, (ulong)whole, 1);

            if (decimals > 0)
            {
                builder.Append('.');
                AppendDigits(builder, (ulong)fraction, decimals);
            }
        }

        /// <summary>Appends a whole number, no sign, no padding.</summary>
        internal static void AppendLong(StringBuilder builder, long value)
        {
            if (builder == null) return;

            if (value < 0L)
            {
                builder.Append('-');
                // Negating long.MinValue overflows; widen to ulong before flipping the sign.
                AppendDigits(builder, (ulong)(-(value + 1L)) + 1UL, 1);
                return;
            }

            AppendDigits(builder, (ulong)value, 1);
        }

        /// <summary>Writes digits backwards into the scratch buffer, then forwards into the builder.</summary>
        private static void AppendDigits(StringBuilder builder, ulong value, int minWidth)
        {
            int index = Scratch.Length;
            do
            {
                Scratch[--index] = (char)('0' + (int)(value % 10UL));
                value /= 10UL;
            }
            while (value > 0UL);

            for (int pad = Scratch.Length - index; pad < minWidth; pad++)
                builder.Append('0');

            for (int i = index; i < Scratch.Length; i++)
                builder.Append(Scratch[i]);
        }
    }
}
