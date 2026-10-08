using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using UnityEditor;
using UnityEngine;

namespace kinatraa.PlayerPrefEditor
{
    /// <summary>
    /// Reads and writes the Editor's PlayerPrefs. Unity cannot list PlayerPrefs keys, so keys come from the
    /// registry on Windows, plus a per-project key list in EditorPrefs that every key written here is added to.
    /// </summary>
    public static class PlayerPrefStore
    {
        public struct ImportPreview
        {
            public int Added, Changed, Unchanged;
        }

        /// <summary>True when keys are read from the OS (Windows registry), false when only the tracked list is used.</summary>
        public static bool NativeEnumeration => Application.platform == RuntimePlatform.WindowsEditor;

        // EditorPrefs are shared by every project on the machine, so scope the list to this project's PlayerPrefs.
        static string TrackedListKey => $"kinatraa.PlayerPrefEditor.Keys.{PlayerSettings.companyName}.{PlayerSettings.productName}";

        /// <summary>All known keys, sorted alphabetically.</summary>
        public static List<string> GetKeys()
        {
            var tracked = LoadTracked();
            if (tracked.RemoveAll(k => !PlayerPrefs.HasKey(k)) > 0) SaveTracked(tracked);

            var keys = new HashSet<string>(tracked, StringComparer.Ordinal);
#if UNITY_EDITOR_WIN
            keys.UnionWith(RegistryKeys());
#endif
            var sorted = keys.ToList();
            sorted.Sort((a, b) =>
            {
                int c = string.Compare(a, b, StringComparison.OrdinalIgnoreCase);
                return c != 0 ? c : string.CompareOrdinal(a, b);
            });
            return sorted;
        }

        public static PrefEntry Read(string key)
        {
            var type = DetectType(key);
            switch (type)
            {
                case PrefType.Int: return new PrefEntry(key, type, PlayerPrefs.GetInt(key));
                case PrefType.Float: return new PrefEntry(key, type, PlayerPrefs.GetFloat(key));
                case PrefType.String: return new PrefEntry(key, type, PlayerPrefs.GetString(key));
                default: return new PrefEntry(key, type, null);
            }
        }

        public static void Write(PrefEntry entry) => Apply(new[] { entry });

        /// <summary>Writes every entry, then saves once.</summary>
        public static void Apply(IReadOnlyCollection<PrefEntry> entries)
        {
            foreach (var e in entries)
            {
                // Delete first so a type change never leaves the old value behind.
                PlayerPrefs.DeleteKey(e.Key);
                switch (e.Type)
                {
                    case PrefType.Int: PlayerPrefs.SetInt(e.Key, (int)e.Value); break;
                    case PrefType.Float: PlayerPrefs.SetFloat(e.Key, (float)e.Value); break;
                    case PrefType.String: PlayerPrefs.SetString(e.Key, (string)e.Value); break;
                    default: throw new ArgumentException($"\"{e.Key}\" has no type; pick int, float or string.");
                }
            }
            PlayerPrefs.Save();
            Track(entries.Select(e => e.Key));
        }

        public static void Delete(string key)
        {
            PlayerPrefs.DeleteKey(key);
            PlayerPrefs.Save();
            var tracked = LoadTracked();
            if (tracked.Remove(key)) SaveTracked(tracked);
        }

        /// <summary>Adds keys that already exist in PlayerPrefs to the tracked list.</summary>
        public static void Track(IEnumerable<string> keys)
        {
            var tracked = LoadTracked();
            var added = keys.Where(k => !tracked.Contains(k)).Distinct().ToList();
            if (added.Count == 0) return;
            tracked.AddRange(added);
            SaveTracked(tracked);
        }

        public static ImportPreview Preview(IEnumerable<PrefEntry> entries)
        {
            var p = new ImportPreview();
            foreach (var e in entries)
            {
                if (!PlayerPrefs.HasKey(e.Key)) p.Added++;
                else
                {
                    var current = Read(e.Key);
                    if (current.Type == e.Type && Equals(current.Value, e.Value)) p.Unchanged++;
                    else p.Changed++;
                }
            }
            return p;
        }

        /// <summary>The whole store as a formatted JSON document. Keys of unknown type are left out.</summary>
        public static string ExportJson(out int exported, out int skipped)
        {
            var entries = GetKeys().Select(Read).ToList();
            var known = entries.Where(e => e.Type != PrefType.Unknown).ToList();
            exported = known.Count;
            skipped = entries.Count - known.Count;
            return PrefJson.FormatDocument(known);
        }

        // Typed getters return the default when the stored type differs, so asking twice with different
        // defaults reveals the type. ponytail: best-effort; a platform that converts between types gives Unknown.
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

        static List<string> LoadTracked()
        {
            try
            {
                return JsonConvert.DeserializeObject<List<string>>(EditorPrefs.GetString(TrackedListKey, "[]")) ?? new List<string>();
            }
            catch (JsonException)
            {
                return new List<string>();
            }
        }

        static void SaveTracked(List<string> keys) => EditorPrefs.SetString(TrackedListKey, JsonConvert.SerializeObject(keys));

#if UNITY_EDITOR_WIN
        // Editor PlayerPrefs live under HKCU\Software\Unity\UnityEditor\<company>\<product>
        // (builds use HKCU\Software\<company>\<product>). Value names end in "_h<hash>".
        static IEnumerable<string> RegistryKeys()
        {
            var path = $@"Software\Unity\UnityEditor\{PlayerSettings.companyName}\{PlayerSettings.productName}";
            using (var root = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(path))
            {
                if (root == null) return Array.Empty<string>();
                return root.GetValueNames()
                    .Select(n =>
                    {
                        int i = n.LastIndexOf("_h", StringComparison.Ordinal);
                        return i < 0 ? n : n.Substring(0, i);
                    })
                    .Where(PlayerPrefs.HasKey)
                    .ToList();
            }
        }
#endif
    }
}
