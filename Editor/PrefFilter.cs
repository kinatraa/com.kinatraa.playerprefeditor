using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace kinatraa.PlayerPrefEditor
{
    public enum SortMode { NameAscending, NameDescending, Type }

    /// <summary>Search, type filter and sort for the key list. Pinned keys always come first.</summary>
    public sealed class PrefFilter
    {
        public string Query = "";
        /// <summary>null shows every type.</summary>
        public PrefType? Type;
        public bool SearchValues = true;
        public bool UseRegex;
        public bool ShowInternal;
        public SortMode Sort = SortMode.NameAscending;

        /// <summary>The visible entries. An invalid regex sets <paramref name="error"/> and the query is ignored.</summary>
        public List<PrefEntry> Apply(IEnumerable<PrefEntry> all, ICollection<string> pinned, out string error)
        {
            var match = CreateMatcher(out error);
            var visible = all.Where(e =>
                    (ShowInternal || !PlayerPrefStore.IsInternalKey(e.Key)) &&
                    (Type == null || e.Type == Type) &&
                    (match == null || match(e.Key) || SearchValues && match(ValueText(e))))
                .ToList();

            visible.Sort((a, b) =>
            {
                int pin = pinned.Contains(b.Key).CompareTo(pinned.Contains(a.Key));
                if (pin != 0) return pin;
                switch (Sort)
                {
                    case SortMode.NameDescending: return PlayerPrefStore.CompareKeys(b.Key, a.Key);
                    case SortMode.Type:
                        int t = a.Type.CompareTo(b.Type);
                        return t != 0 ? t : PlayerPrefStore.CompareKeys(a.Key, b.Key);
                    default: return PlayerPrefStore.CompareKeys(a.Key, b.Key);
                }
            });
            return visible;
        }

        Func<string, bool> CreateMatcher(out string error)
        {
            error = null;
            var query = Query ?? "";
            if (!UseRegex)
            {
                query = query.Trim();
                if (query.Length == 0) return null;
                return s => s.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;
            }
            if (query.Length == 0) return null;
            try
            {
                var regex = new Regex(query, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50));
                return s =>
                {
                    try { return regex.IsMatch(s); }
                    catch (RegexMatchTimeoutException) { return false; }
                };
            }
            catch (ArgumentException e)
            {
                error = "Invalid regex: " + e.Message;
                return null;
            }
        }

        /// <summary>The text value search looks at: the raw string, or the number as JSON.</summary>
        public static string ValueText(PrefEntry e) =>
            e.Type == PrefType.String ? (string)e.Value
            : e.Type == PrefType.Unknown ? ""
            : PrefJson.FormatValue(e.Type, e.Value);

        /// <summary>One-line JSON preview for the list, truncated to <paramref name="max"/> characters.</summary>
        public static string Preview(PrefEntry e, int max = 120)
        {
            if (e.Type == PrefType.Unknown) return "?";
            if (e.Type != PrefType.String) return PrefJson.FormatValue(e.Type, e.Value);
            // Cut before escaping so a multi-megabyte string costs nothing.
            var raw = (string)e.Value;
            if (raw.Length <= max) return Newtonsoft.Json.JsonConvert.ToString(raw);
            return Newtonsoft.Json.JsonConvert.ToString(raw.Substring(0, max)) + "…";
        }
    }
}
