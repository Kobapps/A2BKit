using System.Text;
using TMPro;
using UnityEngine;

namespace A2BKit.Demo
{
    /// <summary>
    /// A wallet counter, driven entirely from the scene: wire <see cref="A2BEffectPlayer"/>'s
    /// <c>OnFirstItemArrivedEvent</c> to <see cref="BeginRollUp"/> and <c>OnItemArrivedEvent</c> to
    /// <see cref="Increment"/> in the inspector.
    ///
    /// That wiring is the point of the whole package. Drive the counter from the burst *starting* and
    /// the number finishes before a coin has landed; drive it from completion and the reward reads as
    /// having already happened. Neither feels like a reward. `FirstItemArrived` is the moment the
    /// player sees the first coin hit the wallet, which is when the number should start moving.
    /// </summary>
    [AddComponentMenu("A2BKit/Demo/Counter")]
    public sealed class A2BDemoCounter : MonoBehaviour
    {
        [Tooltip("The label to write. Defaults to a TMP text on this GameObject.")]
        public TextMeshProUGUI Label;

        [Tooltip("Amount added per arrival.")]
        public int PerItem = 1;

        [Tooltip("Tint while coins are still landing.")]
        public Color RollUpColor = new Color(1f, 0.86f, 0.35f);

        public Color IdleColor = Color.white;

        /// <summary>Reused for every write: StringBuilder.Append(int) allocates on Unity's Mono.</summary>
        private readonly StringBuilder _text = new StringBuilder(16);
        private int _total;

        /// <summary>The running total, in case another component wants it.</summary>
        public int Total => _total;

        private void Reset() => Label = GetComponent<TextMeshProUGUI>();

        private void Awake()
        {
            if (Label == null) Label = GetComponent<TextMeshProUGUI>();
            Write();
        }

        /// <summary>Wire to OnFirstItemArrivedEvent — the first coin just landed.</summary>
        public void BeginRollUp()
        {
            if (Label != null) Label.color = RollUpColor;
        }

        /// <summary>Wire to OnCompletedEvent / OnCancelledEvent.</summary>
        public void EndRollUp()
        {
            if (Label != null) Label.color = IdleColor;
        }

        /// <summary>Wire to OnItemArrivedEvent — one coin landed.</summary>
        public void Increment()
        {
            _total += PerItem;
            Write();
        }

        public void ResetTotal()
        {
            _total = 0;
            Write();
        }

        private void Write()
        {
            if (Label == null) return;

            // StringBuilder + SetText, never interpolation. This runs once per coin, and on Unity's
            // Mono even Append(int) routes through ToString() and allocates — hence the digit writer.
            _text.Clear();
            A2BDemoNumber.AppendInt(_text, _total);
            Label.SetText(_text);
        }
    }

    /// <summary>
    /// Writes an integer's digits into a StringBuilder without allocating.
    ///
    /// Exists because `StringBuilder.Append(int)` allocates on Unity's Mono corlib — it formats via
    /// `value.ToString()`. In a per-arrival callback that is exactly the cost A2BKit advertises away,
    /// so the samples do not get to cheat.
    /// </summary>
    public static class A2BDemoNumber
    {
        [System.ThreadStatic] private static char[] _scratch;

        public static void AppendInt(StringBuilder builder, long value)
        {
            if (builder == null) return;

            _scratch ??= new char[24];

            if (value == 0) { builder.Append('0'); return; }
            if (value < 0) { builder.Append('-'); value = -value; }

            int i = 0;
            while (value > 0)
            {
                _scratch[i++] = (char)('0' + (int)(value % 10));
                value /= 10;
            }
            while (i > 0) builder.Append(_scratch[--i]);
        }
    }
}
