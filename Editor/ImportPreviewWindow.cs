using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using Severity = kinatraa.PlayerPrefEditor.PrefStyles.Severity;

namespace kinatraa.PlayerPrefEditor
{
    /// <summary>Shows what an import would change, key by key, and applies the checked rows as one undoable step.</summary>
    public sealed class ImportPreviewWindow : EditorWindow
    {
        // Serialized so the preview survives script reloads (the callback does not; the main window refreshes on its own).
        [SerializeField] string _source;
        [SerializeField] string _incomingJson;
        [SerializeField] bool _replace, _showUnchanged;
        [SerializeField] List<string> _unchecked = new List<string>();

        List<PrefEntry> _incoming;
        Action<string> _onApplied;
        List<DiffRow> _rows = new List<DiffRow>();
        readonly List<DiffRow> _visible = new List<DiffRow>();

        ListView _list;
        Label _modeHelp, _added, _changed, _removed, _unchangedPill, _empty;
        Button _merge, _replaceButton, _apply;
        PrefBanner _warning, _stale;

        /// <param name="onApplied">Called with a status message after the import was applied.</param>
        public static void Open(string source, List<PrefEntry> incoming, Action<string> onApplied)
        {
            var w = CreateInstance<ImportPreviewWindow>();
            w.titleContent = new GUIContent("Import PlayerPrefs");
            w._source = source;
            w._incomingJson = PrefJson.FormatDocument(incoming);
            w._incoming = incoming;
            w._onApplied = onApplied;
            w.minSize = new Vector2(480, 320);
            w.position = new Rect(w.position.x, w.position.y, 720, 460);
            w.ShowUtility();
        }

        void OnFocus()
        {
            // PlayerPrefs may have changed while another window had focus.
            if (_list != null) Recompute();
        }

        void CreateGUI()
        {
            if (_incoming == null && !PrefJson.TryParseDocument(_incomingJson, out _incoming, out _))
            {
                EditorApplication.delayCall += Close;
                return;
            }

            var root = rootVisualElement;
            PrefStyles.ApplyTheme(root);
            root.AddToClassList("ppe-dialog");

            root.Add(PrefStyles.Text("Import PlayerPrefs", "ppe-dialog-title"));
            root.Add(PrefStyles.Text($"From {_source} · {_incoming.Count} {(_incoming.Count == 1 ? "key" : "keys")}", "ppe-dialog-subtitle", "ppe-dim").Ellipsis());

            var segmented = PrefStyles.Box("ppe-segmented");
            segmented.Add(_merge = new Button(() => SetMode(false)) { text = "Merge" });
            segmented.Add(_replaceButton = new Button(() => SetMode(true)) { text = "Replace" });
            _modeHelp = PrefStyles.Text("", "ppe-mode-help", "ppe-dim");
            var modeRow = PrefStyles.Row(segmented, _modeHelp);
            modeRow.style.alignItems = Align.FlexStart;
            root.Add(modeRow);

            var showUnchanged = new Toggle { text = "Show unchanged", value = _showUnchanged };
            showUnchanged.RegisterValueChangedCallback(e =>
            {
                _showUnchanged = e.newValue;
                Refresh();
            });
            root.Add(PrefStyles.Row(
                _added = PrefStyles.Text("", "ppe-pill"),
                _changed = PrefStyles.Text("", "ppe-pill"),
                _removed = PrefStyles.Text("", "ppe-pill"),
                _unchangedPill = PrefStyles.Text("", "ppe-pill"),
                PrefStyles.Spacer(),
                showUnchanged).Classes("ppe-summary"));

            root.Add(_stale = new PrefBanner());
            root.Add(_warning = new PrefBanner());

            var table = PrefStyles.Box("ppe-table");
            table.Add(PrefStyles.Row(
                PrefStyles.Box("ppe-diff-toggle"),
                PrefStyles.Text("Change", "ppe-diff-kind").Ellipsis(),
                PrefStyles.Text("Key", "ppe-diff-key").Ellipsis(),
                PrefStyles.Text("Current  →  Incoming", "ppe-diff-change").Ellipsis()).Classes("ppe-table-header"));
            _list = new ListView(_visible, 22, MakeRow, BindRow) { selectionType = SelectionType.None, showAlternatingRowBackgrounds = AlternatingRowBackground.ContentOnly };
            _list.style.flexGrow = 1;
            table.Add(_list);
            _empty = PrefStyles.Text("", "ppe-empty-text", "ppe-dim");
            _empty.style.alignSelf = Align.Center;
            _empty.style.marginTop = 16;
            table.Add(_empty);
            root.Add(table);

            var footer = PrefStyles.Row(
                new Button(() => SetAll(true)) { text = "Select All" }.Classes("ppe-mini-button"),
                new Button(() => SetAll(false)) { text = "Select None" }.Classes("ppe-mini-button"),
                PrefStyles.Spacer(),
                new Button(Close) { text = "Cancel" },
                _apply = new Button(Apply).Classes("ppe-primary"));
            footer.AddToClassList("ppe-footer");
            footer.style.paddingBottom = 0;
            root.Add(footer);

            Recompute();
        }

