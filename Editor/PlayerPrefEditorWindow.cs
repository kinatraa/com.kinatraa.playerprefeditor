using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditor.ShortcutManagement;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;
using Severity = kinatraa.PlayerPrefEditor.PrefStyles.Severity;

namespace kinatraa.PlayerPrefEditor
{
    public class PlayerPrefEditorWindow : EditorWindow, IHasCustomMenu
    {
        const string SettingsPrefix = "kinatraa.PlayerPrefEditor.Window.";
        const float LeftMin = 150, RightMin = 230;
        // Above this many characters, validation waits for a pause in typing and JSON strings open as plain text.
        const int LargeText = 100_000;
        static readonly List<string> TypeChoices = new List<string> { "int", "float", "string", "unknown" };

        // Survive script reloads through window serialization.
        [SerializeField] string _query = "";
        [SerializeField] List<string> _selection = new List<string>();

        // The open edit, kept in window serialization so unsaved changes survive script reloads (entering Play Mode reloads scripts).
        [Serializable]
        class Draft
        {
            public string BaseKey, BaseJson, Key, Type, Text;
            public bool IsNew, Renaming, Embedded, EmbeddedIndented;
        }

        [SerializeField] Draft _draft = new Draft();
        [SerializeField] bool _hasDraft;

        readonly PrefFilter _filter = new PrefFilter();
        bool _autoRefresh, _preferEmbedded;
        float _leftPreferred = 320;

        List<PrefEntry> _all = new List<PrefEntry>();
        readonly List<PrefEntry> _visible = new List<PrefEntry>();
        HashSet<string> _pinned = new HashSet<string>();
        bool _committing, _wideList = true;
        Task<NativePrefs.Snapshot> _pendingRead;

        // Detail editor. _editing is the stored entry the editor was opened with; the "unsaved" check compares against it.
        PrefEntry _editing;
        bool _isNew, _renaming, _embedded, _embeddedIndented;
        string _savedPlain, _savedEmbedded;
        bool _externalChange;
        string _typeNote;
        IVisualElementScheduledItem _pendingValidation;

        ToolbarSearchField _search;
        ToolbarMenu _filterMenu;
        ToolbarButton _undo, _redo;
        VisualElement _body, _left;
        ListView _list;
        Label _count;
        PrefBanner _searchError;
        VisualElement _listEmpty, _listEmptyActions;
        Label _listEmptyTitle, _listEmptyText;

        VisualElement _emptyPane, _multiPane, _detail;
        Label _multiTitle, _multiText;
        Label _keyTitle, _keyLabel, _info, _dirty;
        TextField _key, _value;
        Button _menuButton, _format, _save, _revert;
        DropdownField _type;
        Toggle _embedToggle;
        PrefBanner _problem, _notice;

        VisualElement _statusIcon;
        Label _status, _source;

        bool IsDirty =>
            _editing != null &&
            (_isNew || _renaming && _key.value != _editing.Key ||
             PrefJson.ParseType(_type.value) != _editing.Type ||
             _value.value != (_embedded ? _savedEmbedded : _savedPlain));

        PrefType EditorType => PrefJson.ParseType(_type.value);

        [MenuItem("Tools/kinatraa/Player Pref Editor")]
        public static void Open() => GetWindow<PlayerPrefEditorWindow>("Player Pref Editor");

        // ---------- lifecycle ----------

        void OnEnable()
        {
            PrefHistory.Changed += OnHistoryChanged;
            EditorApplication.playModeStateChanged += OnPlayModeChanged;
        }

        void OnDisable()
        {
            PrefHistory.Changed -= OnHistoryChanged;
            EditorApplication.playModeStateChanged -= OnPlayModeChanged;
        }

        void OnFocus()
        {
            // Pick up changes made by play mode, scripts or another tool.
            if (_list != null) Reload();
        }

        void OnHistoryChanged()
        {
            if (!_committing && _list != null) Reload();
        }

        void OnPlayModeChanged(PlayModeStateChange change)
        {
            if (_list != null && (change == PlayModeStateChange.EnteredPlayMode || change == PlayModeStateChange.EnteredEditMode)) Reload();
        }

        /// <summary>Items in the window's ⋮ tab menu: settings and rarely used actions.</summary>
        public void AddItemsToMenu(GenericMenu menu)
        {
            menu.AddItem(new GUIContent("Auto Refresh in Play Mode"), _autoRefresh, () =>
            {
                _autoRefresh = !_autoRefresh;
                SaveSettings();
            });
            menu.AddItem(new GUIContent("Show Unity Internal Keys"), _filter.ShowInternal, () =>
            {
                _filter.ShowInternal = !_filter.ShowInternal;
                SaveSettings();
                ApplyFilter();
            });
            menu.AddSeparator("");
            if (ExportScope().Count > 0) menu.AddItem(new GUIContent("Delete All Keys…"), false, DeleteAll);
            else menu.AddDisabledItem(new GUIContent("Delete All Keys…"));
            if (PrefHistory.instance.CanUndo || PrefHistory.instance.CanRedo)
                menu.AddItem(new GUIContent("Clear Undo History"), false, () => PrefHistory.instance.Clear());
            else menu.AddDisabledItem(new GUIContent("Clear Undo History"));
            menu.AddSeparator("");
            menu.AddItem(new GUIContent("Show Storage Location"), false, ShowStorageLocation);
            menu.AddItem(new GUIContent("Documentation"), false, () => Application.OpenURL("https://github.com/kinatraa/com.kinatraa.playerprefeditor#readme"));
        }

        void CreateGUI()
        {
            titleContent = new GUIContent("Player Pref Editor", PrefStyles.FindIcon("SaveAs"));
            minSize = new Vector2(LeftMin + RightMin + 80, 280);
            LoadSettings();

            var root = rootVisualElement;
            PrefStyles.ApplyTheme(root);
            root.Add(BuildToolbar());
            root.Add(BuildBody());
            root.Add(BuildStatusBar());

            root.RegisterCallback<DragUpdatedEvent>(OnDragUpdated);
            root.RegisterCallback<DragPerformEvent>(OnDragPerform);
            root.schedule.Execute(AutoRefresh).Every(500);

            Reload();
            var restore = _selection.ToList();
            if (RestoreDraft()) return;
            if (restore.Count == 1 && _all.Find(e => e.Key == restore[0]) is PrefEntry entry) Show(entry, false);
            else UpdatePanes();
        }

        /// <summary>Play Mode refresh. The OS store is read on a worker thread so the game never stalls; values are read when it lands.</summary>
        void AutoRefresh()
        {
            if (!_autoRefresh || !EditorApplication.isPlaying) return;
            if (_pendingRead == null)
            {
                _pendingRead = NativePrefs.ReadKeysAsync();
                return;
            }
            if (!_pendingRead.IsCompleted) return;
            var snapshot = _pendingRead.Status == TaskStatus.RanToCompletion
                ? _pendingRead.Result
                : new NativePrefs.Snapshot(null, _pendingRead.Exception?.GetBaseException().Message);
            _pendingRead = null;
            Reload(snapshot);
        }

