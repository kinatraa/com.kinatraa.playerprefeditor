using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

namespace kinatraa.PlayerPrefEditor
{
    public class PlayerPrefEditorWindow : EditorWindow
    {
        static readonly string[] TypeFilters = { "All", "int", "float", "string", "unknown" };
        static readonly List<string> TypeChoices = new List<string> { "int", "float", "string", "unknown" };

        List<PrefEntry> _all = new List<PrefEntry>();
        readonly List<PrefEntry> _visible = new List<PrefEntry>();
        string _typeFilter = "All";

        // Detail panel state: the entry being edited and the saved text/type that "unsaved changes" compares against.
        PrefEntry _editing;
        bool _isNew;
        string _savedJson;
        PrefType _savedType;

        ToolbarSearchField _search;
        ListView _list;
        Label _count, _status, _empty, _dirty, _error;
        VisualElement _detail;
        TextField _key, _value;
        DropdownField _type;
        Button _save, _revert, _copy, _delete;

        static bool Dark => EditorGUIUtility.isProSkin;
        static Color ErrorColor => Dark ? new Color(1f, 0.45f, 0.45f) : new Color(0.75f, 0.1f, 0.1f);
        static Color DirtyColor => Dark ? new Color(1f, 0.8f, 0.3f) : new Color(0.65f, 0.4f, 0f);

        bool IsDirty => _editing != null &&
                        (_isNew || PrefJson.ParseType(_type.value) != _savedType || _value.value != _savedJson);

        [MenuItem("Tools/kinatraa/Player Pref Editor")]
        public static void Open() => GetWindow<PlayerPrefEditorWindow>("Player Pref Editor");

        void CreateGUI()
        {
            titleContent = new GUIContent("Player Pref Editor");
            var root = rootVisualElement;

            var toolbar = new Toolbar();
            _search = new ToolbarSearchField();
            _search.RegisterValueChangedCallback(_ => ApplyFilter());
            toolbar.Add(_search);
            var filter = new ToolbarMenu { text = "Type: All" };
            foreach (var f in TypeFilters)
                filter.menu.AppendAction(f,
                    _ => { _typeFilter = f; filter.text = "Type: " + f; ApplyFilter(); },
                    _ => _typeFilter == f ? DropdownMenuAction.Status.Checked : DropdownMenuAction.Status.Normal);
            toolbar.Add(filter);
            toolbar.Add(new ToolbarSpacer { flex = true });
            toolbar.Add(new ToolbarButton(() => { Reload(); SetStatus("Refreshed."); }) { text = "Refresh" });
            toolbar.Add(new ToolbarButton(AddNew) { text = "Add New" });
            toolbar.Add(new ToolbarButton(Import) { text = "Import" });
            toolbar.Add(new ToolbarButton(Export) { text = "Export" });
            toolbar.Add(new ToolbarButton(CopyAll) { text = "Copy All" });
            root.Add(toolbar);

            var split = new TwoPaneSplitView(0, 260, TwoPaneSplitViewOrientation.Horizontal);
            split.style.flexGrow = 1;

            var left = new VisualElement();
            _count = new Label { style = { paddingLeft = 4, paddingTop = 3, paddingBottom = 3, unityFontStyleAndWeight = FontStyle.Bold } };
            left.Add(_count);
            _list = new ListView(_visible, 20, MakeRow, BindRow) { selectionType = SelectionType.Single };
            _list.style.flexGrow = 1;
#if UNITY_2022_2_OR_NEWER
            _list.selectionChanged += _ => OnListSelection();
#else
            _list.onSelectionChange += _ => OnListSelection();
#endif
            left.Add(_list);
            split.Add(left);
            split.Add(BuildDetail());
            root.Add(split);

            _status = new Label { style = { paddingLeft = 4, paddingTop = 2, paddingBottom = 2, whiteSpace = WhiteSpace.Normal } };
            root.Add(_status);

            Reload();
            UpdateDetail();
            SetStatus(PlayerPrefStore.NativeEnumeration
                ? "Keys are read from the registry."
                : "Showing keys written with this tool. Use Add New with an existing key name to track it.");
        }

        void OnFocus()
        {
            // Pick up changes made by play mode or scripts.
            if (_list != null) Reload();
        }

