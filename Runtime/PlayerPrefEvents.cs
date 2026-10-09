using System;
using UnityEngine;

namespace kinatraa.PlayerPrefEditor
{
    /// <summary>
    /// Lets game code react when PlayerPrefs are changed from the Player Pref Editor (save, rename, delete, import, undo),
    /// for example to re-read a value it cached in Start. Only the Editor raises it; in builds it never fires.
    /// </summary>
    public static class PlayerPrefEvents
    {
        /// <summary>Raised once per changed key, after the change is saved. The key may have been deleted: read it with a default.</summary>
        public static event Action<string> Changed;

        /// <summary>Called by the editor package. A throwing handler is logged and never undoes or blocks the change.</summary>
        public static void RaiseChanged(string key)
        {
            var handlers = Changed;
            if (handlers == null) return;
            foreach (Action<string> handler in handlers.GetInvocationList())
            {
                try { handler(key); }
                catch (Exception e) { Debug.LogException(e); }
            }
        }

        // With domain reload disabled, statics survive entering Play Mode; drop handlers left by the previous session.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetOnPlay() => Changed = null;
    }
}