        /// <summary>List, draggable divider, editor. The list keeps the width the user dragged it to and only shrinks while the window is too narrow.</summary>
        VisualElement BuildBody()
        {
            _body = PrefStyles.Box("ppe-body");
            _body.Add(_left = BuildList());

            var divider = PrefStyles.Box("ppe-divider");
            var grip = PrefStyles.Box("ppe-divider-grip");
            float startX = 0, startWidth = 0;
            grip.RegisterCallback<PointerDownEvent>(e =>
            {
                if (e.button != 0) return;
                startX = e.position.x;
                startWidth = _left.resolvedStyle.width;
                grip.CapturePointer(e.pointerId);
                e.StopPropagation();
            });
            grip.RegisterCallback<PointerMoveEvent>(e =>
            {
                if (!grip.HasPointerCapture(e.pointerId)) return;
                _leftPreferred = ClampListWidth(startWidth + e.position.x - startX);
                ApplyListWidth();
            });
            grip.RegisterCallback<PointerUpEvent>(e =>
            {
                if (!grip.HasPointerCapture(e.pointerId)) return;
                grip.ReleasePointer(e.pointerId);
                SaveSettings();
            });
            divider.Add(grip);
            _body.Add(divider);

            _body.Add(BuildRight());
            _body.RegisterCallback<GeometryChangedEvent>(_ => ApplyListWidth());
            return _body;
        }

        float ClampListWidth(float width)
        {
            float total = _body.resolvedStyle.width;
            float max = float.IsNaN(total) ? width : Mathf.Max(LeftMin, total - RightMin - 1);
            return Mathf.Clamp(width, LeftMin, max);
        }

        void ApplyListWidth()
        {
            float width = ClampListWidth(_leftPreferred);
            if (Mathf.Abs(_left.resolvedStyle.width - width) > 0.5f) _left.style.width = width;
        }

        void LoadSettings()
        {
            int type = EditorPrefs.GetInt(SettingsPrefix + "Type", -1);
            _filter.Type = type < 0 ? (PrefType?)null : (PrefType)type;
            _filter.SearchValues = EditorPrefs.GetBool(SettingsPrefix + "SearchValues", true);
            _filter.UseRegex = EditorPrefs.GetBool(SettingsPrefix + "Regex", false);
            _filter.ShowInternal = EditorPrefs.GetBool(SettingsPrefix + "ShowInternal", false);
            _filter.Sort = (SortMode)EditorPrefs.GetInt(SettingsPrefix + "Sort", 0);
            _filter.Query = _query;
            _autoRefresh = EditorPrefs.GetBool(SettingsPrefix + "AutoRefresh", true);
            _preferEmbedded = EditorPrefs.GetBool(SettingsPrefix + "PreferEmbedded", true);
            _leftPreferred = EditorPrefs.GetFloat(SettingsPrefix + "ListWidth", 320);
        }

        void SaveSettings()
        {
            EditorPrefs.SetInt(SettingsPrefix + "Type", _filter.Type == null ? -1 : (int)_filter.Type.Value);
            EditorPrefs.SetBool(SettingsPrefix + "SearchValues", _filter.SearchValues);
            EditorPrefs.SetBool(SettingsPrefix + "Regex", _filter.UseRegex);
            EditorPrefs.SetBool(SettingsPrefix + "ShowInternal", _filter.ShowInternal);
            EditorPrefs.SetInt(SettingsPrefix + "Sort", (int)_filter.Sort);
            EditorPrefs.SetBool(SettingsPrefix + "AutoRefresh", _autoRefresh);
            EditorPrefs.SetBool(SettingsPrefix + "PreferEmbedded", _preferEmbedded);
            EditorPrefs.SetFloat(SettingsPrefix + "ListWidth", _leftPreferred);
        }

        // ---------- toolbar ----------

        VisualElement BuildToolbar()
        {
            var toolbar = new Toolbar().Classes("ppe-toolbar");
            toolbar.Add(PrefStyles.IconButton("Toolbar Plus", "New", "New key (Ctrl/Cmd+N)", AddNew));
            toolbar.Add(PrefStyles.IconButton("Refresh", "Refresh", "Reload from PlayerPrefs (F5)", () =>
            {
                Reload();
                SetStatus("Refreshed.");
            }));

            _search = new ToolbarSearchField { tooltip = "Search keys and values (Ctrl/Cmd+F)" }.Classes("ppe-search");
            _search.SetValueWithoutNotify(_query);
            _search.RegisterValueChangedCallback(e =>
            {
                _query = _filter.Query = e.newValue;
                ApplyFilter();
            });
            toolbar.Add(_search);

            _filterMenu = new ToolbarMenu { tooltip = "Filter by type, search options and sort order" };
            var menu = _filterMenu.menu;
            AddTypeFilter(menu, "All Types", null);
            foreach (var t in new[] { PrefType.Int, PrefType.Float, PrefType.String, PrefType.Unknown })
                AddTypeFilter(menu, PrefJson.TypeName(t), t);
            menu.AppendSeparator();
            AddToggle(menu, "Search Values Too", () => _filter.SearchValues, v => _filter.SearchValues = v);
            AddToggle(menu, "Use Regex", () => _filter.UseRegex, v => _filter.UseRegex = v);
            AddToggle(menu, "Show Unity Internal Keys", () => _filter.ShowInternal, v => _filter.ShowInternal = v);
            menu.AppendSeparator();
            AddSort(menu, "Sort/Name A → Z", SortMode.NameAscending);
            AddSort(menu, "Sort/Name Z → A", SortMode.NameDescending);
            AddSort(menu, "Sort/Type", SortMode.Type);
            toolbar.Add(_filterMenu);

            toolbar.Add(new ToolbarSpacer { flex = true });
            toolbar.Add(_undo = new ToolbarButton(Undo) { text = "Undo" });
            toolbar.Add(_redo = new ToolbarButton(Redo) { text = "Redo" });

            var import = new ToolbarMenu { text = "Import", tooltip = "Import a JSON document. You can also drop a .json file on the window." };
            import.menu.AppendAction("From File…", _ => ImportFromFile());
            import.menu.AppendAction("From Clipboard", _ => ImportText(EditorGUIUtility.systemCopyBuffer, "the clipboard"));
            toolbar.Add(import);

            var export = new ToolbarMenu { text = "Export", tooltip = "Save or copy keys as JSON" };
            export.menu.AppendAction("All Keys to File…", _ => ExportToFile(ExportScope(), "playerprefs.json"), _ => Status(ExportScope().Count > 0));
            export.menu.AppendAction("Visible Keys to File…", _ => ExportToFile(_visible, "playerprefs-filtered.json"), _ => Status(_visible.Count > 0));
            export.menu.AppendAction("Selected Keys to File…", _ => ExportToFile(SelectedEntries(), "playerprefs-selection.json"), _ => Status(_selection.Count > 0));
            export.menu.AppendSeparator();
            export.menu.AppendAction("Copy All as JSON", _ => CopyEntries(ExportScope(), "all keys"), _ => Status(ExportScope().Count > 0));
            export.menu.AppendAction("Copy Selected as JSON", _ => CopyEntries(SelectedEntries(), "the selection"), _ => Status(_selection.Count > 0));
            toolbar.Add(export);
            return toolbar;
        }

        static DropdownMenuAction.Status Status(bool enabled) => enabled ? DropdownMenuAction.Status.Normal : DropdownMenuAction.Status.Disabled;

        static DropdownMenuAction.Status Check(bool on) => on ? DropdownMenuAction.Status.Checked : DropdownMenuAction.Status.Normal;

        void AddTypeFilter(DropdownMenu menu, string name, PrefType? type)
        {
            menu.AppendAction(name, _ =>
            {
                _filter.Type = type;
                SaveSettings();
                ApplyFilter();
            }, _ => Check(_filter.Type == type));
        }

        void AddToggle(DropdownMenu menu, string name, Func<bool> get, Action<bool> set)
        {
            menu.AppendAction(name, _ =>
            {
                set(!get());
                SaveSettings();
                ApplyFilter();
            }, _ => Check(get()));
        }