        VisualElement BuildDetail()
        {
            var right = new VisualElement { style = { flexGrow = 1, paddingLeft = 6, paddingRight = 6, paddingTop = 6, paddingBottom = 6 } };
            _empty = new Label("Select a key, or click Add New.") { style = { unityTextAlign = TextAnchor.MiddleCenter, flexGrow = 1 } };
            right.Add(_empty);

            _detail = new VisualElement { style = { flexGrow = 1 } };
            _key = new TextField("Key");
            _key.RegisterValueChangedCallback(_ => UpdateDetail());
            _detail.Add(_key);
            _type = new DropdownField("Type", TypeChoices, 0);
            _type.RegisterValueChangedCallback(_ => UpdateDetail());
            _detail.Add(_type);
            _detail.Add(new Label("Value (JSON)") { style = { marginTop = 4, marginLeft = 3 } });

            _value = new TextField { multiline = true };
            _value.style.flexGrow = 1;
#if UNITY_2022_1_OR_NEWER
            _value.verticalScrollerVisibility = ScrollerVisibility.Auto;
#endif
            var mono = new StyleFontDefinition(MonospaceFont());
            _value.Query<VisualElement>().ForEach(e =>
            {
                e.style.unityFontDefinition = mono;
                e.style.whiteSpace = WhiteSpace.Normal;
                e.style.unityTextAlign = TextAnchor.UpperLeft;
            });
            _value.RegisterValueChangedCallback(_ => UpdateDetail());
            _detail.Add(_value);

            _dirty = new Label("● unsaved changes") { style = { marginLeft = 3 } };
            _detail.Add(_dirty);
            _error = new Label { style = { marginLeft = 3, whiteSpace = WhiteSpace.Normal } };
            _detail.Add(_error);

            var buttons = new VisualElement { style = { flexDirection = FlexDirection.Row, marginTop = 4 } };
            buttons.Add(_save = new Button(Save) { text = "Save" });
            buttons.Add(_revert = new Button(Revert) { text = "Revert" });
            buttons.Add(_copy = new Button(CopyJson) { text = "Copy JSON" });
            buttons.Add(new VisualElement { style = { flexGrow = 1 } });
            buttons.Add(_delete = new Button(Delete) { text = "Delete" });
            _detail.Add(buttons);

            right.Add(_detail);
            return right;
        }

        static Font MonospaceFont()
        {
            var font = EditorGUIUtility.Load("Fonts/RobotoMono/RobotoMono-Regular.ttf") as Font;
            return font != null ? font : Font.CreateDynamicFontFromOSFont(new[] { "Menlo", "Consolas", "DejaVu Sans Mono", "Courier New" }, 12);
        }

        static VisualElement MakeRow()
        {
            var row = new VisualElement { style = { flexDirection = FlexDirection.Row, alignItems = Align.Center, paddingLeft = 4, paddingRight = 4 } };
            row.Add(new Label { name = "key", style = { flexGrow = 1, flexShrink = 1, overflow = Overflow.Hidden, textOverflow = TextOverflow.Ellipsis } });
            row.Add(new Label { name = "badge", style = { unityFontStyleAndWeight = FontStyle.Bold, marginLeft = 4 } });
            return row;
        }

        void BindRow(VisualElement row, int index)
        {
            var e = _visible[index];
            row.Q<Label>("key").text = e.Key;
            var badge = row.Q<Label>("badge");
            badge.text = $"[{PrefJson.TypeName(e.Type)}]";
            badge.style.color = BadgeColor(e.Type);
        }

        static Color BadgeColor(PrefType type)
        {
            switch (type)
            {
                case PrefType.Int: return Dark ? new Color(0.45f, 0.75f, 1f) : new Color(0.05f, 0.35f, 0.7f);
                case PrefType.Float: return Dark ? new Color(0.55f, 0.9f, 0.55f) : new Color(0.1f, 0.5f, 0.1f);
                case PrefType.String: return Dark ? new Color(0.95f, 0.75f, 0.45f) : new Color(0.6f, 0.35f, 0f);
                default: return Dark ? new Color(0.65f, 0.65f, 0.65f) : new Color(0.4f, 0.4f, 0.4f);
            }
        }

        // ---------- list ----------

