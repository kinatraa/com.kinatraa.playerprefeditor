using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
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
                    case RuntimePlatform.WindowsEditor: return $@"HKEY_CURRENT_USER\Software\Unity\UnityEditor\{Company}\{Product}";
                    case RuntimePlatform.OSXEditor: return Path.Combine(Home, "Library", "Preferences", MacDomain + ".plist");
                    case RuntimePlatform.LinuxEditor: return LinuxPrefsPath;
                    default: return null;
                }
            }
        }

        static string LinuxPrefsPath
        {
            get
            {
                var config = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
                if (string.IsNullOrEmpty(config)) config = Path.Combine(Home, ".config");
                return Path.Combine(config, "unity3d", Company, Product, "prefs");
            }
        }

        /// <summary>
        /// Keys in the OS store mapped to their stored type (Unknown when the store does not say).
        /// Returns null and an error when the store could not be read.
        /// </summary>
        public static Dictionary<string, PrefType> ReadKeys(out string error)
        {
            error = null;
            try
            {
                switch (Application.platform)
                {
                    case RuntimePlatform.WindowsEditor: return ReadRegistry();
                    case RuntimePlatform.OSXEditor: return ReadMac(out error);
                    case RuntimePlatform.LinuxEditor:
                        return File.Exists(LinuxPrefsPath) ? ParseLinuxPrefs(File.ReadAllText(LinuxPrefsPath)) : new Dictionary<string, PrefType>();
                    default: return new Dictionary<string, PrefType>();
                }
            }
            catch (Exception e)
            {
                error = e.Message;
                return null;
            }
        }

        static Dictionary<string, PrefType> ReadMac(out string error)
        {
            // `defaults` reads through cfprefsd, so it sees values the Editor saved moments ago.
            if (Run("/usr/bin/defaults", $"export {Quote(MacDomain)} -", out var stdout, out var stderr))
            {
                error = null;
                return ParsePlist(stdout);
            }
            if (stderr.IndexOf("does not exist", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                error = null;
                return new Dictionary<string, PrefType>();
            }
            error = "defaults export failed: " + stderr.Trim();
            return null;
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

        static Dictionary<string, PrefType> ReadRegistry()
        {
            var result = new Dictionary<string, PrefType>(StringComparer.Ordinal);
#if UNITY_EDITOR_WIN
            using (var root = Microsoft.Win32.Registry.CurrentUser.OpenSubKey($@"Software\Unity\UnityEditor\{Company}\{Product}"))
            {
                if (root == null) return result;
                foreach (var name in root.GetValueNames())
                {
                    // Strings are stored as binary; ints and floats both come back as numbers, so leave those to detection.
                    var kind = root.GetValueKind(name);
                    result[StripRegistryHash(name)] = kind == Microsoft.Win32.RegistryValueKind.Binary ? PrefType.String : PrefType.Unknown;
                }
            }
#endif
            return result;
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
                CreateNoWindow = true,
            };
            using (var p = Process.Start(psi))
            {
                var output = p.StandardOutput.ReadToEndAsync();
                var errors = p.StandardError.ReadToEndAsync();
                if (!p.WaitForExit(5000))
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