        void SetMode(bool replace)
        {
            _replace = replace;
            Recompute();
        }

        void Recompute()
        {
            _rows = PrefDiff.Compute(_incoming, PlayerPrefStore.ReadAll(), _replace);
            Refresh();
        }

        List<DiffRow> Checked() => _rows.Where(r => r.Kind != DiffKind.Unchanged && !_unchecked.Contains(r.Key)).ToList();

        void Refresh()
        {
            _merge.EnableInClassList("ppe-selected", !_replace);
            _replaceButton.EnableInClassList("ppe-selected", _replace);
            _modeHelp.text = _replace
                ? "Adds and updates keys, and deletes keys that are not in the import. Unity's internal keys are kept."
                : "Adds new keys and updates changed ones. Keys that are not in the import stay as they are.";

            // Group by what happens, so a few additions are not buried among many removals.
            _visible.Clear();
            _visible.AddRange(_rows.Where(r => _showUnchanged || r.Kind != DiffKind.Unchanged).OrderBy(r => KindOrder(r.Kind)));
            _list.RefreshItems();
            _list.style.display = _visible.Count > 0 ? DisplayStyle.Flex : DisplayStyle.None;
            _empty.style.display = _visible.Count > 0 ? DisplayStyle.None : DisplayStyle.Flex;
            _empty.text = _rows.Count == 0 ? "The import is empty." : "Every key already matches. There is nothing to import.";

            int added = PrefDiff.Count(_rows, DiffKind.Added), changed = PrefDiff.Count(_rows, DiffKind.Changed);
            int unchanged = PrefDiff.Count(_rows, DiffKind.Unchanged), removed = PrefDiff.Count(_rows, DiffKind.Removed);
            PrefStyles.SetDiff(_added, DiffKind.Added, $"{added} added");
            PrefStyles.SetDiff(_changed, DiffKind.Changed, $"{changed} changed");
            PrefStyles.SetDiff(_removed, DiffKind.Removed, $"{removed} removed");
            PrefStyles.SetDiff(_unchangedPill, DiffKind.Unchanged, $"{unchanged} unchanged");
            _removed.style.display = _replace ? DisplayStyle.Flex : DisplayStyle.None;

            var selected = Checked();
            int deleting = selected.Count(r => r.Kind == DiffKind.Removed);
            int unreadable = selected.Count(r => r.Before != null && r.Before.Type == PrefType.Unknown);
            var warnings = new List<string>();
            if (deleting > 0) warnings.Add($"{deleting} {(deleting == 1 ? "key" : "keys")} will be deleted.");
            if (unreadable > 0) warnings.Add($"{unreadable} current {(unreadable == 1 ? "value has" : "values have")} an unknown type and can't be restored with Undo.");
            _warning.Set(warnings.Count > 0 ? string.Join(" ", warnings) : null, Severity.Warning);

            _apply.text = selected.Count == 0 ? "Nothing to Import" : $"Import {selected.Count} {(selected.Count == 1 ? "Change" : "Changes")}";
            _apply.SetEnabled(selected.Count > 0);
        }

        void SetAll(bool on)
        {
            _unchecked.Clear();
            if (!on) _unchecked.AddRange(_rows.Select(r => r.Key));
            Refresh();
        }

