using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Xml;
using UnityEditor;
using UnityEngine;

namespace kinatraa.PlayerPrefEditor
{
    /// <summary>
    /// Lists the Editor's PlayerPrefs keys from the OS store, with the stored type where the store records one:
    /// the registry on Windows, the preferences domain on macOS and the prefs file on Linux.
    /// </summary>
    public static class NativePrefs
    {
        static string Company => PlayerSettings.companyName;
        static string Product => PlayerSettings.productName;
        static string MacDomain => $"unity.{Company}.{Product}";
        static string Home => Environment.GetFolderPath(Environment.SpecialFolder.Personal);

        public static bool Supported =>
            Application.platform == RuntimePlatform.WindowsEditor ||
            Application.platform == RuntimePlatform.OSXEditor ||
            Application.platform == RuntimePlatform.LinuxEditor;

        /// <summary>Where this Editor keeps PlayerPrefs for the current company and product.</summary>
        public static string Location
        {
            get
            {
                switch (Application.platform)
                {
                    case RuntimePlatform.WindowsEditor: return @"HKEY_CURRENT_USER\" + RegistryPath;
                    case RuntimePlatform.OSXEditor: return Path.Combine(Home, "Library", "Preferences", MacDomain + ".plist");
                    case RuntimePlatform.LinuxEditor:
                        var paths = LinuxPrefsPaths();
                        return Array.Find(paths, File.Exists) ?? paths[0];
                    default: return null;
                }
            }
        }

        static string RegistryPath => $@"Software\Unity\UnityEditor\{Company}\{Product}";

        // Unity documents ~/.config/unity3d for Linux PlayerPrefs; some Editor versions use ~/.local/share/unity3d.
        // Both are read, and PlayerPrefs.HasKey filters out anything that is not a live key.
        static string[] LinuxPrefsPaths()
        {
            var config = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
            if (string.IsNullOrEmpty(config)) config = Path.Combine(Home, ".config");
            var data = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
            if (string.IsNullOrEmpty(data)) data = Path.Combine(Home, ".local", "share");
            return new[]
            {
                Path.Combine(config, "unity3d", Company, Product, "prefs"),
                Path.Combine(data, "unity3d", Company, Product, "prefs"),
            };
        }

        /// <summary>The result of reading the OS store: <see cref="Keys"/> is null when it could not be read, and <see cref="Error"/> says why.</summary>
        public sealed class Snapshot
        {
            public readonly Dictionary<string, PrefType> Keys;
            public readonly string Error;

            public Snapshot(Dictionary<string, PrefType> keys, string error)
            {
                Keys = keys;
                Error = error;
            }
        }

        /// <summary>
        /// Keys in the OS store mapped to their stored type (Unknown when the store does not say).
        /// Returns null and an error when the store could not be read.
        /// </summary>
        public static Dictionary<string, PrefType> ReadKeys(out string error)
        {
            var snapshot = CreateReader()();
            error = snapshot.Error;
            return snapshot.Keys;
        }

        /// <summary>Reads the OS store on a worker thread, so a refresh never blocks the Editor (the macOS read starts a process).</summary>
        public static Task<Snapshot> ReadKeysAsync() => Task.Run(CreateReader());

        // Unity APIs only work on the main thread, so every path is resolved here and the returned reader touches none.
        static Func<Snapshot> CreateReader()
        {
            switch (Application.platform)
            {
                case RuntimePlatform.WindowsEditor:
                {
                    var path = RegistryPath;
                    return () => Guard(() => ReadRegistry(path));
                }
                case RuntimePlatform.OSXEditor:
                {
                    var domain = MacDomain;
                    return () => Guard(() => ReadMac(domain));
                }
                case RuntimePlatform.LinuxEditor:
                {
                    var paths = LinuxPrefsPaths();
                    return () => Guard(() => ReadLinux(paths));
                }
                default:
                    return () => new Snapshot(new Dictionary<string, PrefType>(), null);
            }
        }

        static Snapshot Guard(Func<Snapshot> read)
        {
            try
            {
                return read();
            }
            catch (Exception e)
            {
                return new Snapshot(null, e.Message);
            }
        }