        void AddSort(DropdownMenu menu, string name, SortMode mode)
        {
            menu.AppendAction(name, _ =>
            {
                _filter.Sort = mode;
                SaveSettings();
                ApplyFilter();
            }, _ => Check(_filter.Sort == mode));
        }

        // ---------- list ----------

        VisualElement BuildList()
        {
            var left = PrefStyles.Box("ppe-list-pane");
            _count = PrefStyles.Text("", "ppe-list-count").Ellipsis();
            _count.AddToClassList("ppe-grow");
            left.Add(PrefStyles.Row(_count).Classes("ppe-list-header"));
            _searchError = new PrefBanner();
            _searchError.style.marginLeft = _searchError.style.marginRight = 4;
            left.Add(_searchError);

            _list = new ListView(_visible, 22, MakeRow, BindRow)
            {
                selectionType = SelectionType.Multiple,
                showAlternatingRowBackgrounds = AlternatingRowBackground.ContentOnly,
            }.Classes("ppe-list");
#if UNITY_2022_2_OR_NEWER
            _list.selectionChanged += _ => OnListSelection();
#else
            _list.onSelectionChange += _ => OnListSelection();
#endif
            _list.RegisterCallback<KeyDownEvent>(OnListKeyDown);
            _list.AddManipulator(new ContextualMenuManipulator(BuildContextMenu));
            left.Add(_list);

            _listEmpty = PrefStyles.Box("ppe-empty");
            _listEmpty.Add(_listEmptyTitle = PrefStyles.Text("", "ppe-empty-title"));
            _listEmpty.Add(_listEmptyText = PrefStyles.Text("", "ppe-empty-text", "ppe-dim"));
            _listEmpty.Add(_listEmptyActions = PrefStyles.Box("ppe-empty-actions"));
            left.Add(_listEmpty);

            // Show the value preview column only when the list is wide enough to read it.
            left.RegisterCallback<GeometryChangedEvent>(e =>
            {
                bool wide = e.newRect.width >= 250;
                if (wide == _wideList) return;
                _wideList = wide;
                _list.RefreshItems();
            });
            return left;
        }

        VisualElement MakeRow()
        {
            var row = PrefStyles.Box("ppe-key-row");
            row.Add(PrefStyles.Icon("Favorite", "ppe-pin").Classes("pin"));
            row.Add(PrefStyles.Text("", "ppe-key-name").Ellipsis());
            row.Add(PrefStyles.Text("", "ppe-key-preview").Ellipsis().Mono());
            row.Add(PrefStyles.TypePill(PrefType.Unknown));
            // Right-clicking a row that is not selected selects it, so the context menu acts on it.
            row.RegisterCallback<PointerDownEvent>(e =>
            {
                if (e.button == 1 && row.userData is int index && !_list.selectedIndices.Contains(index))
                    _list.SetSelection(index);
            });
            return row;
        }

        void BindRow(VisualElement row, int index)
        {
            var e = _visible[index];
            row.userData = index;
            bool pinned = _pinned.Contains(e.Key);
            var pin = row.Q(className: "pin");
            pin.style.display = pinned ? DisplayStyle.Flex : DisplayStyle.None;
            pin.tooltip = pinned ? "Pinned" : "";
            var key = row.Q<Label>(className: "ppe-key-name");
            key.text = e.Key;
            key.tooltip = e.Key;
            var preview = row.Q<Label>(className: "ppe-key-preview");
            preview.text = e.Type == PrefType.Unknown ? "unreadable" : PrefFilter.Preview(e);
            preview.style.display = _wideList ? DisplayStyle.Flex : DisplayStyle.None;
            PrefStyles.SetType(row.Q<Label>(className: "ppe-pill"), e.Type);
        }

        void Reload() => Reload(null);

        /// <summary>Re-reads every key. <paramref name="native"/> is an OS store snapshot already read off the main thread, or null to read it now.</summary>
        void Reload(NativePrefs.Snapshot native)
        {
            _all = native != null ? PlayerPrefStore.ReadAll(native) : PlayerPrefStore.ReadAll();
            _pinned = PlayerPrefStore.GetPinned();
            _selection.RemoveAll(k => _all.All(e => e.Key != k));

            if (_editing != null && !_isNew)
            {
                var current = _all.Find(e => e.Key == _editing.Key);
                if (!IsDirty && !_renaming)
                {
                    // Only reopen when the stored value moved, so a refresh never disturbs the field being edited.
                    if (current == null) ClearEditor();
                    else if (!Same(current, _editing)) Show(current, false);
                }
                else _externalChange = !Same(current, _editing);
            }

            UpdateSource();
            ApplyFilter();
            UpdateUndoButtons();
            UpdatePanes();
        }

        static bool Same(PrefEntry a, PrefEntry b) => PrefDiff.Same(a, b);

        void UpdateSource()
        {
            var error = PlayerPrefStore.LastNativeError;
            string where;
            switch (Application.platform)
            {
                case RuntimePlatform.WindowsEditor: where = "Registry"; break;
                case RuntimePlatform.OSXEditor: where = "macOS plist"; break;
                case RuntimePlatform.LinuxEditor: where = "Linux prefs"; break;
                default: where = "Tracked keys"; break;
            }
            _source.text = error == null ? where : "Tracked keys only";
            _source.tooltip = error == null
                ? "Keys are read from " + NativePrefs.Location
                : "The OS store could not be read, so only keys written with this tool are listed.\n" + error;
            _source.EnableInClassList("ppe-text--warning", error != null);
            _source.EnableInClassList("ppe-dim", error == null);
        }

        void ApplyFilter()
        {
            _visible.Clear();
            _visible.AddRange(_filter.Apply(_all, _pinned, out var searchError));
            _searchError.Set(searchError, Severity.Error);

            int total = _filter.ShowInternal ? _all.Count : _all.Count(e => !PlayerPrefStore.IsInternalKey(e.Key));
            int hidden = _all.Count - total;
            _count.text = (_visible.Count == total ? $"{total} {Plural(total, "key")}" : $"{_visible.Count} of {total} keys")
                          + (_selection.Count > 1 ? $" · {_selection.Count} selected" : "");
            _count.tooltip = hidden > 0 ? $"{hidden} Unity internal {Plural(hidden, "key")} hidden. Show them from the filter menu." : "";
            _filterMenu.text = _filter.Type == null ? "All Types" : PrefJson.TypeName(_filter.Type.Value);
            _list.RefreshItems();
            SyncListSelection();
            UpdateListEmptyState(total);
        }

        void UpdateListEmptyState(int total)
        {
            bool empty = _visible.Count == 0;
            _list.style.display = empty ? DisplayStyle.None : DisplayStyle.Flex;
            _listEmpty.style.display = empty ? DisplayStyle.Flex : DisplayStyle.None;
            if (!empty) return;

            _listEmptyActions.Clear();
            if (total == 0)
            {
                _listEmptyTitle.text = "No PlayerPrefs yet";
                _listEmptyText.text = "Keys your game saves show up here. Create one, or import a JSON file.";
                _listEmptyActions.Add(new Button(AddNew) { text = "New Key" });
                _listEmptyActions.Add(new Button(ImportFromFile) { text = "Import…" });
                return;
            }
            _listEmptyTitle.text = "No matching keys";
            var query = (_filter.Query ?? "").Trim();
            _listEmptyText.text = query.Length > 0 ? $"Nothing matches \"{query}\"." : $"No {PrefJson.TypeName(_filter.Type ?? PrefType.Unknown)} keys.";
            if (query.Length > 0) _listEmptyActions.Add(new Button(() => _search.value = "") { text = "Clear Search" });
            if (_filter.Type != null)
                _listEmptyActions.Add(new Button(() =>
                {
                    _filter.Type = null;
                    SaveSettings();
                    ApplyFilter();
                }) { text = "Show All Types" });
        }

