using System;
using System.Linq;
using NUnit.Framework;
using UnityEngine;

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
                new PrefEntry("date_like", PrefType.String, "2024-01-01T00:00:00Z"),
                new PrefEntry("embedded_json", PrefType.String, "{\"a\":[1,2]}"),
                new PrefEntry("unicode", PrefType.String, "xin chào ✓\nline 2"),
                new PrefEntry("master_volume", PrefType.Float, 0.8f),
                new PrefEntry("tiny", PrefType.Float, 1.17549435E-38f),
                new PrefEntry("level", PrefType.Int, 5),
                new PrefEntry("min", PrefType.Int, int.MinValue),
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
        [TestCase("\"Hakien\"", PrefType.String, "Hakien")]
        [TestCase("  \"padded\"  ", PrefType.String, "padded")]
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
    }

    public class PlayerPrefStoreTests
    {
        readonly string _prefix = "kinatraa.PlayerPrefEditor.Test." + Guid.NewGuid().ToString("N") + ".";

        [TearDown]
        public void TearDown()
        {
            foreach (var key in PlayerPrefStore.GetKeys().Where(k => k.StartsWith(_prefix)))
                PlayerPrefStore.Delete(key);
        }

        [Test]
        public void WriteReadDeleteRoundTrip()
        {
            var entries = new[]
            {
                new PrefEntry(_prefix + "int", PrefType.Int, 42),
                new PrefEntry(_prefix + "float", PrefType.Float, 0.25f),
                new PrefEntry(_prefix + "string", PrefType.String, "hello"),
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
        public void PreviewCountsAddedChangedUnchanged()
        {
            PlayerPrefStore.Apply(new[]
            {
                new PrefEntry(_prefix + "same", PrefType.Int, 1),
                new PrefEntry(_prefix + "changed", PrefType.Int, 1),
            });
            var p = PlayerPrefStore.Preview(new[]
            {
                new PrefEntry(_prefix + "same", PrefType.Int, 1),
                new PrefEntry(_prefix + "changed", PrefType.Float, 1f),
                new PrefEntry(_prefix + "new", PrefType.String, "x"),
            });
            Assert.AreEqual(1, p.Added);
            Assert.AreEqual(1, p.Changed);
            Assert.AreEqual(1, p.Unchanged);
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
    }
}
