using System;
using System.Collections.Generic;
using System.Linq;

namespace kinatraa.PlayerPrefEditor
{
    public enum DiffKind { Added, Changed, Unchanged, Removed }

    public sealed class DiffRow
    {
        public readonly string Key;
        public readonly DiffKind Kind;
        /// <summary>Current value; null when the key does not exist yet.</summary>
        public readonly PrefEntry Before;
        /// <summary>Incoming value; null when the key will be removed.</summary>
        public readonly PrefEntry After;

        public DiffRow(string key, DiffKind kind, PrefEntry before, PrefEntry after)
        {
            Key = key;
            Kind = kind;
            Before = before;
            After = after;
        }
    }

    /// <summary>Compares an incoming document with the current PlayerPrefs, key by key.</summary>
    public static class PrefDiff
    {
        /// <param name="replace">Also remove current keys missing from <paramref name="incoming"/> (never Unity's internal keys).</param>
        public static List<DiffRow> Compute(IEnumerable<PrefEntry> incoming, IEnumerable<PrefEntry> current, bool replace)
        {
            var now = current.ToDictionary(e => e.Key, StringComparer.Ordinal);
            var rows = new List<DiffRow>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var e in incoming)
            {
                seen.Add(e.Key);
                now.TryGetValue(e.Key, out var before);
                var kind = before == null ? DiffKind.Added : before.SameValue(e) ? DiffKind.Unchanged : DiffKind.Changed;
                rows.Add(new DiffRow(e.Key, kind, before, e));
            }
            if (replace)
            {
                foreach (var e in now.Values)
                    if (!seen.Contains(e.Key) && !PlayerPrefStore.IsInternalKey(e.Key))
                        rows.Add(new DiffRow(e.Key, DiffKind.Removed, e, null));
            }
            rows.Sort((a, b) => PlayerPrefStore.CompareKeys(a.Key, b.Key));
            return rows;
        }

        public static int Count(IEnumerable<DiffRow> rows, DiffKind kind) => rows.Count(r => r.Kind == kind);

        /// <summary>True when both are missing, hold the same type and value, or are both unreadable.</summary>
        internal static bool Same(PrefEntry a, PrefEntry b) =>
            a == null ? b == null : b != null && (a.SameValue(b) || a.Type == PrefType.Unknown && b.Type == PrefType.Unknown);
    }
}