        static string Plural(int n, string word) => n == 1 ? word : word + "s";

        void SyncListSelection()
        {
            var indices = _selection.Select(k => _visible.FindIndex(e => e.Key == k)).Where(i => i >= 0).ToList();
            _list.SetSelectionWithoutNotify(indices);
        }

        void OnListSelection()
        {
            var keys = _list.selectedIndices.Where(i => i >= 0 && i < _visible.Count).Select(i => _visible[i].Key).ToList();
            if (keys.SequenceEqual(_selection)) return;

            bool keepsEditor = keys.Count == 1 && _editing != null && !_isNew && _editing.Key == keys[0];
            if (!keepsEditor && !ConfirmDiscard())
            {
                SyncListSelection();
                return;
            }

            _selection = keys;
            if (keys.Count == 1)
            {
                if (!keepsEditor) Show(_all.Find(e => e.Key == keys[0]), false);
            }
            else ClearEditor();
            ApplyFilter();
            UpdatePanes();
        }

        void OnListKeyDown(KeyDownEvent e)
        {
            bool delete = e.keyCode == KeyCode.Delete || e.keyCode == KeyCode.Backspace && e.actionKey;
            if (delete && _selection.Count > 0)
            {
                DeleteKeys(_selection.ToList());
                e.StopPropagation();
            }
            else if (e.keyCode == KeyCode.C && e.actionKey && _selection.Count > 0)
            {
                CopyEntries(SelectedEntries(), _selection.Count == 1 ? $"\"{_selection[0]}\"" : "the selection");
                e.StopPropagation();
            }
            else if ((e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter) && _editing != null)
            {
                // Enter jumps from the list into the value, like renaming in the Project window.
                _value.Focus();
                e.StopPropagation();
            }
        }

        void BuildContextMenu(ContextualMenuPopulateEvent evt)
        {
            var menu = evt.menu;
            var keys = _selection.ToList();
            if (keys.Count == 0)
            {
                menu.AppendAction("New Key", _ => AddNew());
                menu.AppendAction("Import…", _ => ImportFromFile());
                return;
            }
            if (keys.Count == 1)
            {
                foreach (var (name, action) in KeyActions(keys[0]))
                {
                    if (name == null) menu.AppendSeparator();
                    else menu.AppendAction(name, _ => action());
                }
                return;
            }
            menu.AppendAction($"Copy {keys.Count} Keys as JSON", _ => CopyEntries(SelectedEntries(), "the selection"));
            menu.AppendAction($"Export {keys.Count} Keys…", _ => ExportToFile(SelectedEntries(), "playerprefs-selection.json"));
            menu.AppendSeparator();
            menu.AppendAction("Pin to Top", _ => SetPins(keys, true));
            menu.AppendAction("Unpin", _ => SetPins(keys, false));
            menu.AppendSeparator();
            menu.AppendAction($"Delete {keys.Count} Keys…", _ => DeleteKeys(keys));
        }

        /// <summary>Actions on one key, shared by the list's context menu and the editor's ⋮ menu. A null name is a separator.</summary>
        List<(string name, Action action)> KeyActions(string key) => new List<(string, Action)>
        {
            ("Rename", StartRename),
            ("Duplicate", Duplicate),
            (_pinned.Contains(key) ? "Unpin" : "Pin to Top", () => TogglePin(key)),
            (null, null),
            ("Copy Key Name", () =>
            {
                EditorGUIUtility.systemCopyBuffer = key;
                SetStatus($"Copied the key name \"{key}\".");
            }),
            ("Copy as JSON", () => CopyEntries(_all.Where(e => e.Key == key).ToList(), $"\"{key}\"")),
            (null, null),
            ("Delete…", () => DeleteKeys(new List<string> { key })),
        };

        List<PrefEntry> SelectedEntries() => _all.Where(e => _selection.Contains(e.Key)).ToList();

        /// <summary>What "all" means for Export, Copy All and Delete All: every key, minus Unity's own unless they are shown.</summary>
        List<PrefEntry> ExportScope() => _all.Where(e => _filter.ShowInternal || !PlayerPrefStore.IsInternalKey(e.Key)).ToList();

        // ---------- right side ----------

        VisualElement BuildRight()
        {
            var right = PrefStyles.Box("ppe-detail-pane");

            _emptyPane = PrefStyles.Box("ppe-empty");
            _emptyPane.Add(PrefStyles.Text("No key selected", "ppe-empty-title"));
            _emptyPane.Add(PrefStyles.Text("Select a key to see and edit its value, or drop a .json file here to import it.", "ppe-empty-text", "ppe-dim"));
            var emptyActions = PrefStyles.Box("ppe-empty-actions");
            emptyActions.Add(new Button(AddNew) { text = "New Key" });
            _emptyPane.Add(emptyActions);
            right.Add(_emptyPane);

            _multiPane = PrefStyles.Box("ppe-empty");
            _multiPane.Add(_multiTitle = PrefStyles.Text("", "ppe-empty-title"));
            _multiPane.Add(_multiText = PrefStyles.Text("", "ppe-empty-text", "ppe-dim"));
            var multiActions = PrefStyles.Box("ppe-empty-actions");
            multiActions.Add(new Button(() => CopyEntries(SelectedEntries(), "the selection")) { text = "Copy as JSON" });
            multiActions.Add(new Button(() => ExportToFile(SelectedEntries(), "playerprefs-selection.json")) { text = "Export…" });
            multiActions.Add(new Button(() => DeleteKeys(_selection.ToList())) { text = "Delete…" });
            _multiPane.Add(multiActions);
            right.Add(_multiPane);

            right.Add(_detail = BuildDetail());
            return right;
        }

