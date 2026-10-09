using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace kinatraa.PlayerPrefEditor.Tests
{
    public class PrefJsonTests
    {
        static string Lf(string s) => s.Replace("\r\n", "\n");

        [Test]
        public void FormatsValuesAsJson()
        {
            Assert.AreEqual("5", PrefJson.FormatValue(PrefType.Int, 5));
            Assert.AreEqual("0.8", PrefJson.FormatValue(PrefType.Float, 0.8f));
            Assert.AreEqual("2.0", PrefJson.FormatValue(PrefType.Float, 2f));
            Assert.AreEqual("\"Hakien\"", PrefJson.FormatValue(PrefType.String, "Hakien"));
            Assert.AreEqual("\"say \\\"hi\\\"\"", PrefJson.FormatValue(PrefType.String, "say \"hi\""));
            Assert.AreEqual("\"NaN\"", PrefJson.FormatValue(PrefType.Float, float.NaN));
        }

        [Test]
        public void DocumentUsesSchemaWithTwoSpaceIndent()
        {
            var json = PrefJson.FormatDocument(new[]
            {
                new PrefEntry("player_name", PrefType.String, "Hakien"),
                new PrefEntry("master_volume", PrefType.Float, 0.8f),
                new PrefEntry("level", PrefType.Int, 5),
            });
            const string expected = @"{
  ""player_name"": {
    ""type"": ""string"",
    ""value"": ""Hakien""
  },
  ""master_volume"": {
    ""type"": ""float"",
    ""value"": 0.8
  },
  ""level"": {
    ""type"": ""int"",
    ""value"": 5
  }
}";
            Assert.AreEqual(Lf(expected), Lf(json));
        }

        [Test]
        public void DocumentRoundTrips()
        {
            var original = new[]
            {
                new PrefEntry("player_name", PrefType.String, "Hakien"),
                new PrefEntry("empty", PrefType.String, ""),
                new PrefEntry("date_like", PrefType.String, "2024-01-01T00:00:00Z"),
                new PrefEntry("embedded_json", PrefType.String, "{\"a\":[1,2]}"),
                new PrefEntry("unicode", PrefType.String, "xin chào ✓\nline 2\ttab"),
                new PrefEntry("NaN as text", PrefType.String, "NaN"),
                new PrefEntry("master_volume", PrefType.Float, 0.8f),
                new PrefEntry("tiny", PrefType.Float, 1.17549435E-38f),
                new PrefEntry("max", PrefType.Float, float.MaxValue),
                new PrefEntry("nan", PrefType.Float, float.NaN),
                new PrefEntry("inf", PrefType.Float, float.PositiveInfinity),
                new PrefEntry("-inf", PrefType.Float, float.NegativeInfinity),
                new PrefEntry("level", PrefType.Int, 5),
                new PrefEntry("min", PrefType.Int, int.MinValue),
                new PrefEntry("key with spaces and \"quotes\"", PrefType.Int, 1),
            };

            Assert.IsTrue(PrefJson.TryParseDocument(PrefJson.FormatDocument(original), out var parsed, out var error), error);
            Assert.AreEqual(original.Length, parsed.Count);
            for (int i = 0; i < original.Length; i++)
            {
                Assert.AreEqual(original[i].Key, parsed[i].Key);
                Assert.AreEqual(original[i].Type, parsed[i].Type, original[i].Key);
                Assert.AreEqual(original[i].Value, parsed[i].Value, original[i].Key);
                Assert.AreEqual(original[i].Value.GetType(), parsed[i].Value.GetType(), original[i].Key);
            }
        }

        [TestCase("5", PrefType.Int, 5)]
        [TestCase("-2147483648", PrefType.Int, int.MinValue)]
        [TestCase("0.8", PrefType.Float, 0.8f)]
        [TestCase("3", PrefType.Float, 3f)]
        [TestCase("\"Infinity\"", PrefType.Float, float.PositiveInfinity)]
        [TestCase("\"Hakien\"", PrefType.String, "Hakien")]
        [TestCase("  \"padded\"  ", PrefType.String, "padded")]
        [TestCase("\"5\" // trailing comment", PrefType.String, "5")]
        public void AcceptsMatchingValues(string json, PrefType type, object expected)
        {
            Assert.IsTrue(PrefJson.TryParseValue(json, type, out var value, out var error), error);
            Assert.AreEqual(expected, value);
        }

        [TestCase("5.5", PrefType.Int)]
        [TestCase("5.0", PrefType.Int)]
        [TestCase("\"5\"", PrefType.Int)]
        [TestCase("2147483648", PrefType.Int)]
        [TestCase("\"0.8\"", PrefType.Float)]
        [TestCase("\"nan\"", PrefType.Float)]
        [TestCase("true", PrefType.Float)]
        [TestCase("1e40", PrefType.Float)]
        [TestCase("5", PrefType.String)]
        [TestCase("null", PrefType.String)]
        [TestCase("{\"a\":1}", PrefType.String)]
        [TestCase("\"x\"", PrefType.Unknown)]
        public void RejectsTypeMismatch(string json, PrefType type)
        {
            Assert.IsFalse(PrefJson.TryParseValue(json, type, out _, out var error));
            Assert.IsNotEmpty(error);
            StringAssert.DoesNotContain("Invalid JSON", error);
        }

        [TestCase("")]
        [TestCase("   ")]
        [TestCase("{")]
        [TestCase("Hakien")]
        [TestCase("5 6")]
        [TestCase("\"unterminated")]
        public void RejectsInvalidJson(string json)
        {
            Assert.IsFalse(PrefJson.TryParseValue(json, PrefType.String, out var value, out var error));
            Assert.IsNull(value);
            StringAssert.StartsWith("Invalid JSON", error);
        }

        [TestCase("[]")]
        [TestCase("{\"\": {\"type\": \"int\", \"value\": 1}}")]
        [TestCase("{\"a\": 5}")]
        [TestCase("{\"a\": {\"value\": 5}}")]
        [TestCase("{\"a\": {\"type\": \"int\"}}")]
        [TestCase("{\"a\": {\"type\": \"bool\", \"value\": true}}")]
        [TestCase("{\"a\": {\"type\": \"unknown\", \"value\": null}}")]
        [TestCase("{\"a\": {\"type\": \"int\", \"value\": \"5\"}}")]
        [TestCase("{\"a\": {\"type\": \"int\", \"value\": 1}, \"a\": {\"type\": \"int\", \"value\": 2}}")]
        [TestCase("{\"ok\": {\"type\": \"int\", \"value\": 1}, \"bad\": {\"type\": \"float\", \"value\": \"x\"}}")]
        public void RejectsInvalidDocuments(string json)
        {
            Assert.IsFalse(PrefJson.TryParseDocument(json, out var entries, out var error));
            Assert.IsNotEmpty(error);
            Assert.IsEmpty(entries, "nothing partial may be returned");
        }

        [TestCase("5", PrefType.Int, PrefType.Float, "5.0")]
        [TestCase("2.0", PrefType.Float, PrefType.Int, "2")]
        [TestCase("-3", PrefType.Int, PrefType.String, "\"-3\"")]
        [TestCase("0.8", PrefType.Float, PrefType.String, "\"0.8\"")]
        [TestCase("\" 0.25 \"", PrefType.String, PrefType.Float, "0.25")]
        [TestCase("\"42\"", PrefType.String, PrefType.Int, "42")]
        [TestCase("\"x\"", PrefType.String, PrefType.String, "\"x\"")]
        public void ConvertsValueWhenTypeChanges(string json, PrefType from, PrefType to, string expected)
        {
            Assert.IsTrue(PrefJson.TryConvertText(json, from, to, out var converted));
            Assert.AreEqual(expected, converted);
        }

        [TestCase("2.5", PrefType.Float, PrefType.Int)]
        [TestCase("\"NaN\"", PrefType.Float, PrefType.Int)]
        [TestCase("3e9", PrefType.Float, PrefType.Int)]
        [TestCase("16777217", PrefType.Int, PrefType.Float)]
        [TestCase("\"abc\"", PrefType.String, PrefType.Int)]
        [TestCase("not json", PrefType.Int, PrefType.Float)]
        [TestCase("5", PrefType.Int, PrefType.Unknown)]
        public void KeepsTextWhenConversionWouldLoseTheValue(string json, PrefType from, PrefType to)
        {
            Assert.IsFalse(PrefJson.TryConvertText(json, from, to, out _));
        }

        [Test]
        public void ExpandsAndCollapsesEmbeddedJson()
        {
            Assert.IsTrue(PrefJson.TryExpandEmbedded("{\"lang\":\"en\",\"when\":\"2024-01-01T00:00:00Z\",\"n\":[1,2.5]}", out var pretty));
            Assert.AreEqual("{\n  \"lang\": \"en\",\n  \"when\": \"2024-01-01T00:00:00Z\",\n  \"n\": [\n    1,\n    2.5\n  ]\n}", Lf(pretty));

            Assert.IsTrue(PrefJson.TryCollapseEmbedded(pretty, false, out var compact, out var error), error);
            Assert.AreEqual("{\"lang\":\"en\",\"when\":\"2024-01-01T00:00:00Z\",\"n\":[1,2.5]}", compact);

            Assert.IsTrue(PrefJson.TryCollapseEmbedded("[1,  2]", true, out var indented, out _));
            Assert.AreEqual("[\n  1,\n  2\n]", Lf(indented));

            Assert.IsFalse(PrefJson.TryCollapseEmbedded("{\"a\":", false, out _, out error));
            StringAssert.StartsWith("Invalid JSON", error);
        }

        [TestCase("5")]
        [TestCase("\"text\"")]
        [TestCase("plain text")]
        [TestCase("")]
        [TestCase(null)]
        public void DoesNotExpandNonContainerStrings(string value)
        {
            Assert.IsFalse(PrefJson.TryExpandEmbedded(value, out _));
        }
    }

    public class NativePrefsTests
    {
        [Test]
        public void ParsesMacPlist()
        {
            const string xml = @"<?xml version=""1.0"" encoding=""UTF-8""?>
<!DOCTYPE plist PUBLIC ""-//Apple//DTD PLIST 1.0//EN"" ""http://www.apple.com/DTDs/PropertyList-1.0.dtd"">
<plist version=""1.0"">
<dict>
	<key>level</key>
	<integer>5</integer>
	<key>master_volume</key>
	<real>0.80000001192092896</real>
	<key>player &amp; name</key>
	<string>Hakien</string>
	<key>blob</key>
	<data>AAEC</data>
</dict>
</plist>";
            var keys = NativePrefs.ParsePlist(xml);
            Assert.AreEqual(4, keys.Count);
            Assert.AreEqual(PrefType.Int, keys["level"]);
            Assert.AreEqual(PrefType.Float, keys["master_volume"]);
            Assert.AreEqual(PrefType.String, keys["player & name"]);
            Assert.AreEqual(PrefType.Unknown, keys["blob"]);
        }

        [Test]
        public void ParsesLinuxPrefs()
        {
            const string xml = @"<unity_prefs version_major=""1"" version_minor=""1"">
	<pref name=""level"" type=""int"">5</pref>
	<pref name=""volume"" type=""float"">0.8</pref>
	<pref name=""player_name"" type=""string"">SGFraWVu</pref>
</unity_prefs>";
            var keys = NativePrefs.ParseLinuxPrefs(xml);
            Assert.AreEqual(PrefType.Int, keys["level"]);
            Assert.AreEqual(PrefType.Float, keys["volume"]);
            Assert.AreEqual(PrefType.String, keys["player_name"]);
        }

        [TestCase("level_h3203123", "level")]
        [TestCase("my_hash_h12_h99", "my_hash_h12")]
        [TestCase("no_hash", "no_hash")]
        [TestCase("ends_h", "ends_h")]
        public void StripsRegistryHash(string valueName, string key)
        {
            Assert.AreEqual(key, NativePrefs.StripRegistryHash(valueName));
        }

        [Test]
        [UnityPlatform(RuntimePlatform.OSXEditor, RuntimePlatform.WindowsEditor, RuntimePlatform.LinuxEditor)]
        public void ReadsKeysWrittenByTheEditor()
        {
            var key = "kinatraa.PlayerPrefEditor.Test.Native." + Guid.NewGuid().ToString("N");
            try
            {
                PlayerPrefs.SetString(key, "x");
                PlayerPrefs.Save();
                var keys = NativePrefs.ReadKeys(out var error);
                Assert.IsNull(error);
                Assert.IsTrue(keys.ContainsKey(key), "the OS store should list a saved key");
                Assert.AreEqual(PrefType.String, keys[key]);
            }
            finally
            {
                PlayerPrefs.DeleteKey(key);
                PlayerPrefs.Save();
            }
        }
    }

    public class PrefFilterTests
    {
        static readonly List<PrefEntry> Entries = new List<PrefEntry>
        {
            new PrefEntry("level", PrefType.Int, 5),
            new PrefEntry("Audio.Master", PrefType.Float, 0.8f),
            new PrefEntry("player_name", PrefType.String, "Hakien"),
            new PrefEntry("settings", PrefType.String, "{\"lang\":\"en\"}"),
            new PrefEntry("mystery", PrefType.Unknown, null),
            new PrefEntry("unity.player_sessionid", PrefType.String, "123"),
        };

        static List<string> Keys(PrefFilter f, ICollection<string> pinned = null) =>
            f.Apply(Entries, pinned ?? new HashSet<string>(), out _).Select(e => e.Key).ToList();

        [Test]
        public void SortsCaseInsensitiveAndHidesInternalKeys()
        {
            CollectionAssert.AreEqual(new[] { "Audio.Master", "level", "mystery", "player_name", "settings" }, Keys(new PrefFilter()));
            CollectionAssert.Contains(Keys(new PrefFilter { ShowInternal = true }), "unity.player_sessionid");
        }

        [Test]
        public void SearchesKeysAndValues()
        {
            CollectionAssert.AreEqual(new[] { "level" }, Keys(new PrefFilter { Query = "LEV" }));
            CollectionAssert.AreEqual(new[] { "player_name" }, Keys(new PrefFilter { Query = "hakien" }));
            CollectionAssert.IsEmpty(Keys(new PrefFilter { Query = "hakien", SearchValues = false }));
            CollectionAssert.AreEqual(new[] { "Audio.Master" }, Keys(new PrefFilter { Query = "0.8" }));
            CollectionAssert.AreEqual(new[] { "settings" }, Keys(new PrefFilter { Query = "\"lang\"" }));
        }

        [Test]
        public void SupportsRegexAndReportsInvalidOnes()
        {
            CollectionAssert.AreEqual(new[] { "level", "player_name" }, Keys(new PrefFilter { Query = "^(lev|play)", UseRegex = true, SearchValues = false }));

            var filter = new PrefFilter { Query = "([", UseRegex = true };
            var visible = filter.Apply(Entries, new HashSet<string>(), out var error);
            StringAssert.StartsWith("Invalid regex", error);
            Assert.AreEqual(5, visible.Count, "an invalid regex filters nothing");
        }

        [Test]
        public void FiltersByTypeAndSorts()
        {
            CollectionAssert.AreEqual(new[] { "player_name", "settings" }, Keys(new PrefFilter { Type = PrefType.String }));
            CollectionAssert.AreEqual(new[] { "mystery" }, Keys(new PrefFilter { Type = PrefType.Unknown }));
            CollectionAssert.AreEqual(new[] { "settings", "player_name", "mystery", "level", "Audio.Master" }, Keys(new PrefFilter { Sort = SortMode.NameDescending }));
            CollectionAssert.AreEqual(new[] { "level", "Audio.Master", "player_name", "settings", "mystery" }, Keys(new PrefFilter { Sort = SortMode.Type }));
        }

        [Test]
        public void PinnedKeysComeFirst()
        {
            CollectionAssert.AreEqual(new[] { "settings", "Audio.Master", "level", "mystery", "player_name" },
                Keys(new PrefFilter(), new HashSet<string> { "settings" }));
        }

        [Test]
        public void PreviewIsOneLineAndTruncated()
        {
            Assert.AreEqual("\"a\\nb\"", PrefFilter.Preview(new PrefEntry("k", PrefType.String, "a\nb")));
            Assert.AreEqual("\"aaaaa\"…", PrefFilter.Preview(new PrefEntry("k", PrefType.String, new string('a', 1_000_000)), 5));
            Assert.AreEqual("0.8", PrefFilter.Preview(new PrefEntry("k", PrefType.Float, 0.8f)));
        }

        [TestCase("unity.cloud_userid", true)]
        [TestCase("unity_connect.session_id", true)]
        [TestCase("UnityGraphicsQuality", true)]
        [TestCase("Screenmanager Resolution Width", true)]
        [TestCase("PT_Run", true)]
        [TestCase("unity_level", false)]
        [TestCase("community.name", false)]
        public void RecognizesInternalKeys(string key, bool internalKey)
        {
            Assert.AreEqual(internalKey, PlayerPrefStore.IsInternalKey(key));
        }
    }

    public class PrefDiffTests
    {
        static readonly PrefEntry[] Current =
        {
            new PrefEntry("same", PrefType.Int, 1),
            new PrefEntry("changed", PrefType.Int, 1),
            new PrefEntry("retyped", PrefType.Int, 1),
            new PrefEntry("nan", PrefType.Float, float.NaN),
            new PrefEntry("only_here", PrefType.String, "x"),
            new PrefEntry("unity.cloud_userid", PrefType.String, "id"),
        };

        static readonly PrefEntry[] Incoming =
        {
            new PrefEntry("same", PrefType.Int, 1),
            new PrefEntry("changed", PrefType.Int, 2),
            new PrefEntry("retyped", PrefType.Float, 1f),
            new PrefEntry("nan", PrefType.Float, float.NaN),
            new PrefEntry("new", PrefType.String, "y"),
        };

        [Test]
        public void MergeComparesKeyByKey()
        {
            var rows = PrefDiff.Compute(Incoming, Current, replace: false);
            Assert.AreEqual(1, PrefDiff.Count(rows, DiffKind.Added));
            Assert.AreEqual(2, PrefDiff.Count(rows, DiffKind.Changed));
            Assert.AreEqual(2, PrefDiff.Count(rows, DiffKind.Unchanged));
            Assert.AreEqual(0, PrefDiff.Count(rows, DiffKind.Removed));
            var changed = rows.Single(r => r.Key == "changed");
            Assert.AreEqual(1, changed.Before.Value);
            Assert.AreEqual(2, changed.After.Value);
        }

        [Test]
        public void ReplaceRemovesMissingKeysButNeverInternalOnes()
        {
            var rows = PrefDiff.Compute(Incoming, Current, replace: true);
            var removed = rows.Where(r => r.Kind == DiffKind.Removed).Select(r => r.Key).ToList();
            CollectionAssert.AreEqual(new[] { "only_here" }, removed);
            Assert.IsNull(rows.Single(r => r.Key == "only_here").After);
        }
    }

    /// <summary>These tests write real PlayerPrefs under a unique prefix and delete them afterwards.</summary>
    public class PlayerPrefStoreTests
    {
        readonly string _prefix = "kinatraa.PlayerPrefEditor.Test." + Guid.NewGuid().ToString("N") + ".";

        [TearDown]
        public void TearDown()
        {
            var keys = PlayerPrefStore.GetKeys().Where(k => k.StartsWith(_prefix)).ToList();
            PlayerPrefStore.Commit(Array.Empty<PrefEntry>(), keys);
        }

        [Test]
        public void WriteReadDeleteRoundTrip()
        {
            var entries = new[]
            {
                new PrefEntry(_prefix + "int", PrefType.Int, 42),
                new PrefEntry(_prefix + "float", PrefType.Float, 0.25f),
                new PrefEntry(_prefix + "string", PrefType.String, "hello"),
                new PrefEntry(_prefix + "empty", PrefType.String, ""),
                new PrefEntry(_prefix + "zero", PrefType.Int, 0),
            };
            PlayerPrefStore.Apply(entries);

            var keys = PlayerPrefStore.GetKeys();
            foreach (var e in entries)
            {
                Assert.Contains(e.Key, keys);
                var read = PlayerPrefStore.Read(e.Key);
                Assert.AreEqual(e.Type, read.Type, e.Key);
                Assert.AreEqual(e.Value, read.Value, e.Key);
            }

            PlayerPrefStore.Delete(entries[0].Key);
            Assert.IsFalse(PlayerPrefs.HasKey(entries[0].Key));
            Assert.IsNull(PlayerPrefStore.Read(entries[0].Key));
            CollectionAssert.DoesNotContain(PlayerPrefStore.GetKeys(), entries[0].Key);
        }

        [Test]
        public void WriteChangesType()
        {
            var key = _prefix + "changes";
            PlayerPrefStore.Write(new PrefEntry(key, PrefType.Int, 1));
            PlayerPrefStore.Write(new PrefEntry(key, PrefType.String, "now a string"));
            var read = PlayerPrefStore.Read(key);
            Assert.AreEqual(PrefType.String, read.Type);
            Assert.AreEqual("now a string", read.Value);
        }

        [Test]
        public void InvalidCommitChangesNothing()
        {
            var keep = _prefix + "keep";
            PlayerPrefStore.Write(new PrefEntry(keep, PrefType.Int, 1));
            Assert.Throws<ArgumentException>(() => PlayerPrefStore.Commit(new[]
            {
                new PrefEntry(_prefix + "valid", PrefType.Int, 2),
                new PrefEntry(_prefix + "invalid", PrefType.Int, "not an int"),
            }, new[] { keep }));
            Assert.IsTrue(PlayerPrefs.HasKey(keep));
            Assert.IsFalse(PlayerPrefs.HasKey(_prefix + "valid"));
        }

        [Test]
        public void ExportImportRoundTrip()
        {
            var key = _prefix + "export";
            PlayerPrefStore.Write(new PrefEntry(key, PrefType.Float, 0.5f));
            var json = PlayerPrefStore.ExportJson(out _, out _);
            PlayerPrefStore.Delete(key);

            Assert.IsTrue(PrefJson.TryParseDocument(json, out var entries, out var error), error);
            PlayerPrefStore.Apply(entries.Where(e => e.Key == key).ToList());
            Assert.AreEqual(0.5f, PlayerPrefs.GetFloat(key));
        }

        [Test]
        public void HistoryUndoesAndRedoes()
        {
            var history = PrefHistory.instance;
            string a = _prefix + "a", b = _prefix + "b";
            PlayerPrefStore.Write(new PrefEntry(a, PrefType.Int, 1));
            try
            {
                history.Commit("test", new[] { new PrefEntry(a, PrefType.String, "two"), new PrefEntry(b, PrefType.Float, 3f) }, Array.Empty<string>());
                Assert.AreEqual("test", history.UndoLabel);

                Assert.AreEqual("test", history.Undo());
                Assert.AreEqual(PrefType.Int, PlayerPrefStore.Read(a).Type);
                Assert.AreEqual(1, PlayerPrefStore.Read(a).Value);
                Assert.IsFalse(PlayerPrefs.HasKey(b), "undo deletes keys the step created");

                Assert.AreEqual("test", history.Redo());
                Assert.AreEqual("two", PlayerPrefStore.Read(a).Value);
                Assert.AreEqual(3f, PlayerPrefStore.Read(b).Value);

                history.Commit("delete", Array.Empty<PrefEntry>(), new[] { a, b });
                Assert.IsFalse(PlayerPrefs.HasKey(a));
                history.Undo();
                Assert.AreEqual("two", PlayerPrefStore.Read(a).Value, "undo restores deleted keys");
                Assert.AreEqual(3f, PlayerPrefStore.Read(b).Value);
            }
            finally
            {
                history.Clear();
            }
        }

        [Test]
        public void CommitRaisesChangedOncePerKeyEvenWhenAHandlerThrows()
        {
            string a = _prefix + "a", b = _prefix + "b", gone = _prefix + "gone";
            PlayerPrefStore.Write(new PrefEntry(gone, PrefType.Int, 1));

            var seen = new List<string>();
            Action<string> broken = _ => throw new InvalidOperationException("handler bug");
            Action<string> record = k => { if (k.StartsWith(_prefix)) seen.Add(k); };
            PlayerPrefEvents.Changed += broken;
            PlayerPrefEvents.Changed += record;
            try
            {
                for (int i = 0; i < 3; i++) LogAssert.Expect(LogType.Exception, new Regex("handler bug"));
                PrefHistory.instance.Commit("events", new[] { new PrefEntry(a, PrefType.Int, 1), new PrefEntry(b, PrefType.String, "x") }, new[] { gone });

                CollectionAssert.AreEquivalent(new[] { a, b, gone }, seen);
                Assert.AreEqual(1, PlayerPrefs.GetInt(a), "a throwing handler must not undo the save");
                Assert.AreEqual("events", PrefHistory.instance.UndoLabel, "the step is still recorded");
            }
            finally
            {
                PlayerPrefEvents.Changed -= broken;
                PlayerPrefEvents.Changed -= record;
                PrefHistory.instance.Clear();
            }
        }

        [Test]
        public void FailedHistoryCommitRecordsNothing()
        {
            var history = PrefHistory.instance;
            history.Clear();
            Assert.Throws<ArgumentException>(() => history.Commit("bad", new[] { new PrefEntry(_prefix + "x", PrefType.Float, 1) }, Array.Empty<string>()));
            Assert.IsFalse(history.CanUndo);
        }
    }
}