        void Reload()
        {
            _all = PlayerPrefStore.GetKeys().Select(PlayerPrefStore.Read).ToList();
            // Refresh the open entry unless the user is in the middle of editing it.
            if (_editing != null && !_isNew && !IsDirty)
            {
                var current = _all.FirstOrDefault(e => e.Key == _editing.Key);
                if (current != null) Show(current, false);
                else ClearDetail();
            }
            ApplyFilter();
        }

        void ApplyFilter()
        {
            var query = _search.value?.Trim() ?? "";
            _visible.Clear();
            _visible.AddRange(_all.Where(e =>
                (_typeFilter == "All" || PrefJson.TypeName(e.Type) == _typeFilter) &&
                e.Key.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0));
            _count.text = $"Keys: {_visible.Count} / {_all.Count}";
            _list.Rebuild();
            SyncListSelection();
        }

        void SyncListSelection()
        {
            int i = _editing == null || _isNew ? -1 : _visible.FindIndex(e => e.Key == _editing.Key);
            _list.SetSelectionWithoutNotify(i >= 0 ? new[] { i } : Array.Empty<int>());
        }

        void OnListSelection()
        {
            int i = _list.selectedIndex;
            if (i < 0 || i >= _visible.Count) return;
            var picked = _visible[i];
            if (!_isNew && _editing != null && _editing.Key == picked.Key) return;
            if (!ConfirmDiscard())
            {
                SyncListSelection();
                return;
            }
            Show(picked, false);
        }

        // ---------- detail ----------

        void Show(PrefEntry entry, bool isNew)
        {
            _editing = entry;
            _isNew = isNew;
            _savedType = entry.Type;
            _savedJson = isNew || entry.Type == PrefType.Unknown ? "" : PrefJson.FormatValue(entry.Type, entry.Value);
            _key.SetValueWithoutNotify(entry.Key);
            _key.isReadOnly = !isNew;
            _type.SetValueWithoutNotify(PrefJson.TypeName(entry.Type));
            _value.SetValueWithoutNotify(isNew ? "\"\"" : _savedJson);
            UpdateDetail();
            SyncListSelection();
        }

        void ClearDetail()
        {
            _editing = null;
            _isNew = false;
            UpdateDetail();
        }

        void UpdateDetail()
        {
            bool has = _editing != null;
            _empty.style.display = has ? DisplayStyle.None : DisplayStyle.Flex;
            _detail.style.display = has ? DisplayStyle.Flex : DisplayStyle.None;
            if (!has) return;

            string error = null;
            if (_isNew && string.IsNullOrEmpty(_key.value)) error = "Enter a key name.";
            else if (!_isNew && _editing.Type == PrefType.Unknown && _type.value == "unknown")
                error = "The type of this key could not be detected. Pick a type and enter a value to overwrite it.";
            else PrefJson.TryParseValue(_value.value, PrefJson.ParseType(_type.value), out _, out error);

            bool dirty = IsDirty;
            _dirty.style.display = dirty ? DisplayStyle.Flex : DisplayStyle.None;
            _dirty.style.color = DirtyColor;
            _error.text = error ?? "";
            _error.style.display = error == null ? DisplayStyle.None : DisplayStyle.Flex;
            _error.style.color = ErrorColor;
            _save.SetEnabled(dirty && error == null);
            _revert.SetEnabled(dirty);
            _copy.SetEnabled(error == null);
            _delete.SetEnabled(!_isNew);
        }

        bool ConfirmDiscard()
        {
            if (!IsDirty) return true;
            var name = string.IsNullOrEmpty(_key.value) ? "the new key" : $"\"{_key.value}\"";
            return EditorUtility.DisplayDialog("Discard changes?", $"Discard unsaved changes to {name}?", "Discard", "Keep Editing");
        }

        void AddNew()
        {
            if (!ConfirmDiscard()) return;
            Show(new PrefEntry("", PrefType.String, ""), true);
            _key.Focus();
        }