        VisualElement BuildDetail()
        {
            var detail = PrefStyles.Box("ppe-detail");

            // Header: the key as a title. It turns into a text field for a new key or a rename.
            _keyTitle = PrefStyles.Text("", "ppe-key-title").Ellipsis();
            _keyTitle.RegisterCallback<MouseDownEvent>(e =>
            {
                if (e.clickCount == 2) StartRename();
            });
            _key = new TextField().Classes("ppe-key-field");
            _key.RegisterValueChangedCallback(_ => UpdateDetail());
            _key.RegisterCallback<KeyDownEvent>(e =>
            {
                if (e.keyCode == KeyCode.Escape && _renaming) CancelRename();
                else if (e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter) _value.Focus();
            }, TrickleDown.TrickleDown);
            _menuButton = PrefStyles.SmallIconButton("_Menu", "Rename, duplicate, pin, copy or delete this key", () =>
            {
                var menu = new GenericMenu();
                foreach (var (name, action) in KeyActions(_editing.Key))
                {
                    if (name == null) menu.AddSeparator("");
                    else menu.AddItem(new GUIContent(name), false, () => action());
                }
                menu.DropDown(_menuButton.worldBound);
            });
            _keyLabel = PrefStyles.Text("Key", "ppe-field-label");
            detail.Add(PrefStyles.Row(_keyTitle, _keyLabel, _key, _menuButton).Classes("ppe-detail-header"));

            _type = new DropdownField(TypeChoices, 0).Classes("ppe-type-field");
            _type.RegisterValueChangedCallback(OnTypeChanged);
            _info = PrefStyles.Text("", "ppe-info", "ppe-dim").Ellipsis();
            detail.Add(PrefStyles.Row(PrefStyles.Text("Type", "ppe-field-label"), _type, _info).Classes("ppe-field-row"));

            _embedToggle = new Toggle { text = "Edit as JSON", tooltip = "This string holds JSON. Edit it formatted; it is saved back as a string, in its original layout (compact or indented)." };
            _embedToggle.RegisterValueChangedCallback(e => SetEmbedded(e.newValue));
            _format = new Button(FormatValue) { text = "Format", tooltip = "Pretty-print the JSON" }.Classes("ppe-mini-button");
            detail.Add(PrefStyles.Row(PrefStyles.Text("Value", "ppe-value-label"), PrefStyles.Text("(JSON)", "ppe-dim"), PrefStyles.Spacer(), _embedToggle, _format)
                .Classes("ppe-value-header"));

            _value = new TextField { multiline = true }.Classes("ppe-value").Mono();
#if UNITY_2022_1_OR_NEWER
            _value.verticalScrollerVisibility = ScrollerVisibility.Auto;
#endif
            _value.RegisterValueChangedCallback(e =>
            {
                _typeNote = null;
                if ((e.newValue?.Length ?? 0) < LargeText)
                {
                    UpdateDetail();
                    return;
                }
                // Parsing megabytes on every keystroke would stall typing; validate once the user pauses.
                _pendingValidation?.Pause();
                _pendingValidation = _value.schedule.Execute(UpdateDetail).StartingIn(250);
            });
            detail.Add(_value);

            detail.Add(_problem = new PrefBanner());
            detail.Add(_notice = new PrefBanner());

            _dirty = PrefStyles.Text("", "ppe-dirty").Ellipsis();
            _dirty.AddToClassList("ppe-grow");
            _revert = new Button(Revert) { text = "Revert" };
            _save = new Button(Save) { text = "Save", tooltip = "Save (Ctrl/Cmd+S)" }.Classes("ppe-primary");
            detail.Add(PrefStyles.Row(_dirty, _revert, _save).Classes("ppe-footer"));
            return detail;
        }

        void UpdatePanes()
        {
            bool editing = _editing != null;
            bool multi = !editing && _selection.Count > 1;
            _detail.style.display = editing ? DisplayStyle.Flex : DisplayStyle.None;
            _multiPane.style.display = multi ? DisplayStyle.Flex : DisplayStyle.None;
            // With no keys at all the list already offers New Key and Import, so the right side stays quiet.
            _emptyPane.style.display = !editing && !multi && _all.Count > 0 ? DisplayStyle.Flex : DisplayStyle.None;
            if (multi)
            {
                var selected = SelectedEntries();
                _multiTitle.text = $"{_selection.Count} keys selected";
                _multiText.text = string.Join(" · ", selected.GroupBy(e => e.Type).OrderBy(g => g.Key).Select(g => $"{g.Count()} {PrefJson.TypeName(g.Key)}"));
            }
            if (editing) UpdateDetail();
        }

        /// <summary>Records <paramref name="entry"/> as the stored state the editor compares against.</summary>
        void SetBaseline(PrefEntry entry, bool isNew)
        {
            _editing = entry;
            _isNew = isNew;
            _externalChange = false;
            _savedPlain = entry.Type == PrefType.Unknown || entry.Value == null ? "" : PrefJson.FormatValue(entry.Type, entry.Value);
            _savedEmbedded = null;
            // Large strings get their formatted baseline only when Edit as JSON is switched on (see SetEmbedded).
            if (entry.Type == PrefType.String && ((string)entry.Value).Length < LargeText) ComputeSavedEmbedded();
            _draft.BaseKey = entry.Key;
            _draft.BaseJson = isNew || entry.Type == PrefType.Unknown ? "" : PrefJson.FormatDocument(new[] { entry });
            _draft.IsNew = isNew;
        }

        void ComputeSavedEmbedded()
        {
            if (_editing.Value is string stored && PrefJson.TryExpandEmbedded(stored, out var pretty))
            {
                _savedEmbedded = pretty;
                _embeddedIndented = stored.IndexOf('\n') >= 0;
            }
        }

        /// <summary>Opens an entry in the editor. For a new draft, <paramref name="entry"/> holds the initial key, type and value.</summary>
        void Show(PrefEntry entry, bool isNew)
        {
            SetBaseline(entry, isNew);
            _renaming = false;
            _typeNote = null;
            // Pretty-printing a huge string doubles its size and makes the field sluggish, so large ones open as plain text.
            _embedded = _savedEmbedded != null && _preferEmbedded && !isNew;

            _key.SetValueWithoutNotify(entry.Key);
            _type.SetValueWithoutNotify(PrefJson.TypeName(entry.Type));
            _value.SetValueWithoutNotify(_embedded ? _savedEmbedded : _savedPlain);
            _embedToggle.SetValueWithoutNotify(_embedded);
            if (!isNew)
            {
                _selection = new List<string> { entry.Key };
                SyncListSelection();
                int index = _visible.FindIndex(e => e.Key == entry.Key);
                if (index >= 0) _list.ScrollToItem(index);
            }
            UpdatePanes();
        }

        void ClearEditor()
        {
            _editing = null;
            _isNew = _renaming = _embedded = _externalChange = false;
            _hasDraft = false;
            hasUnsavedChanges = false;
            UpdatePanes();
        }

        /// <summary>Reopens the edit that was unsaved when scripts reloaded. Returns false when there was none.</summary>
        bool RestoreDraft()
        {
            if (!_hasDraft) return false;
            // Copy first: Show() rewrites the draft for the entry it opens.
            string key = _draft.Key, type = _draft.Type, text = _draft.Text, baseKey = _draft.BaseKey, baseJson = _draft.BaseJson;
            bool isNew = _draft.IsNew, renaming = _draft.Renaming, embedded = _draft.Embedded, indented = _draft.EmbeddedIndented;

            PrefEntry opened;
            if (isNew) opened = new PrefEntry(key, PrefJson.ParseType(type), null);
            else if (PrefJson.TryParseDocument(baseJson, out var entries, out _) && entries.Count == 1) opened = entries[0];
            else opened = PlayerPrefStore.Read(baseKey) ?? new PrefEntry(baseKey, PrefType.Unknown, null);

            Show(opened, isNew);
            _renaming = renaming;
            _embedded = embedded;
            _embeddedIndented = indented;
            _embedToggle.SetValueWithoutNotify(embedded);
            _key.SetValueWithoutNotify(key);
            _type.SetValueWithoutNotify(type);
            _value.SetValueWithoutNotify(text);
            if (!isNew) _externalChange = !Same(PlayerPrefStore.Read(baseKey), opened);
            UpdatePanes();
            SetStatus($"Kept your unsaved changes to {(string.IsNullOrEmpty(key) ? "the new key" : $"\"{key}\"")}.");
            return true;
        }

        // Unity asks Save / Discard / Cancel when a window with unsaved changes is closed.
        public override void SaveChanges()
        {
            Save();
            if (!IsDirty) base.SaveChanges();
        }

        /// <summary>Parses the editor contents into a value of the selected type.</summary>
        bool TryGetEditorValue(out object value, out string error)
        {
            value = null;
            if (_embedded)
            {
                if (!PrefJson.TryCollapseEmbedded(_value.value, _embeddedIndented, out var s, out error)) return false;
                value = s;
                return true;
            }
            if (PrefJson.TryParseValue(_value.value, EditorType, out value, out error)) return true;
            // The most common slip: typing text for a string without the JSON quotes.
            if (EditorType == PrefType.String && error.StartsWith("Invalid JSON") && !(_value.value ?? "").TrimStart().StartsWith("\""))
                error = "Strings need double quotes in JSON, e.g. \"Hakien\".";
            return false;
        }

