using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using UnityEditor;
using UnityEngine;

namespace kinatraa.PlayerPrefEditor
{
    /// <summary>
    /// Reads and writes the Editor's PlayerPrefs. Keys come from the OS store (<see cref="NativePrefs"/>) plus a
    /// per-project list of every key written through this package, which covers values not yet flushed to disk.
    /// Use <see cref="PrefHistory"/> instead of the write methods here when the change should be undoable.
    /// </summary>
    public static class PlayerPrefStore
    {
        // EditorPrefs are shared by every project on the machine, so scope the lists to this project's PlayerPrefs.
        static string Scope => $"{PlayerSettings.companyName}.{PlayerSettings.productName}";
        static string TrackedListKey => "kinatraa.PlayerPrefEditor.Keys." + Scope;
        static string PinnedListKey => "kinatraa.PlayerPrefEditor.Pins." + Scope;

        static Dictionary<string, PrefType> _nativeTypes = new Dictionary<string, PrefType>();

        /// <summary>Why the OS store could not be read on the last <see cref="GetKeys"/>, or null.</summary>
        public static string LastNativeError { get; private set; }

        /// <summary>All existing keys, sorted alphabetically (case-insensitive).</summary>
        public static List<string> GetKeys()
        {
            var keys = new HashSet<string>(StringComparer.Ordinal);
            var native = NativePrefs.ReadKeys(out var error);
            LastNativeError = error;
            if (native != null)
            {
                _nativeTypes = native;
                keys.UnionWith(native.Keys);
            }

            var tracked = LoadList(TrackedListKey);
            keys.UnionWith(tracked);
            // The OS store can hold stale names and lag behind unsaved values; PlayerPrefs itself decides.
            keys.RemoveWhere(k => !PlayerPrefs.HasKey(k));
            if (tracked.RemoveAll(k => !keys.Contains(k)) > 0) SaveList(TrackedListKey, tracked);

            var sorted = keys.ToList();
            sorted.Sort(CompareKeys);
            return sorted;
        }

        public static List<PrefEntry> ReadAll() => GetKeys().Select(Read).ToList();

        public static int CompareKeys(string a, string b)
        {
            int c = string.Compare(a, b, StringComparison.OrdinalIgnoreCase);
            return c != 0 ? c : string.CompareOrdinal(a, b);
        }

        public static bool Exists(string key) => PlayerPrefs.HasKey(key);

        /// <summary>The current value, or null when the key does not exist.</summary>
        public static PrefEntry Read(string key)
        {
            if (!PlayerPrefs.HasKey(key)) return null;
            var type = DetectType(key);
            if (type == PrefType.Unknown && _nativeTypes.TryGetValue(key, out var hint)) type = hint;
            switch (type)
            {
                case PrefType.Int: return new PrefEntry(key, type, PlayerPrefs.GetInt(key));
                case PrefType.Float: return new PrefEntry(key, type, PlayerPrefs.GetFloat(key));
                case PrefType.String: return new PrefEntry(key, type, PlayerPrefs.GetString(key));
                default: return new PrefEntry(key, PrefType.Unknown, null);
            }
        }

        public static void Write(PrefEntry entry) => Commit(new[] { entry }, Array.Empty<string>());

        public static void Apply(IReadOnlyCollection<PrefEntry> entries) => Commit(entries, Array.Empty<string>());

        public static void Delete(string key) => Commit(Array.Empty<PrefEntry>(), new[] { key });

        /// <summary>
        /// Deletes <paramref name="deletes"/>, writes <paramref name="writes"/>, then saves once.
        /// Every write is validated first, so an invalid entry throws before anything changes.
        /// </summary>
        public static void Commit(IReadOnlyCollection<PrefEntry> writes, IReadOnlyCollection<string> deletes)
        {
            foreach (var e in writes)
            {
                if (string.IsNullOrEmpty(e.Key)) throw new ArgumentException("Keys must not be empty.");
                bool ok = e.Type == PrefType.Int && e.Value is int
                          || e.Type == PrefType.Float && e.Value is float
                          || e.Type == PrefType.String && e.Value is string;
                if (!ok) throw new ArgumentException($"\"{e.Key}\": the value does not match type {PrefJson.TypeName(e.Type)}.");
            }

            foreach (var key in deletes) PlayerPrefs.DeleteKey(key);
            foreach (var e in writes)
            {
                // Delete first so a type change never leaves the old value behind.
                PlayerPrefs.DeleteKey(e.Key);
                switch (e.Type)
                {
                    case PrefType.Int: PlayerPrefs.SetInt(e.Key, (int)e.Value); break;
                    case PrefType.Float: PlayerPrefs.SetFloat(e.Key, (float)e.Value); break;
                    default: PlayerPrefs.SetString(e.Key, (string)e.Value); break;
                }
            }
            PlayerPrefs.Save();

            var tracked = LoadList(TrackedListKey);
            var written = new HashSet<string>(writes.Select(e => e.Key));
            tracked.RemoveAll(k => deletes.Contains(k) && !written.Contains(k));
            tracked.AddRange(written.Where(k => !tracked.Contains(k)));
            SaveList(TrackedListKey, tracked);

            foreach (var key in deletes.Union(written)) PlayerPrefEvents.RaiseChanged(key);
        }

