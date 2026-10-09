using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace kinatraa.PlayerPrefEditor
{
    /// <summary>Shows what an import would change, key by key, and applies the checked rows as one undoable step.</summary>
    public sealed class ImportPreviewWindow : EditorWindow
    {
        static readonly List<string> Modes = new List<string>
        {
            "Merge: add and update keys",
            "Replace: also delete keys missing from the import",
        };

        string _source;
        List<PrefEntry> _incoming;
        Action<string> _onApplied;

        List<DiffRow> _rows = new List<DiffRow>();
        readonly List<DiffRow> _visible = new List<DiffRow>();
        readonly HashSet<string> _unchecked = new HashSet<string>(StringComparer.Ordinal);
        bool _replace, _showUnchanged;

        ListView _list;
        Label _summary;
        Button _apply;

        /// <param name="onApplied">Called with a status message after the import was applied.</param>
        public static void Open(string source, List<PrefEntry> incoming, Action<string> onApplied)
        {
            var w = CreateInstance<ImportPreviewWindow>();
            w.titleContent = new GUIContent("Import PlayerPrefs");
            w._source = source;
            w._incoming = incoming;
            w._onApplied = onApplied;
            w.minSize = new Vector2(480, 300);
            w.position = new Rect(w.position.x, w.position.y, 680, 440);
            w.ShowUtility();
        }

        void CreateGUI()
        {
            // The import data is not serialized, so a script reload closes the preview.
            if (_incoming == null)
            {
                EditorApplication.delayCall += Close;
                return;
            }

            var root = rootVisualElement;
            root.style.paddingLeft = root.style.paddingRight = root.style.paddingTop = root.style.paddingBottom = 6;

            root.Add(new Label($"Import from {_source}: {_incoming.Count} keys")
                { style = { unityFontStyleAndWeight = FontStyle.Bold, marginBottom = 4 } }.Ellipsis());

            var mode = new DropdownField(Modes, 0) { style = { flexGrow = 1, flexShrink = 1, minWidth = 0 } };
            mode.RegisterValueChangedCallback(e =>
            {
                _replace = e.newValue == Modes[1];
                Recompute();
            });
            var showUnchanged = new Toggle { text = "Show unchanged", style = { marginLeft = 8, flexShrink = 0 } };
            showUnchanged.RegisterValueChangedCallback(e =>
            {
                _showUnchanged = e.newValue;
                Refresh();
            });
            root.Add(PrefStyles.Row(mode, showUnchanged));

            _summary = new Label { style = { marginTop = 4, marginBottom = 4 } }.Ellipsis();
            root.Add(_summary);

            _list = new ListView(_visible, 20, MakeRow, BindRow) { selectionType = SelectionType.None };
            _list.style.flexGrow = 1;
            _list.style.borderTopWidth = _list.style.borderBottomWidth = 1;
            _list.style.borderTopColor = _list.style.borderBottomColor = PrefStyles.Dim;
            root.Add(_list);

            var buttons = PrefStyles.Row(
                new Button(() => SetAll(true)) { text = "Check All" },
                new Button(() => SetAll(false)) { text = "Check None" },
                PrefStyles.Spacer(),
                new Button(Close) { text = "Cancel" },
                _apply = new Button(Apply));
            buttons.style.marginTop = 6;
            buttons.style.flexWrap = Wrap.Wrap;
            root.Add(buttons);

            Recompute();
        }

        void Recompute()
        {
            _rows = PrefDiff.Compute(_incoming, PlayerPrefStore.ReadAll(), _replace);
            Refresh();
        }

        IEnumerable<DiffRow> Checked => _rows.Where(r => r.Kind != DiffKind.Unchanged && !_unchecked.Contains(r.Key));

        void Refresh()
        {
            _visible.Clear();
            _visible.AddRange(_rows.Where(r => _showUnchanged || r.Kind != DiffKind.Unchanged));
            _list.RefreshItems();

            int added = PrefDiff.Count(_rows, DiffKind.Added), changed = PrefDiff.Count(_rows, DiffKind.Changed);
            int unchanged = PrefDiff.Count(_rows, DiffKind.Unchanged), removed = PrefDiff.Count(_rows, DiffKind.Removed);
            _summary.text = $"{added} added · {changed} changed · {unchanged} unchanged" + (_replace ? $" · {removed} removed" : "");

            int count = Checked.Count();
            _apply.text = count == 0 ? "Nothing to Import" : $"Import {count} Change{(count == 1 ? "" : "s")}";
            _apply.SetEnabled(count > 0);
        }

        void SetAll(bool on)
        {
            _unchecked.Clear();
            if (!on) _unchecked.UnionWith(_rows.Select(r => r.Key));
            Refresh();
        }

        VisualElement MakeRow()
        {
            var toggle = new Toggle { style = { marginRight = 4, flexShrink = 0 } };
            toggle.RegisterValueChangedCallback(e =>
            {
                if (!(toggle.userData is string key)) return;
                if (e.newValue) _unchecked.Remove(key);
                else _unchecked.Add(key);
                Refresh();
            });
            var kind = new Label { name = "kind", style = { width = 64, flexShrink = 0, unityFontStyleAndWeight = FontStyle.Bold } };
            var key = new Label { name = "key", style = { flexBasis = new Length(40, LengthUnit.Percent), flexGrow = 1 } }.Ellipsis();
            var change = new Label { name = "change", style = { flexBasis = new Length(60, LengthUnit.Percent), flexGrow = 1, color = PrefStyles.Dim, marginLeft = 6 } }.Ellipsis();
            var row = PrefStyles.Row(toggle, kind, key, change);
            row.style.paddingLeft = row.style.paddingRight = 4;
            return row;
        }

        void BindRow(VisualElement row, int index)
        {
            var r = _visible[index];
            var toggle = row.Q<Toggle>();
            toggle.userData = r.Key;
            toggle.SetValueWithoutNotify(r.Kind != DiffKind.Unchanged && !_unchecked.Contains(r.Key));
            toggle.SetEnabled(r.Kind != DiffKind.Unchanged);

            var kind = row.Q<Label>("kind");
            kind.text = r.Kind.ToString().ToLowerInvariant();
            kind.style.color = PrefStyles.DiffColor(r.Kind);
            row.Q<Label>("key").text = r.Key;

            var change = row.Q<Label>("change");
            switch (r.Kind)
            {
                case DiffKind.Added: change.text = Describe(r.After); break;
                case DiffKind.Removed: change.text = Describe(r.Before); break;
                case DiffKind.Unchanged: change.text = Describe(r.After); break;
                default: change.text = $"{Describe(r.Before)}  →  {Describe(r.After)}"; break;
            }
            change.tooltip = change.text;
        }

        static string Describe(PrefEntry e) => $"{PrefJson.TypeName(e.Type)} {PrefFilter.Preview(e, 60)}";

        void Apply()
        {
            var rows = Checked.ToList();
            if (rows.Count == 0) return;
            var writes = rows.Where(r => r.Kind != DiffKind.Removed).Select(r => r.After).ToList();
            var deletes = rows.Where(r => r.Kind == DiffKind.Removed).Select(r => r.Key).ToList();
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