        void UpdateDetail()
        {
            if (_editing == null) return;
            bool keyEditable = _isNew || _renaming;
            var key = _key.value ?? "";
            bool dirty = IsDirty;

            // Header
            _keyTitle.text = _editing.Key;
            _keyTitle.tooltip = _editing.Key + "\nDouble-click or press F2 to rename.";
            _keyTitle.style.display = keyEditable ? DisplayStyle.None : DisplayStyle.Flex;
            _key.style.display = keyEditable ? DisplayStyle.Flex : DisplayStyle.None;
            _keyLabel.style.display = keyEditable ? DisplayStyle.Flex : DisplayStyle.None;
            _menuButton.style.display = _isNew ? DisplayStyle.None : DisplayStyle.Flex;

            // Validation: parse once and reuse the value below.
            string error = null;
            object parsed = null;
            bool valid = TryGetEditorValue(out parsed, out var parseError);
            // An empty name just keeps Save disabled; it is not worth a red error before the user has typed anything.
            if (keyEditable && key.Length == 0) error = null;
            else if (!_isNew && _editing.Type == PrefType.Unknown && EditorType == PrefType.Unknown)
                error = "Pick a type to replace this value.";
            else if (!valid) error = parseError;
            _problem.Set(error, Severity.Error);

            // Notices, most important first.
            if (_externalChange)
            {
                var current = _all.Find(e => e.Key == _editing.Key);
                _notice.Set(current == null
                        ? "This key was deleted outside the editor."
                        : $"This key changed outside the editor. It is now {PrefJson.TypeName(current.Type)} {PrefFilter.Preview(current, 40)}.",
                    Severity.Warning, ("Load Theirs", (Action)LoadExternal), ("Keep Mine", (Action)KeepMine));
            }
            else if (!_isNew && _editing.Type == PrefType.Unknown)
                _notice.Set("PlayerPrefs can't tell this key's type, so its value can't be read. Saving replaces it, and Undo can't bring the old value back.", Severity.Warning);
            else if (keyEditable && key.Length > 0 && key.Trim() != key)
                _notice.Set("The key name starts or ends with a space.", Severity.Warning);
            else _notice.Set(null);

            // Value tools: "Edit as JSON" for strings holding a JSON object or array; Format only where it can change something.
            _embedToggle.style.display = _embedded || valid && parsed is string text && HoldsJson(text) ? DisplayStyle.Flex : DisplayStyle.None;
            _format.style.display = _embedded ? DisplayStyle.Flex : DisplayStyle.None;
            _format.SetEnabled(error == null);

            if (_typeNote != null) _info.text = _typeNote;
            else if (valid && parsed is string str) _info.text = (_embedded ? "JSON content · " : "") + $"{str.Length:N0} characters";
            else _info.text = "";

            // Footer
            _dirty.text = !dirty ? (_renaming ? "Renaming" : "")
                : _isNew ? "● New key"
                : _renaming && key != _editing.Key ? "● Renamed"
                : "● Unsaved";
            _dirty.tooltip = dirty ? "Not saved yet. Save writes it to PlayerPrefs; Revert drops it." : "";
            _save.SetEnabled(dirty && error == null && valid && key.Length > 0);
            _revert.SetEnabled(dirty || _renaming);
            _revert.text = _isNew ? "Discard" : _renaming && !dirty ? "Cancel" : "Revert";

            // Unity marks the tab and asks before closing; the draft survives script reloads.
            hasUnsavedChanges = dirty;
            saveChangesMessage = $"Save changes to the PlayerPref \"{key}\"?";
            _hasDraft = dirty;
            if (dirty)
            {
                _draft.Key = key;
                _draft.Type = _type.value;
                _draft.Text = _value.value;
                _draft.Renaming = _renaming;
                _draft.Embedded = _embedded;
                _draft.EmbeddedIndented = _embeddedIndented;
            }
        }

        /// <summary>Whether a string's content is a JSON object or array. Large strings are only checked by their first character.</summary>
        static bool HoldsJson(string text)
        {
            var trimmed = text.TrimStart();
            if (trimmed.Length == 0 || trimmed[0] != '{' && trimmed[0] != '[') return false;
            return text.Length >= LargeText || PrefJson.TryExpandEmbedded(text, out _);
        }

        /// <summary>The editor text as Format would leave it, or null when it does not parse.</summary>
        string FormattedText()
        {
            if (!TryGetEditorValue(out var value, out _)) return null;
            if (_embedded) return PrefJson.TryExpandEmbedded((string)value, out var pretty) ? pretty : null;
            return PrefJson.FormatValue(EditorType, value);
        }

        void OnTypeChanged(ChangeEvent<string> e)
        {
            var from = PrefJson.ParseType(e.previousValue);
            var to = PrefJson.ParseType(e.newValue);
            if (_embedded)
            {
                SetEmbedded(false);
                if (_embedded)
                {
                    _type.SetValueWithoutNotify(e.previousValue);
                    return;
                }
            }
            // Carry the value over when it survives the change (5 → 5.0 → "5"); otherwise say so and leave it to the user.
            if (to == PrefType.Unknown) _typeNote = null;
            else if (PrefJson.TryConvertText(_value.value, from, to, out var converted))
            {
                _typeNote = converted == _value.value ? null : $"Converted from {PrefJson.TypeName(from)}.";
                _value.SetValueWithoutNotify(converted);
            }
            else _typeNote = from == PrefType.Unknown ? null : $"Can't convert this value to {PrefJson.TypeName(to)}. Enter a new one.";
            UpdateDetail();
        }

        void SetEmbedded(bool on)
        {
            if (on == _embedded)
            {
                _embedToggle.SetValueWithoutNotify(on);
                return;
            }
            if (on)
            {
                if (!PrefJson.TryParseValue(_value.value, PrefType.String, out var s, out var error) ||
                    !PrefJson.TryExpandEmbedded((string)s, out var pretty))
                {
                    _embedToggle.SetValueWithoutNotify(false);
                    SetStatus(error ?? "The string does not hold a JSON object or array.", Severity.Error);
                    return;
                }
                if (_savedEmbedded == null && !_isNew && _editing.Type == PrefType.String) ComputeSavedEmbedded();
                if (_savedEmbedded == null) _embeddedIndented = ((string)s).IndexOf('\n') >= 0;
                _value.SetValueWithoutNotify(pretty);
            }
            else
            {
                if (!PrefJson.TryCollapseEmbedded(_value.value, _embeddedIndented, out var s, out var error))
                {
                    _embedToggle.SetValueWithoutNotify(true);
                    SetStatus("Fix the JSON before switching back: " + error, Severity.Error);
                    return;
                }
                _value.SetValueWithoutNotify(PrefJson.FormatValue(PrefType.String, s));
            }
            _embedded = on;
            _preferEmbedded = on;
            SaveSettings();
            _embedToggle.SetValueWithoutNotify(on);
            UpdateDetail();
        }

        void FormatValue()
        {
            var formatted = FormattedText();
            if (formatted != null) _value.value = formatted;
        }

        bool ConfirmDiscard()
        {
            if (!IsDirty) return true;
            var name = string.IsNullOrEmpty(_key.value) ? "the new key" : $"\"{_key.value}\"";
            return EditorUtility.DisplayDialog("Discard changes?", $"Your changes to {name} have not been saved.", "Discard", "Keep Editing");
        }

        // ---------- actions ----------

