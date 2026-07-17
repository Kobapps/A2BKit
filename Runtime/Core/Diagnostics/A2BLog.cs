using UnityEngine;
using Object = UnityEngine.Object;

namespace A2BKit.Core
{
    /// <summary>
    /// Logging with a context object attached, so every message names the offending asset or
    /// GameObject and clicking it selects the culprit (FR-23).
    ///
    /// Never call Debug.Log directly, and never interpolate a string in the tick path (AD-3, AD-20) —
    /// the overloads here take pre-built parts so the common case concatenates nothing.
    /// </summary>
    public static class A2BLog
    {
        /// <summary>Set false to silence the package entirely (e.g. in a shipping build).</summary>
        public static bool Enabled = true;

        private const string Prefix = "[A2BKit] ";

        public static void Error(Object context, string message)
        {
            if (!Enabled) return;
            Debug.LogError(Prefix + message, context);
        }

        public static void Warn(Object context, string message)
        {
            if (!Enabled) return;
            Debug.LogWarning(Prefix + message, context);
        }

        public static void Info(Object context, string message)
        {
            if (!Enabled) return;
            Debug.Log(Prefix + message, context);
        }

        /// <summary>
        /// Logs an exception thrown by user code (a listener, a custom path) without letting it
        /// escape into the caller's gameplay stack (AD-8).
        /// </summary>
        public static void Exception(Object context, System.Exception exception)
        {
            if (!Enabled) return;
            Debug.LogException(exception, context);
        }
    }
}