        /// <summary>Adds keys that already exist in PlayerPrefs to the tracked list, so they are listed on every platform.</summary>
        public static void Track(IEnumerable<string> keys)
        {
            var tracked = LoadList(TrackedListKey);
            var added = keys.Where(k => PlayerPrefs.HasKey(k) && !tracked.Contains(k)).Distinct().ToList();
            if (added.Count == 0) return;
            tracked.AddRange(added);
            SaveList(TrackedListKey, tracked);
        }

        /// <summary>
        /// Keys Unity, its packages or the Editor write for themselves (graphics quality, analytics session ids, test runner state).
        /// The window hides them unless asked, and Replace imports and Delete All never touch them.
        /// </summary>
        public static bool IsInternalKey(string key) =>
            key.StartsWith("unity.", StringComparison.Ordinal) ||
            key.StartsWith("unity_connect.", StringComparison.Ordinal) ||
            key.StartsWith("UnityGraphicsQuality", StringComparison.Ordinal) ||
            key.StartsWith("UnitySelectMonitor", StringComparison.Ordinal) ||
            key.StartsWith("Screenmanager ", StringComparison.Ordinal) ||
            key == "PT_Run" || key == "PT_Settings";

        /// <summary>The given entries (all non-internal keys when null) as a formatted JSON document. Keys of unknown type are left out.</summary>
        public static string ExportJson(IEnumerable<PrefEntry> entries, out int exported, out int skipped)
        {
            var list = (entries ?? ReadAll().Where(e => !IsInternalKey(e.Key))).ToList();
            var known = list.Where(e => e.Type != PrefType.Unknown).ToList();
            exported = known.Count;
            skipped = list.Count - known.Count;
            return PrefJson.FormatDocument(known);
        }

        public static string ExportJson(out int exported, out int skipped) => ExportJson(null, out exported, out skipped);

        public static HashSet<string> GetPinned() => new HashSet<string>(LoadList(PinnedListKey), StringComparer.Ordinal);

        public static void SetPinned(string key, bool pinned)
        {
            var pins = LoadList(PinnedListKey);
            if (pinned == pins.Contains(key)) return;
            if (pinned) pins.Add(key);
            else pins.Remove(key);
            SaveList(PinnedListKey, pins);
        }

        // Typed getters return the default when the stored type differs, so asking twice with different
        // defaults reveals the type. ponytail: best-effort; a platform that converts between types gives Unknown
        // and the OS store's type is used instead.
        static PrefType DetectType(string key)
        {
            bool isInt = PlayerPrefs.GetInt(key, 1) != 1 || PlayerPrefs.GetInt(key, 2) != 2;
            bool isFloat = PlayerPrefs.GetFloat(key, 1f) != 1f || PlayerPrefs.GetFloat(key, 2f) != 2f;
            bool isString = PlayerPrefs.GetString(key, "\u0001") != "\u0001" || PlayerPrefs.GetString(key, "\u0002") != "\u0002";

            if (isInt && !isFloat && !isString) return PrefType.Int;
            if (isFloat && !isInt && !isString) return PrefType.Float;
            if (isString && !isInt && !isFloat) return PrefType.String;
            return PrefType.Unknown;
        }

        static List<string> LoadList(string prefKey)
        {
            try
            {
                return JsonConvert.DeserializeObject<List<string>>(EditorPrefs.GetString(prefKey, "[]")) ?? new List<string>();
            }
            catch (JsonException)
            {
                return new List<string>();
            }
        }

        static void SaveList(string prefKey, List<string> keys) => EditorPrefs.SetString(prefKey, JsonConvert.SerializeObject(keys));
    }
}