        /// <summary>Commits through the undo history. Returns false (and reports why) when nothing was written.</summary>
        bool Commit(string label, IReadOnlyCollection<PrefEntry> writes, IReadOnlyCollection<string> deletes)
        {
            _committing = true;
            try
            {
                PrefHistory.instance.Commit(label, writes, deletes);
                return true;
            }
            catch (Exception e)
            {
                SetStatus($"Nothing was written: {e.Message}", Severity.Error);
                return false;
            }
            finally
            {
                _committing = false;
            }
        }

        void AddNew()
        {
            if (!ConfirmDiscard()) return;
            _selection.Clear();
            Show(new PrefEntry("", PrefType.String, ""), true);
            ApplyFilter();
            _key.Focus();
            SetStatus("Name the new key, pick a type and enter its value.");
        }

        void Duplicate()
        {
            if (_editing == null || _isNew || !ConfirmDiscard()) return;
            var source = _editing;
            var key = source.Key + "_copy";
            for (int i = 2; PlayerPrefs.HasKey(key); i++) key = $"{source.Key}_copy{i}";
            _selection.Clear();
            Show(new PrefEntry(key, source.Type, source.Value), true);
            ApplyFilter();
            _key.Focus();
            SetStatus($"Duplicating \"{source.Key}\". Adjust the name and value, then Save.");
        }

        void StartRename()
        {
            if (_editing == null || _isNew) return;
            _renaming = true;
            UpdateDetail();
            _key.Focus();
            _key.SelectAll();
        }

        void CancelRename()
        {
            _renaming = false;
            _key.SetValueWithoutNotify(_editing.Key);
            UpdateDetail();
            _list.Focus();
        }

        void TogglePin(string key) => SetPins(new[] { key }, !_pinned.Contains(key));

        void SetPins(IEnumerable<string> keys, bool pinned)
        {
            foreach (var k in keys) PlayerPrefStore.SetPinned(k, pinned);
            Reload();
        }

        void LoadExternal()
        {
            var current = PlayerPrefStore.Read(_editing.Key);
            if (current != null) Show(current, false);
            else ClearEditor();
            SetStatus("Loaded the current value; your edit was discarded.");
        }

        /// <summary>Keeps the edit and makes the outside value the new baseline, so Revert goes back to it and Save no longer asks.</summary>
        void KeepMine()
        {
            var current = PlayerPrefStore.Read(_editing.Key);
            string text = _value.value, type = _type.value, key = _key.value;
            bool renaming = _renaming, embedded = _embedded;
            if (current == null)
            {
                SetBaseline(new PrefEntry(key, PrefJson.ParseType(type), null), true);
                renaming = false;
            }
            else SetBaseline(current, false);
            _renaming = renaming;
            _embedded = embedded;
            _key.SetValueWithoutNotify(key);
            _type.SetValueWithoutNotify(type);
            _value.SetValueWithoutNotify(text);
            UpdatePanes();
            SetStatus("Kept your edit. Saving will replace the outside change.");
        }

        void Save()
        {
            if (_editing == null || !IsDirty) return;
            if (!TryGetEditorValue(out var value, out var error))
            {
                SetStatus($"Not saved: {error}", Severity.Error);
                return;
            }
            var type = EditorType;
            var key = _isNew || _renaming ? _key.value : _editing.Key;
            if (string.IsNullOrEmpty(key))
            {
                SetStatus("Not saved: enter a key name.", Severity.Error);
                return;
            }
            string oldKey = _renaming && key != _editing.Key ? _editing.Key : null;

            if (_isNew && PlayerPrefs.HasKey(key))
            {
                int choice = EditorUtility.DisplayDialogComplex("Key already exists",
                    $"\"{key}\" already exists in PlayerPrefs.\n\nOpen its current value instead, or replace it with the value you entered? Replacing can be undone.",
                    "Open Existing", "Cancel", "Replace");
                if (choice == 1) return;
                if (choice == 0)
                {
                    PlayerPrefStore.Track(new[] { key });
                    _isNew = false;
                    _editing = null;
                    Reload();
                    Show(PlayerPrefStore.Read(key), false);
                    SetStatus($"Opened the existing key \"{key}\".");
                    return;
                }
            }
            if (oldKey != null && PlayerPrefs.HasKey(key) &&
                !EditorUtility.DisplayDialog("Key already exists", $"\"{key}\" already exists. Renaming \"{oldKey}\" replaces it. You can undo this.", "Replace", "Cancel"))
                return;

            if (!_isNew)
            {
                var current = PlayerPrefStore.Read(_editing.Key);
                if (!Same(current, _editing))
                {
                    int choice = EditorUtility.DisplayDialogComplex("Changed outside the editor",
                        $"\"{_editing.Key}\" changed after you opened it, for example in Play Mode.\n\nReplace that change with your value?",
                        "Replace", "Cancel", "Load Theirs");
                    if (choice == 1) return;
                    if (choice == 2)
                    {
                        LoadExternal();
                        return;
                    }
                }
                if (current != null && current.Type == PrefType.Unknown &&
                    !EditorUtility.DisplayDialog("Replace an unreadable value?",
                        $"PlayerPrefs can't tell the type of \"{_editing.Key}\", so its current value can't be read or restored.\n\nSaving replaces it with {PrefJson.TypeName(type)} {PrefFilter.Preview(new PrefEntry(key, type, value), 40)}, and Undo can't bring the old value back.",
                        "Replace", "Cancel"))
                    return;
            }

            var entry = new PrefEntry(key, type, value);
            if (!Commit(oldKey != null ? $"Rename \"{oldKey}\" to \"{key}\"" : $"Save \"{key}\"",
                    new[] { entry }, oldKey != null ? new[] { oldKey } : Array.Empty<string>()))
                return;
            if (oldKey != null && _pinned.Contains(oldKey))
            {
                PlayerPrefStore.SetPinned(oldKey, false);
                PlayerPrefStore.SetPinned(key, true);
            }
            bool embedded = _embedded;
            Show(entry, false);
            if (embedded != _embedded && _savedEmbedded != null) SetEmbedded(embedded);
            Reload();
            SetStatus(oldKey != null ? $"Renamed \"{oldKey}\" to \"{key}\"." : $"Saved \"{key}\".");
        }

        void Revert()
        {
            if (_editing == null) return;
            if (_isNew)
            {
                ClearEditor();
                SetStatus("Discarded the new key.");
                return;
            }
            if (_renaming && !IsDirty)
            {
                CancelRename();
                return;
            }
            var current = PlayerPrefStore.Read(_editing.Key);
            if (current != null) Show(current, false);
            else ClearEditor();
            SetStatus("Reverted to the saved value.");
        }

        void DeleteAll()
        {
            var keys = ExportScope().Select(e => e.Key).ToList();
            int hidden = _all.Count - keys.Count;
            DeleteKeys(keys, $"Delete all {keys.Count} {Plural(keys.Count, "key")}?" + (hidden > 0 ? $" Unity's {hidden} internal {Plural(hidden, "key")} are kept." : ""));
        }

        void DeleteKeys(List<string> keys, string question = null)
        {
            if (keys.Count == 0) return;
            int unknown = keys.Count(k => _all.Find(e => e.Key == k)?.Type == PrefType.Unknown);
            question = question ?? (keys.Count == 1 ? $"Delete \"{keys[0]}\"?" : $"Delete {keys.Count} keys?");
            var undo = unknown == 0
                ? "You can bring them back with Undo until Unity closes."
                : $"{unknown} of them {(unknown == 1 ? "has" : "have")} an unknown type and can't be brought back with Undo.";
            if (keys.Count == 1) undo = unknown == 0 ? "You can bring it back with Undo until Unity closes." : "Its type is unknown, so Undo can't bring it back.";
            if (!EditorUtility.DisplayDialog("Delete PlayerPrefs", question + "\n\n" + undo, "Delete", "Cancel")) return;

            if (!Commit(keys.Count == 1 ? $"Delete \"{keys[0]}\"" : $"Delete {keys.Count} keys", Array.Empty<PrefEntry>(), keys)) return;
            if (_editing != null && keys.Contains(_editing.Key)) ClearEditor();
            // Keep the open key selected when only other keys were deleted.
            _selection = _editing != null && !_isNew ? new List<string> { _editing.Key } : new List<string>();
            Reload();
            SetStatus(keys.Count == 1 ? $"Deleted \"{keys[0]}\"." : $"Deleted {keys.Count} keys.");
        }

