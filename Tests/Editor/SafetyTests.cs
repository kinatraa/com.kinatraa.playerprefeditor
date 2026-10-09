using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace kinatraa.PlayerPrefEditor.Tests
{
    public class TypeResolutionTests
    {
        [TestCase(true, false, false, PrefType.Unknown, PrefType.Int)]
        [TestCase(false, true, false, PrefType.String, PrefType.Float, TestName = "One answering getter beats the OS hint")]
        [TestCase(false, false, true, PrefType.Unknown, PrefType.String)]
        [TestCase(true, true, false, PrefType.Float, PrefType.Float, TestName = "Ambiguous, and the hinted getter answered")]
        [TestCase(true, true, false, PrefType.String, PrefType.Unknown, TestName = "Ambiguous, and the hinted getter did not answer")]
        [TestCase(true, true, true, PrefType.Unknown, PrefType.Unknown)]
        [TestCase(false, false, false, PrefType.String, PrefType.Unknown, TestName = "No getter answered: never trust the hint")]
        public void ResolvesTypesConservatively(bool isInt, bool isFloat, bool isString, PrefType hint, PrefType expected)
        {
            Assert.AreEqual(expected, PlayerPrefStore.ResolveType(isInt, isFloat, isString, hint));
        }
    }

    /// <summary>These tests write real PlayerPrefs under a unique prefix and delete them afterwards.</summary>
    public class HistorySafetyTests
    {
        readonly string _prefix = "kinatraa.PlayerPrefEditor.Test." + Guid.NewGuid().ToString("N") + ".";
        PrefHistory History => PrefHistory.instance;

        [SetUp]
        public void SetUp() => History.Clear();

        [TearDown]
        public void TearDown()
        {
            foreach (var key in PlayerPrefStore.GetKeys().Where(k => k.StartsWith(_prefix))) PlayerPrefs.DeleteKey(key);
            PlayerPrefs.Save();
            History.Clear();
        }

        PrefEntry Read(string suffix) => PlayerPrefStore.Read(_prefix + suffix);
        PrefEntry E(string suffix, PrefType type, object value) => new PrefEntry(_prefix + suffix, type, value);

        static void AssertEntry(PrefEntry actual, PrefType type, object value, string what)
        {
            Assert.IsNotNull(actual, what + ": missing");
            Assert.AreEqual(type, actual.Type, what + ": type");
            Assert.AreEqual(value, actual.Value, what + ": value");
        }

        [Test]
        public void BoundaryValuesRoundTripThroughPlayerPrefs()
        {
            var entries = new[]
            {
                E("int.max", PrefType.Int, int.MaxValue),
                E("int.min", PrefType.Int, int.MinValue),
                E("int.zero", PrefType.Int, 0),
                E("int.negative", PrefType.Int, -1),
                E("float.epsilon", PrefType.Float, float.Epsilon),
                E("float.max", PrefType.Float, float.MaxValue),
                E("float.min", PrefType.Float, float.MinValue),
                E("float.negative", PrefType.Float, -1.5f),
                E("float.zero", PrefType.Float, 0f),
                E("float.nan", PrefType.Float, float.NaN),
                E("string.empty", PrefType.String, ""),
                E("string.space", PrefType.String, " "),
                E("string.unicode", PrefType.String, "xin chào ✓ 日本語 😀"),
                E("string.long", PrefType.String, new string('a', 100_000)),
                E("string.json", PrefType.String, "{\"a\":[1,2]}"),
            };
            PlayerPrefStore.Apply(entries);
            foreach (var e in entries) AssertEntry(PlayerPrefStore.Read(e.Key), e.Type, e.Value, e.Key);
        }

        [Test]
        public void UndoAndRedoWalkThroughEditsTypeChangesAndRenames()
        {
            PlayerPrefStore.Write(E("a", PrefType.Int, 1));
            History.Commit("edit", E("a", PrefType.Int, 2));
            History.Commit("retype", E("a", PrefType.String, "three"));
            History.Commit("rename", new[] { E("b", PrefType.String, "three") }, new[] { _prefix + "a" });

            for (int round = 0; round < 2; round++)
            {
                Assert.AreEqual("rename", History.Undo());
                Assert.IsNull(Read("b"), "undoing a rename removes the new key");
                AssertEntry(Read("a"), PrefType.String, "three", "rename undone");
                Assert.AreEqual("retype", History.Undo());
                AssertEntry(Read("a"), PrefType.Int, 2, "type change undone");
                Assert.AreEqual("edit", History.Undo());
                AssertEntry(Read("a"), PrefType.Int, 1, "edit undone");
                Assert.IsNull(History.Undo(), "nothing left to undo");

                History.Redo();
                History.Redo();
                Assert.AreEqual("rename", History.Redo());
                Assert.IsNull(Read("a"));
                AssertEntry(Read("b"), PrefType.String, "three", "everything redone");
                Assert.IsNull(History.Redo(), "nothing left to redo");
                CollectionAssert.IsEmpty(History.LastUnrestorable);
            }
        }

        [Test]
        public void DeleteAllAndSelectiveImportAreUndoable()
        {
            PlayerPrefStore.Apply(new[] { E("k1", PrefType.Int, 1), E("k2", PrefType.Float, 2f), E("k3", PrefType.String, "3") });
            History.Commit("delete all", Array.Empty<PrefEntry>(), new[] { _prefix + "k1", _prefix + "k2", _prefix + "k3" });
            Assert.IsNull(Read("k1"));
            History.Undo();
            AssertEntry(Read("k1"), PrefType.Int, 1, "k1");
            AssertEntry(Read("k2"), PrefType.Float, 2f, "k2");
            AssertEntry(Read("k3"), PrefType.String, "3", "k3");

            // Import with one change and one addition, but only the addition checked.
            var rows = PrefDiff.Compute(new[] { E("k1", PrefType.Int, 10), E("k4", PrefType.String, "new") },
                new[] { Read("k1"), Read("k2"), Read("k3") }, replace: false);
            var selected = rows.Where(r => r.Key == _prefix + "k4").Select(r => r.After).ToList();
            History.Commit("import", selected, Array.Empty<string>());
            AssertEntry(Read("k1"), PrefType.Int, 1, "unchecked change is not applied");
            AssertEntry(Read("k4"), PrefType.String, "new", "checked addition is applied");
            History.Undo();
            Assert.IsNull(Read("k4"), "undo removes the imported key");
        }

        [Test]
        public void ReplaceOnlyRemovesKeysMissingFromTheImport()
        {
            var current = new[] { E("keep", PrefType.Int, 1), E("drop", PrefType.Int, 2), new PrefEntry("unity.player_sessionid", PrefType.String, "x") };
            var rows = PrefDiff.Compute(new[] { E("keep", PrefType.Int, 1) }, current, replace: true);
            CollectionAssert.AreEquivalent(new[] { _prefix + "drop" }, rows.Where(r => r.Kind == DiffKind.Removed).Select(r => r.Key));
            Assert.IsEmpty(PrefDiff.Compute(new[] { E("keep", PrefType.Int, 1) }, current, replace: false).Where(r => r.Kind == DiffKind.Removed));
        }

        [Test]
        public void DamagedHistoryStepIsNeitherAppliedNorLost()
        {
            PlayerPrefStore.Write(E("a", PrefType.Int, 1));
            History.Commit("edit", E("a", PrefType.Int, 2));

            var undo = (IList)typeof(PrefHistory).GetField("_undo", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(History);
            var step = undo[undo.Count - 1];
            step.GetType().GetField("Writes").SetValue(step, "{ damaged");
            step.GetType().GetField("Deletes").SetValue(step, new System.Collections.Generic.List<string> { _prefix + "a" });

            Assert.Throws<InvalidOperationException>(() => History.Undo());
            AssertEntry(Read("a"), PrefType.Int, 2, "nothing was applied, not even the step's deletes");
            Assert.IsTrue(History.CanUndo, "the step stays in the history");
        }

        [Test]
        public void UnreadableValuesAreReportedAndNeverClaimedRestorable()
        {
            var key = _prefix + "unreadable";
            PlayerPrefStore.Write(new PrefEntry(key, PrefType.Int, 1));
            var unreadable = new PrefEntry(key, PrefType.Unknown, null);

            PlayerPrefStore.ExportJson(new[] { unreadable }, out int exported, out int skipped);
            Assert.AreEqual((0, 1), (exported, skipped), "export leaves unreadable values out and says so");
            Assert.AreEqual(DiffKind.Changed, PrefDiff.Compute(new[] { new PrefEntry(key, PrefType.Int, 5) }, new[] { unreadable }, false).Single().Kind);

            // History sees the key as unreadable, the way an undetectable type looks.
            PrefHistory.Reader = k => k == key ? unreadable : PlayerPrefStore.Read(k);
            try
            {
                History.Commit("overwrite", new PrefEntry(key, PrefType.Int, 5));
                CollectionAssert.AreEqual(new[] { key }, History.LastUnrestorable, "the commit reports what Undo can't restore");

                Assert.AreEqual("overwrite", History.Undo());
                CollectionAssert.AreEqual(new[] { key }, History.LastUnrestorable, "undo reports the key it could not restore");
                AssertEntry(PlayerPrefStore.Read(key), PrefType.Int, 5, "the unreadable old value is not faked or deleted");
            }
            finally
            {
                PrefHistory.Reader = PlayerPrefStore.Read;
            }
        }

        [Test]
        [UnityPlatform(RuntimePlatform.OSXEditor, RuntimePlatform.WindowsEditor, RuntimePlatform.LinuxEditor)]
        public void AsyncOsReadMatchesTheSynchronousRead()
        {
            PlayerPrefStore.Write(E("async", PrefType.Int, 1));
            var sync = NativePrefs.ReadKeys(out var error);
            var snapshot = NativePrefs.ReadKeysAsync().Result;
            Assert.IsNull(error);
            Assert.IsNull(snapshot.Error);
            CollectionAssert.AreEquivalent(sync.Keys, snapshot.Keys.Keys);
            CollectionAssert.Contains(PlayerPrefStore.GetKeys(snapshot), _prefix + "async");
        }
    }
}