        static Snapshot ReadMac(string domain)
        {
            // `defaults` reads through cfprefsd, so it sees values the Editor saved moments ago.
            if (Run("/usr/bin/defaults", $"export {Quote(domain)} -", out var stdout, out var stderr))
                return new Snapshot(ParsePlist(stdout), null);
            if (stderr.IndexOf("does not exist", StringComparison.OrdinalIgnoreCase) >= 0)
                return new Snapshot(new Dictionary<string, PrefType>(), null);
            return new Snapshot(null, "defaults export failed: " + stderr.Trim());
        }

        static Snapshot ReadLinux(string[] paths)
        {
            var result = new Dictionary<string, PrefType>(StringComparer.Ordinal);
            foreach (var path in paths)
            {
                if (!File.Exists(path)) continue;
                foreach (var pair in ParseLinuxPrefs(File.ReadAllText(path, Encoding.UTF8)))
                    if (!result.ContainsKey(pair.Key)) result[pair.Key] = pair.Value;
            }
            return new Snapshot(result, null);
        }

        /// <summary>Parses an XML property list: plist › dict › (key, value)*.</summary>
        public static Dictionary<string, PrefType> ParsePlist(string xml)
        {
            var result = new Dictionary<string, PrefType>(StringComparer.Ordinal);
            var dict = LoadXml(xml).SelectSingleNode("/plist/dict");
            if (dict == null) return result;

            string key = null;
            foreach (XmlNode node in dict.ChildNodes)
            {
                if (node.NodeType != XmlNodeType.Element) continue;
                if (node.Name == "key")
                {
                    key = node.InnerText;
                    continue;
                }
                if (key == null) continue;
                result[key] = node.Name == "integer" ? PrefType.Int
                    : node.Name == "real" ? PrefType.Float
                    : node.Name == "string" ? PrefType.String
                    : PrefType.Unknown;
                key = null;
            }
            return result;
        }

        /// <summary>Parses Unity's Linux prefs file: unity_prefs › pref[name, type].</summary>
        public static Dictionary<string, PrefType> ParseLinuxPrefs(string xml)
        {
            var result = new Dictionary<string, PrefType>(StringComparer.Ordinal);
            var prefs = LoadXml(xml).SelectNodes("//pref");
            if (prefs == null) return result;
            foreach (XmlElement pref in prefs)
            {
                var name = pref.GetAttribute("name");
                if (name.Length > 0) result[name] = PrefJson.ParseType(pref.GetAttribute("type"));
            }
            return result;
        }

        static readonly Regex RegistryHash = new Regex(@"^(.*)_h\d+$", RegexOptions.Singleline);

        /// <summary>Registry value names are "&lt;key&gt;_h&lt;hash&gt;"; returns the key.</summary>
        public static string StripRegistryHash(string valueName)
        {
            var m = RegistryHash.Match(valueName);
            return m.Success ? m.Groups[1].Value : valueName;
        }

        static Snapshot ReadRegistry(string path)
        {
            var result = new Dictionary<string, PrefType>(StringComparer.Ordinal);
#if UNITY_EDITOR_WIN
            using (var root = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(path))
            {
                if (root == null) return new Snapshot(result, null);
                foreach (var name in root.GetValueNames())
                {
                    // Strings are stored as binary; ints and floats both come back as numbers, so leave those to detection.
                    var kind = root.GetValueKind(name);
                    result[StripRegistryHash(name)] = kind == Microsoft.Win32.RegistryValueKind.Binary ? PrefType.String : PrefType.Unknown;
                }
            }
#endif
            return new Snapshot(result, null);
        }

        static XmlDocument LoadXml(string xml)
        {
            var doc = new XmlDocument { XmlResolver = null };
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null };
            using (var reader = XmlReader.Create(new StringReader(xml ?? ""), settings))
                doc.Load(reader);
            return doc;
        }

        static string Quote(string arg) => "\"" + arg.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

        static bool Run(string file, string args, out string stdout, out string stderr)
        {
            var psi = new ProcessStartInfo(file, args)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                CreateNoWindow = true,
            };
            using (var p = Process.Start(psi))
            {
                var output = p.StandardOutput.ReadToEndAsync();
                var errors = p.StandardError.ReadToEndAsync();
                if (!p.WaitForExit(3000))
                {
                    try { p.Kill(); } catch (InvalidOperationException) { }
                    stdout = "";
                    stderr = "timed out";
                    return false;
                }
                stdout = output.Result;
                stderr = errors.Result;
                return p.ExitCode == 0;
            }
        }
    }
}