        void Undo() => StepHistory(() => PrefHistory.instance.Undo(), "Undid");

        void Redo() => StepHistory(() => PrefHistory.instance.Redo(), "Redid");

        void StepHistory(Func<string> step, string verb)
        {
            string label;
            try
            {
                label = step();
            }
            catch (Exception e)
            {
                Reload();
                SetStatus(e.Message, Severity.Error);
                return;
            }
            if (label == null) return;
            Reload();
            var lost = PrefHistory.instance.LastUnrestorable;
            if (lost.Count == 0) SetStatus($"{verb}: {label}.");
            else SetStatus($"{verb}: {label}. {string.Join(", ", lost.Select(k => $"\"{k}\""))} could not be restored: the earlier value had an unknown type.", Severity.Warning);
        }

        void UpdateUndoButtons()
        {
            var h = PrefHistory.instance;
            _undo.SetEnabled(h.CanUndo);
            _undo.tooltip = h.CanUndo ? $"Undo: {h.UndoLabel}" : "Nothing to undo";
            _redo.SetEnabled(h.CanRedo);
            _redo.tooltip = h.CanRedo ? $"Redo: {h.RedoLabel}" : "Nothing to redo";
        }

        void CopyEntries(List<PrefEntry> entries, string what)
        {
            EditorGUIUtility.systemCopyBuffer = PlayerPrefStore.ExportJson(entries, out int exported, out int skipped);
            if (skipped == 0) SetStatus($"Copied {what} as JSON ({exported} {Plural(exported, "key")}).");
            else SetStatus($"Copied {exported} {Plural(exported, "key")} as JSON. {skipped} of unknown type left out: their values can't be read.", Severity.Warning);
        }

        void ExportToFile(List<PrefEntry> entries, string defaultName)
        {
            var path = EditorUtility.SaveFilePanel("Export PlayerPrefs", "", defaultName, "json");
            if (string.IsNullOrEmpty(path)) return;
            try
            {
                File.WriteAllText(path, PlayerPrefStore.ExportJson(entries, out int exported, out int skipped));
                if (skipped == 0) SetStatus($"Exported {exported} {Plural(exported, "key")} to {path}.");
                else SetStatus($"Exported {exported} {Plural(exported, "key")} to {path}. {skipped} of unknown type left out: their values can't be read.", Severity.Warning);
            }
            catch (Exception e)
            {
                SetStatus($"Export failed: {e.Message}", Severity.Error);
            }
        }

        void ImportFromFile()
        {
            var path = EditorUtility.OpenFilePanel("Import PlayerPrefs", "", "json");
            if (!string.IsNullOrEmpty(path)) ImportFile(path);
        }

        void ImportFile(string path)
        {
            string json;
            try
            {
                json = File.ReadAllText(path);
            }
            catch (Exception e)
            {
                SetStatus($"Import failed: {e.Message}", Severity.Error);
                return;
            }
            ImportText(json, Path.GetFileName(path));
        }

        void ImportText(string json, string source)
        {
            if (!PrefJson.TryParseDocument(json, out var entries, out var error))
            {
                SetStatus($"Import failed, nothing was written. {error}", Severity.Error);
                EditorUtility.DisplayDialog("Can't import", $"{source} is not a valid PlayerPrefs document, so nothing was written.\n\n{error}", "OK");
                return;
            }
            if (entries.Count == 0)
            {
                SetStatus($"{source} has no keys to import.", Severity.Warning);
                return;
            }
            ImportPreviewWindow.Open(source, entries, message =>
            {
                if (this == null) return;
                Reload();
                SetStatus(message);
            });
        }

        void OnDragUpdated(DragUpdatedEvent e)
        {
            if (DragAndDrop.paths.Any(IsJson)) DragAndDrop.visualMode = DragAndDropVisualMode.Copy;
        }

        void OnDragPerform(DragPerformEvent e)
        {
            var path = DragAndDrop.paths.FirstOrDefault(IsJson);
            if (path == null) return;
            DragAndDrop.AcceptDrag();
            ImportFile(path);
        }

        static bool IsJson(string path) => path.EndsWith(".json", StringComparison.OrdinalIgnoreCase);

        void ShowStorageLocation()
        {
            var location = NativePrefs.Location;
            if (location == null) return;
            if (File.Exists(location)) EditorUtility.RevealInFinder(location);
            EditorGUIUtility.systemCopyBuffer = location;
            SetStatus($"Copied the storage location: {location}");
        }

        // ---------- status bar ----------

        VisualElement BuildStatusBar()
        {
            _statusIcon = PrefStyles.Box("ppe-status-icon");
            _status = PrefStyles.Text("").Ellipsis();
            _status.AddToClassList("ppe-grow");
            _source = PrefStyles.Text("", "ppe-source", "ppe-dim");
            return PrefStyles.Row(_statusIcon, _status, _source).Classes("ppe-statusbar");
        }

        void SetStatus(string message, Severity severity = Severity.Info)
        {
            _status.text = message;
            _status.tooltip = message;
            _status.EnableInClassList("ppe-text--error", severity == Severity.Error);
            _status.EnableInClassList("ppe-text--warning", severity == Severity.Warning);
            var icon = severity == Severity.Info ? null : PrefStyles.FindIcon(PrefStyles.SeverityIcon(severity));
            _statusIcon.style.backgroundImage = icon != null ? new StyleBackground(icon) : new StyleBackground(StyleKeyword.None);
            _statusIcon.style.display = icon != null ? DisplayStyle.Flex : DisplayStyle.None;
        }

        // ---------- shortcuts (active while this window has focus) ----------

        [Shortcut("kinatraa/Player Pref Editor/Save", typeof(PlayerPrefEditorWindow), KeyCode.S, ShortcutModifiers.Action)]
        static void SaveShortcut(ShortcutArguments args) => (args.context as PlayerPrefEditorWindow)?.Save();

        [Shortcut("kinatraa/Player Pref Editor/Refresh", typeof(PlayerPrefEditorWindow), KeyCode.F5)]
        static void RefreshShortcut(ShortcutArguments args) => (args.context as PlayerPrefEditorWindow)?.Reload();

        [Shortcut("kinatraa/Player Pref Editor/Find", typeof(PlayerPrefEditorWindow), KeyCode.F, ShortcutModifiers.Action)]
        static void FindShortcut(ShortcutArguments args) => (args.context as PlayerPrefEditorWindow)?._search.Focus();

        [Shortcut("kinatraa/Player Pref Editor/New Key", typeof(PlayerPrefEditorWindow), KeyCode.N, ShortcutModifiers.Action)]
        static void AddShortcut(ShortcutArguments args) => (args.context as PlayerPrefEditorWindow)?.AddNew();

        [Shortcut("kinatraa/Player Pref Editor/Rename", typeof(PlayerPrefEditorWindow), KeyCode.F2)]
        static void RenameShortcut(ShortcutArguments args) => (args.context as PlayerPrefEditorWindow)?.StartRename();
    }
}