        void Save()
        {
            var key = _isNew ? _key.value : _editing.Key;
            var type = PrefJson.ParseType(_type.value);
            if (!PrefJson.TryParseValue(_value.value, type, out var value, out var error))
            {
                SetStatus($"Not saved: {error}", true);
                return;
            }

            if (_isNew && PlayerPrefs.HasKey(key))
            {
                int choice = EditorUtility.DisplayDialogComplex("Key already exists",
                    $"\"{key}\" already exists in PlayerPrefs.\n\nLoad its current value, or overwrite it with the value you entered?",
                    "Load Existing", "Cancel", "Overwrite");
                if (choice == 1) return;
                if (choice == 0)
                {
                    PlayerPrefStore.Track(new[] { key });
                    _isNew = false;
                    _editing = null;
                    Reload();
                    Show(PlayerPrefStore.Read(key), false);
                    SetStatus($"Loaded existing key \"{key}\".");
                    return;
                }
            }

            try
            {
                PlayerPrefStore.Write(new PrefEntry(key, type, value));
            }
            catch (Exception e)
            {
                SetStatus($"Save failed: {e.Message}", true);
                return;
            }
            Show(new PrefEntry(key, type, value), false);
            Reload();
            SetStatus($"Saved \"{key}\" ({PrefJson.TypeName(type)}).");
        }

        void Revert()
        {
            if (_isNew) ClearDetail();
            else Show(_editing, false);
            SetStatus("Reverted.");
        }

        void Delete()
        {
            var key = _editing.Key;
            if (!EditorUtility.DisplayDialog("Delete PlayerPref", $"Delete \"{key}\"? This cannot be undone.", "Delete", "Cancel")) return;
            PlayerPrefStore.Delete(key);
            ClearDetail();
            Reload();
            SetStatus($"Deleted \"{key}\".");
        }

        void CopyJson()
        {
            var key = _isNew ? _key.value : _editing.Key;
            var type = PrefJson.ParseType(_type.value);
            if (!PrefJson.TryParseValue(_value.value, type, out var value, out var error))
            {
                SetStatus(error, true);
                return;
            }
            EditorGUIUtility.systemCopyBuffer = PrefJson.FormatDocument(new[] { new PrefEntry(key, type, value) });
            SetStatus($"Copied \"{key}\" as JSON.");
        }

        // ---------- import / export ----------

        void CopyAll()
        {
            EditorGUIUtility.systemCopyBuffer = PlayerPrefStore.ExportJson(out int exported, out int skipped);
            SetStatus($"Copied {exported} keys as JSON{SkippedNote(skipped)}.");
        }

        void Export()
        {
            var path = EditorUtility.SaveFilePanel("Export PlayerPrefs", "", "playerprefs.json", "json");
            if (string.IsNullOrEmpty(path)) return;
            try
            {
                File.WriteAllText(path, PlayerPrefStore.ExportJson(out int exported, out int skipped));
                SetStatus($"Exported {exported} keys to {path}{SkippedNote(skipped)}.");
            }
            catch (Exception e)
            {
                SetStatus($"Export failed: {e.Message}", true);
            }
        }

        void Import()
        {
            var path = EditorUtility.OpenFilePanel("Import PlayerPrefs", "", "json");
            if (string.IsNullOrEmpty(path)) return;

            string json;
            try
            {
                json = File.ReadAllText(path);
            }
            catch (Exception e)
            {
                SetStatus($"Import failed: {e.Message}", true);
                return;
            }
            if (!PrefJson.TryParseDocument(json, out var entries, out var error))
            {
                SetStatus($"Import failed, nothing was written. {error}", true);
                return;
            }

            var p = PlayerPrefStore.Preview(entries);
            var message = $"{Path.GetFileName(path)} has {entries.Count} keys:\n\n" +
                          $"Added: {p.Added}\nChanged: {p.Changed}\nUnchanged: {p.Unchanged}" +
                          (p.Changed > 0 ? $"\n\n{p.Changed} existing keys will be overwritten." : "");
            if (!EditorUtility.DisplayDialog("Import PlayerPrefs", message, "Import", "Cancel"))
            {
                SetStatus("Import cancelled.");
                return;
            }

            PlayerPrefStore.Apply(entries);
            Reload();
            SetStatus($"Imported {entries.Count} keys: {p.Added} added, {p.Changed} changed, {p.Unchanged} unchanged.");
        }

        static string SkippedNote(int skipped) => skipped > 0 ? $" ({skipped} keys of unknown type skipped)" : "";

        void SetStatus(string message, bool isError = false)
        {
            _status.text = message;
            _status.style.color = isError ? new StyleColor(ErrorColor) : new StyleColor(StyleKeyword.Null);
        }
    }
}