        VisualElement MakeRow()
        {
            var row = PrefStyles.Box("ppe-diff-row");
            var toggle = new Toggle().Classes("ppe-diff-toggle");
            toggle.RegisterValueChangedCallback(e =>
            {
                if (!(toggle.userData is string key)) return;
                if (e.newValue) _unchecked.Remove(key);
                else if (!_unchecked.Contains(key)) _unchecked.Add(key);
                Refresh();
            });
            row.Add(toggle);
            var kind = PrefStyles.Box("ppe-diff-kind");
            kind.Add(PrefStyles.Text("", "ppe-pill"));
            row.Add(kind);
            row.Add(PrefStyles.Text("", "ppe-diff-key").Ellipsis());
            row.Add(PrefStyles.Text("", "ppe-diff-change", "ppe-dim").Ellipsis().Mono());
            return row;
        }

        void BindRow(VisualElement row, int index)
        {
            var r = _visible[index];
            var toggle = row.Q<Toggle>();
            toggle.userData = r.Key;
            toggle.SetValueWithoutNotify(r.Kind != DiffKind.Unchanged && !_unchecked.Contains(r.Key));
            toggle.SetEnabled(r.Kind != DiffKind.Unchanged);
            PrefStyles.SetDiff(row.Q<Label>(className: "ppe-pill"), r.Kind, r.Kind.ToString().ToLowerInvariant());

            var key = row.Q<Label>(className: "ppe-diff-key");
            key.text = r.Key;
            key.tooltip = r.Key;
            var change = row.Q<Label>(className: "ppe-diff-change");
            switch (r.Kind)
            {
                case DiffKind.Added: change.text = $"—  →  {Describe(r.After)}"; break;
                case DiffKind.Removed: change.text = $"{Describe(r.Before)}  →  deleted"; break;
                case DiffKind.Unchanged: change.text = Describe(r.After); break;
                default: change.text = $"{Describe(r.Before)}  →  {Describe(r.After)}"; break;
            }
            change.tooltip = change.text;
        }

        // Deleting keys is the one destructive part of an import, so it gets an explicit confirmation. Tests can skip it.
        internal static Func<int, int, bool> ConfirmDeletes = (deletes, writes) => EditorUtility.DisplayDialog("Delete keys?",
            $"This import deletes {deletes} {(deletes == 1 ? "key" : "keys")} that {(deletes == 1 ? "is" : "are")} not in the file" +
            (writes > 0 ? $", and adds or updates {writes}." : ".") + "\n\nYou can undo the whole import with Undo until Unity closes.",
            "Import and Delete", "Cancel");

        static int KindOrder(DiffKind kind) => kind == DiffKind.Added ? 0 : kind == DiffKind.Changed ? 1 : kind == DiffKind.Removed ? 2 : 3;

        static string Describe(PrefEntry e) => e.Type == PrefType.Unknown ? "unknown (unreadable)" : $"{PrefJson.TypeName(e.Type)} {PrefFilter.Preview(e, 60)}";

        void Apply()
        {
            // Never apply a stale preview: if PlayerPrefs moved since it was built, show the new diff instead.
            var shown = _rows.ToDictionary(r => r.Key);
            var fresh = PrefDiff.Compute(_incoming, PlayerPrefStore.ReadAll(), _replace);
            bool stale = fresh.Count != _rows.Count || fresh.Any(r => !shown.TryGetValue(r.Key, out var old) || old.Kind != r.Kind || !PrefDiff.Same(old.Before, r.Before));
            if (stale)
            {
                _rows = fresh;
                Refresh();
                _stale.Set("PlayerPrefs changed while this preview was open. The list now shows the current state. Review it and import again.", Severity.Warning);
                return;
            }
            _stale.Set(null);

            var rows = Checked();
            if (rows.Count == 0) return;
            var writes = rows.Where(r => r.Kind != DiffKind.Removed).Select(r => r.After).ToList();
            var deletes = rows.Where(r => r.Kind == DiffKind.Removed).Select(r => r.Key).ToList();
            if (deletes.Count > 0 && !ConfirmDeletes(deletes.Count, writes.Count)) return;
            try
            {
                PrefHistory.instance.Commit($"Import {_source}", writes, deletes);
            }
            catch (Exception e)
            {
                EditorUtility.DisplayDialog("Import failed", e.Message + "\n\nNothing was written.", "OK");
                return;
            }

            int added = rows.Count(r => r.Kind == DiffKind.Added), changed = rows.Count(r => r.Kind == DiffKind.Changed);
            var message = $"Imported from {_source}: {added} added, {changed} changed" + (deletes.Count > 0 ? $", {deletes.Count} deleted" : "") + ".";
            var callback = _onApplied;
            Close();
            callback?.Invoke(message);
        }
    }
}
