using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.ShortcutManagement;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

namespace kinatraa.PlayerPrefEditor
{
    public class PlayerPrefEditorWindow : EditorWindow
    {
        const string SettingsPrefix = "kinatraa.PlayerPrefEditor.Window.";
        const float LeftMin = 150, RightMin = 230;
        static readonly List<string> TypeChoices = new List<string> { "int", "float", "string", "unknown" };

        // Survive script reloads through window serialization.
        [SerializeField] string _query = "";
        [SerializeField] List<string> _selection = new List<string>();

        readonly PrefFilter _filter = new PrefFilter();
        bool _autoRefresh, _preferEmbedded;
        float _leftPreferred = 320;

        List<PrefEntry> _all = new List<PrefEntry>();
        readonly List<PrefEntry> _visible = new List<PrefEntry>();
        HashSet<string> _pinned = new HashSet<string>();
        bool _committing, _wideList = true;

        // Detail editor. _editing is the stored entry the editor was opened with; the "unsaved" check compares against it.
        PrefEntry _editing;
        bool _isNew, _renaming, _embedded, _embeddedIndented;
        string _savedPlain, _savedEmbedded;
        string _externalNote;

        ToolbarSearchField _search;
        ToolbarMenu _filterMenu;
        ToolbarButton _undo, _redo;
        VisualElement _body, _left;
        ListView _list;
        Label _count, _searchError, _status, _source;
        VisualElement _emptyPane, _multiPane, _detail;
        Label _multiLabel;
        TextField _key, _value;
        DropdownField _type;
        Button _rename, _format, _save, _revert, _copy, _pin, _duplicate, _delete;
        Toggle _embedToggle;
        Label _info, _dirty, _warning, _error;

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

        void CreateGUI()
        {
            titleContent = new GUIContent("Player Pref Editor");
            minSize = new Vector2(LeftMin + RightMin + 80, 280);
            LoadSettings();

            var root = rootVisualElement;
            root.Add(BuildToolbar());

            var sheet = AssetDatabase.LoadAssetAtPath<StyleSheet>("Packages/com.kinatraa.playerprefeditor/Editor/PlayerPrefEditorWindow.uss");
            if (sheet != null) root.styleSheets.Add(sheet);
            root.Add(BuildBody());

            _status = new Label().Ellipsis();
            _status.style.flexGrow = 1;
            _source = new Label { style = { color = PrefStyles.Dim, marginLeft = 8, flexShrink = 0 } };
            var statusBar = PrefStyles.Row(_status, _source);
            statusBar.style.paddingLeft = statusBar.style.paddingRight = 4;
            statusBar.style.height = 20;
            statusBar.style.borderTopWidth = 1;
            statusBar.style.borderTopColor = new Color(0, 0, 0, 0.25f);
            root.Add(statusBar);

            root.RegisterCallback<DragUpdatedEvent>(OnDragUpdated);
            root.RegisterCallback<DragPerformEvent>(OnDragPerform);
            root.schedule.Execute(() =>
            {
                if (_autoRefresh && EditorApplication.isPlaying) Reload();
            }).Every(1000);

            Reload();
            var restore = _selection.ToList();
            if (restore.Count == 1 && _all.Find(e => e.Key == restore[0]) is PrefEntry entry) Show(entry, false);
            else UpdatePanes();
            SetStatus(PlayerPrefStore.LastNativeError != null
                ? "Could not read the OS store (" + PlayerPrefStore.LastNativeError + "). Showing keys written with this tool."
                : "Ready. Drop a .json file here to import it.", PlayerPrefStore.LastNativeError != null);
        }

        /// <summary>List, draggable divider, editor. The list keeps the width the user dragged it to and only shrinks while the window is too narrow.</summary>
        VisualElement BuildBody()
        {
            _body = new VisualElement { style = { flexDirection = FlexDirection.Row, flexGrow = 1, flexShrink = 1 } };
            _left = BuildList();
            _left.style.flexShrink = 0;
            _body.Add(_left);

            var divider = new VisualElement { style = { width = 1, flexShrink = 0, backgroundColor = new Color(0, 0, 0, 0.3f) } };
            var grip = new VisualElement { style = { position = Position.Absolute, left = -3, width = 7, top = 0, bottom = 0 } };
            grip.AddToClassList("ppe-divider-grip");
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

            var right = BuildRight();
            right.style.flexShrink = 1;
            _body.Add(right);
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
            var toolbar = new Toolbar();

            _search = new ToolbarSearchField { tooltip = "Search keys and values (Ctrl/Cmd+F)" };
            _search.style.flexGrow = 1;
            _search.style.flexShrink = 1;
            _search.style.minWidth = 70;
            _search.style.width = StyleKeyword.Auto;
            _search.style.maxWidth = 420;
            _search.SetValueWithoutNotify(_query);
            _search.RegisterValueChangedCallback(e =>
            {
                _query = _filter.Query = e.newValue;
                ApplyFilter();
            });
            toolbar.Add(_search);

            _filterMenu = new ToolbarMenu { tooltip = "Type filter, search options and sort order" };
            _filterMenu.style.flexShrink = 0;
            var menu = _filterMenu.menu;
            AddTypeFilter(menu, "All types", null);
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
            toolbar.Add(Fixed(PrefStyles.IconButton("Refresh", "Refresh", "Reload from PlayerPrefs (F5)", () =>
            {
                Reload();
                SetStatus("Refreshed.");
            })));
            toolbar.Add(Fixed(PrefStyles.IconButton("Toolbar Plus", "Add", "Add a new key (Ctrl/Cmd+N)", AddNew)));
            toolbar.Add(_undo = Fixed(new ToolbarButton(Undo) { text = "Undo" }));
            toolbar.Add(_redo = Fixed(new ToolbarButton(Redo) { text = "Redo" }));

            var import = Fixed(new ToolbarMenu { text = "Import", tooltip = "Import a JSON document (you can also drop a .json file on the window)" });
            import.menu.AppendAction("From File…", _ => ImportFromFile());
            import.menu.AppendAction("From Clipboard", _ => ImportText(EditorGUIUtility.systemCopyBuffer, "clipboard"));
            toolbar.Add(import);

            var export = Fixed(new ToolbarMenu { text = "Export", tooltip = "Export or copy as JSON" });
            export.menu.AppendAction("All Keys to File…", _ => ExportToFile(ExportScope(), "playerprefs.json"), _ => Status(_all.Count > 0));
            export.menu.AppendAction("Visible Keys to File…", _ => ExportToFile(_visible, "playerprefs-filtered.json"), _ => Status(_visible.Count > 0));
            export.menu.AppendAction("Selected Keys to File…", _ => ExportToFile(SelectedEntries(), "playerprefs-selection.json"), _ => Status(_selection.Count > 0));
            export.menu.AppendSeparator();
            export.menu.AppendAction("Copy All as JSON", _ => CopyEntries(ExportScope(), "all"), _ => Status(_all.Count > 0));
            export.menu.AppendAction("Copy Selected as JSON", _ => CopyEntries(SelectedEntries(), "selected"), _ => Status(_selection.Count > 0));
            toolbar.Add(export);

            var more = Fixed(new ToolbarMenu { text = "More", tooltip = "More actions" });
            more.menu.AppendAction("Auto Refresh in Play Mode", _ =>
            {
                _autoRefresh = !_autoRefresh;
                SaveSettings();
            }, _ => _autoRefresh ? DropdownMenuAction.Status.Checked : DropdownMenuAction.Status.Normal);
            more.menu.AppendSeparator();
            more.menu.AppendAction("Delete All Keys…", _ => DeleteKeys(ExportScope().Select(e => e.Key).ToList(), "all keys"), _ => Status(_all.Count > 0));
            more.menu.AppendAction("Clear Undo History", _ => PrefHistory.instance.Clear(), _ => Status(PrefHistory.instance.CanUndo || PrefHistory.instance.CanRedo));
            more.menu.AppendSeparator();
            more.menu.AppendAction("Show Storage Location", _ => ShowStorageLocation());
            more.menu.AppendAction("Documentation", _ => Application.OpenURL("https://github.com/kinatraa/com.kinatraa.playerprefeditor#readme"));
            toolbar.Add(more);

            return toolbar;
        }

        static T Fixed<T>(T e) where T : VisualElement
        {
            e.style.flexShrink = 0;
            return e;
        }

        static DropdownMenuAction.Status Status(bool enabled) => enabled ? DropdownMenuAction.Status.Normal : DropdownMenuAction.Status.Disabled;

        void AddTypeFilter(DropdownMenu menu, string name, PrefType? type)
        {
            menu.AppendAction(name, _ =>
            {
                _filter.Type = type;
                SaveSettings();
                ApplyFilter();
            }, _ => _filter.Type == type ? DropdownMenuAction.Status.Checked : DropdownMenuAction.Status.Normal);
        }

        void AddToggle(DropdownMenu menu, string name, Func<bool> get, Action<bool> set)
        {
            menu.AppendAction(name, _ =>
            {
                set(!get());
                SaveSettings();
                ApplyFilter();
            }, _ => get() ? DropdownMenuAction.Status.Checked : DropdownMenuAction.Status.Normal);
        }

        void AddSort(DropdownMenu menu, string name, SortMode mode)
        {
            menu.AppendAction(name, _ =>
            {
                _filter.Sort = mode;
                SaveSettings();
                ApplyFilter();
            }, _ => _filter.Sort == mode ? DropdownMenuAction.Status.Checked : DropdownMenuAction.Status.Normal);
        }

        // ---------- list ----------

        VisualElement BuildList()
        {
            var left = new VisualElement { style = { minWidth = LeftMin, overflow = Overflow.Hidden } };
            _count = new Label { style = { unityFontStyleAndWeight = FontStyle.Bold } }.Ellipsis();
            _count.style.flexGrow = 1;
            var header = PrefStyles.Row(_count);
            header.style.paddingLeft = header.style.paddingRight = 4;
            header.style.height = 20;
            left.Add(header);
            _searchError = new Label { style = { color = PrefStyles.Error, paddingLeft = 4, whiteSpace = WhiteSpace.Normal, display = DisplayStyle.None } };
            left.Add(_searchError);

            _list = new ListView(_visible, 20, MakeRow, BindRow)
            {
                selectionType = SelectionType.Multiple,
                showAlternatingRowBackgrounds = AlternatingRowBackground.ContentOnly,
            };
            _list.style.flexGrow = 1;
#if UNITY_2022_2_OR_NEWER
            _list.selectionChanged += _ => OnListSelection();
#else
            _list.onSelectionChange += _ => OnListSelection();
#endif
            _list.RegisterCallback<KeyDownEvent>(OnListKeyDown);
            _list.AddManipulator(new ContextualMenuManipulator(BuildContextMenu));
            left.Add(_list);
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
            var pin = new Label("●") { name = "pin", tooltip = "Pinned", style = { width = 10, flexShrink = 0, fontSize = 8, color = PrefStyles.Warning, unityTextAlign = TextAnchor.MiddleCenter } };
            var key = new Label { name = "key", style = { flexBasis = new Length(55, LengthUnit.Percent), flexGrow = 1 } }.Ellipsis();
            var preview = new Label { name = "preview", style = { flexBasis = new Length(45, LengthUnit.Percent), flexGrow = 1, color = PrefStyles.Dim, marginLeft = 6, unityFontDefinition = new StyleFontDefinition(PrefStyles.Monospace), fontSize = 11 } }.Ellipsis();
            var badge = new Label { name = "badge", style = { unityFontStyleAndWeight = FontStyle.Bold, marginLeft = 4, flexShrink = 0 } };
            var row = PrefStyles.Row(pin, key, preview, badge);
            row.style.paddingRight = 4;
            row.style.height = 20;
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
            row.Q<Label>("pin").style.visibility = _pinned.Contains(e.Key) ? Visibility.Visible : Visibility.Hidden;
            var key = row.Q<Label>("key");
            key.text = e.Key;
            key.tooltip = e.Key;
            var preview = row.Q<Label>("preview");
            preview.text = PrefFilter.Preview(e);
            preview.style.display = _wideList ? DisplayStyle.Flex : DisplayStyle.None;
            var badge = row.Q<Label>("badge");
            badge.text = $"[{PrefJson.TypeName(e.Type)}]";
            badge.style.color = PrefStyles.TypeColor(e.Type);
        }

        void Reload()
        {
            _all = PlayerPrefStore.ReadAll();
            _pinned = PlayerPrefStore.GetPinned();
            _selection.RemoveAll(k => _all.All(e => e.Key != k));

            if (_editing != null && !_isNew)
            {
                var current = _all.Find(e => e.Key == _editing.Key);
                if (!IsDirty)
                {
                    // Only reopen when the stored value moved, so a refresh never disturbs the field being edited.
                    if (current == null) ClearEditor();
                    else if (!Same(current, _editing)) Show(current, false);
                }
                else if (!Same(current, _editing))
                {
                    _externalNote = current == null
                        ? "This key was deleted outside the editor. Saving creates it again."
                        : "This key changed outside the editor since you opened it. Saving overwrites that change.";
                }
            }

            _source.text = SourceText();
            _source.tooltip = NativePrefs.Location;
            ApplyFilter();
            UpdateUndoButtons();
            UpdatePanes();
        }

        static bool Same(PrefEntry a, PrefEntry b) =>
            a == null ? b == null : b != null && (a.SameValue(b) || a.Type == PrefType.Unknown && b.Type == PrefType.Unknown);

        string SourceText()
        {
            switch (Application.platform)
            {
                case RuntimePlatform.WindowsEditor: return "Registry";
                case RuntimePlatform.OSXEditor: return "macOS plist";
                case RuntimePlatform.LinuxEditor: return "Linux prefs";
                default: return "Tracked keys";
            }
        }

        void ApplyFilter()
        {
            _visible.Clear();
            _visible.AddRange(_filter.Apply(_all, _pinned, out var searchError));
            _searchError.text = searchError ?? "";
            _searchError.style.display = searchError == null ? DisplayStyle.None : DisplayStyle.Flex;

            int total = _filter.ShowInternal ? _all.Count : _all.Count(e => !PlayerPrefStore.IsInternalKey(e.Key));
            int hidden = _all.Count - total;
            _count.text = (_visible.Count == total ? $"{total} keys" : $"{_visible.Count} of {total} keys")
                          + (_selection.Count > 1 ? $" · {_selection.Count} selected" : "")
                          + (hidden > 0 ? $" · {hidden} internal hidden" : "");
            _filterMenu.text = _filter.Type == null ? "All types" : PrefJson.TypeName(_filter.Type.Value);
            _list.RefreshItems();
            SyncListSelection();
        }

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
                DeleteKeys(_selection.ToList(), null);
                e.StopPropagation();
            }
            else if (e.keyCode == KeyCode.C && e.actionKey && _selection.Count > 0)
            {
                CopyEntries(SelectedEntries(), "selected");
                e.StopPropagation();
            }
        }

        void BuildContextMenu(ContextualMenuPopulateEvent evt)
        {
            var menu = evt.menu;
            var keys = _selection.ToList();
            if (keys.Count == 0)
            {
                menu.AppendAction("Add New Key", _ => AddNew());
                return;
            }
            if (keys.Count == 1)
            {
                var key = keys[0];
                menu.AppendAction("Copy Key Name", _ =>
                {
                    EditorGUIUtility.systemCopyBuffer = key;
                    SetStatus($"Copied key name \"{key}\".");
                });
                menu.AppendAction("Copy as JSON", _ => CopyEntries(SelectedEntries(), $"\"{key}\""));
                menu.AppendSeparator();
                menu.AppendAction("Duplicate", _ => Duplicate());
                menu.AppendAction("Rename", _ => StartRename());
                menu.AppendAction(_pinned.Contains(key) ? "Unpin" : "Pin to Top", _ => TogglePin(key));
                menu.AppendSeparator();
                menu.AppendAction("Delete", _ => DeleteKeys(keys, null));
                return;
            }
            menu.AppendAction($"Copy {keys.Count} Keys as JSON", _ => CopyEntries(SelectedEntries(), "selected"));
            menu.AppendAction($"Export {keys.Count} Keys to File…", _ => ExportToFile(SelectedEntries(), "playerprefs-selection.json"));
            menu.AppendAction("Pin to Top", _ => SetPins(keys, true));
            menu.AppendAction("Unpin", _ => SetPins(keys, false));
            menu.AppendSeparator();
            menu.AppendAction($"Delete {keys.Count} Keys", _ => DeleteKeys(keys, null));
        }

        List<PrefEntry> SelectedEntries() => _all.Where(e => _selection.Contains(e.Key)).ToList();

        /// <summary>What "all" means for Export, Copy All and Delete All: every key, minus Unity's own unless they are shown.</summary>
        List<PrefEntry> ExportScope() => _all.Where(e => _filter.ShowInternal || !PlayerPrefStore.IsInternalKey(e.Key)).ToList();

        // ---------- right side ----------

        VisualElement BuildRight()
        {
            var right = new VisualElement { style = { minWidth = RightMin, flexGrow = 1, overflow = Overflow.Hidden, paddingLeft = 6, paddingRight = 6, paddingTop = 6, paddingBottom = 6 } };

            _emptyPane = new VisualElement { style = { flexGrow = 1, justifyContent = Justify.Center, alignItems = Align.Center } };
            _emptyPane.Add(new Label("Select a key to edit it.") { style = { whiteSpace = WhiteSpace.Normal, unityTextAlign = TextAnchor.MiddleCenter } });
            _emptyPane.Add(new Button(AddNew) { text = "Add New Key", style = { marginTop = 6 } });
            _emptyPane.Add(new Label("Tip: drop a .json file here to import it.") { style = { color = PrefStyles.Dim, marginTop = 10, whiteSpace = WhiteSpace.Normal, unityTextAlign = TextAnchor.MiddleCenter } });
            right.Add(_emptyPane);

            _multiPane = new VisualElement { style = { flexGrow = 1, justifyContent = Justify.Center, alignItems = Align.Center } };
            _multiPane.Add(_multiLabel = new Label { style = { unityFontStyleAndWeight = FontStyle.Bold, marginBottom = 6 } });
            var multiButtons = PrefStyles.Row(
                new Button(() => CopyEntries(SelectedEntries(), "selected")) { text = "Copy as JSON" },
                new Button(() => ExportToFile(SelectedEntries(), "playerprefs-selection.json")) { text = "Export…" },
                new Button(() => DeleteKeys(_selection.ToList(), null)) { text = "Delete" });
            multiButtons.style.flexWrap = Wrap.Wrap;
            multiButtons.style.justifyContent = Justify.Center;
            _multiPane.Add(multiButtons);
            right.Add(_multiPane);

            right.Add(_detail = BuildDetail());
            return right;
        }

        static Label FieldLabel(string text) => new Label(text) { style = { width = 40, flexShrink = 0, marginLeft = 2 } };

        VisualElement BuildDetail()
        {
            var detail = new VisualElement { style = { flexGrow = 1, flexShrink = 1 } };

            _key = new TextField { style = { flexGrow = 1, flexShrink = 1, minWidth = 40 } };
            _key.RegisterValueChangedCallback(_ => UpdateDetail());
            _rename = new Button(StartRename) { text = "Rename", tooltip = "Rename this key", style = { flexShrink = 0 } };
            detail.Add(PrefStyles.Row(FieldLabel("Key"), _key, _rename));

            _type = new DropdownField(TypeChoices, 0) { style = { width = 90, flexShrink = 0 } };
            _type.RegisterValueChangedCallback(OnTypeChanged);
            _info = new Label { style = { color = PrefStyles.Dim, marginLeft = 6 } }.Ellipsis();
            detail.Add(PrefStyles.Row(FieldLabel("Type"), _type, _info));

            _embedToggle = new Toggle { text = "Edit as JSON", tooltip = "This string holds JSON. Edit it formatted; it is stored back as a string.", style = { flexShrink = 0, marginRight = 4 } };
            _embedToggle.RegisterValueChangedCallback(e => SetEmbedded(e.newValue));
            _format = new Button(FormatValue) { text = "Format", tooltip = "Pretty-print the JSON", style = { flexShrink = 0 } };
            var valueHeader = PrefStyles.Row(new Label("Value (JSON)") { style = { marginLeft = 2, flexShrink = 0, marginRight = 6 } }, PrefStyles.Spacer(), _embedToggle, _format);
            valueHeader.style.flexWrap = Wrap.Wrap;
            valueHeader.style.marginTop = 2;
            detail.Add(valueHeader);

            _value = new TextField { multiline = true };
            _value.style.flexGrow = 1;
            _value.style.flexShrink = 1;
            _value.style.minHeight = 40;
#if UNITY_2022_1_OR_NEWER
            _value.verticalScrollerVisibility = ScrollerVisibility.Auto;
#endif
            var mono = new StyleFontDefinition(PrefStyles.Monospace);
            _value.Query<VisualElement>().ForEach(e =>
            {
                e.style.unityFontDefinition = mono;
                e.style.whiteSpace = WhiteSpace.Normal;
                e.style.unityTextAlign = TextAnchor.UpperLeft;
            });
            _value.RegisterValueChangedCallback(_ => UpdateDetail());
            detail.Add(_value);

            _dirty = new Label("● unsaved changes") { style = { color = PrefStyles.Warning, marginLeft = 2, flexShrink = 0 } };
            _warning = new Label { style = { color = PrefStyles.Warning, marginLeft = 2, whiteSpace = WhiteSpace.Normal, flexShrink = 0 } };
            // At most three lines; the full message is in the tooltip.
            _error = new Label { style = { color = PrefStyles.Error, marginLeft = 2, whiteSpace = WhiteSpace.Normal, flexShrink = 0, maxHeight = 45, overflow = Overflow.Hidden } };
            detail.Add(_dirty);
            detail.Add(_warning);
            detail.Add(_error);

            var buttons = PrefStyles.Row(
                _save = new Button(Save) { text = "Save", tooltip = "Save (Ctrl/Cmd+S)" },
                _revert = new Button(Revert) { text = "Revert" },
                _copy = new Button(CopyJson) { text = "Copy JSON", tooltip = "Copy this key as a JSON document" },
                PrefStyles.Spacer(),
                _pin = new Button(() => TogglePin(_editing.Key)),
                _duplicate = new Button(Duplicate) { text = "Duplicate" },
                _delete = new Button(() => DeleteKeys(new List<string> { _editing.Key }, null)) { text = "Delete" });
            buttons.style.flexWrap = Wrap.Wrap;
            buttons.style.marginTop = 4;
            detail.Add(buttons);
            return detail;
        }

        void UpdatePanes()
        {
            bool editing = _editing != null;
            bool multi = !editing && _selection.Count > 1;
            _detail.style.display = editing ? DisplayStyle.Flex : DisplayStyle.None;
            _multiPane.style.display = multi ? DisplayStyle.Flex : DisplayStyle.None;
            _emptyPane.style.display = !editing && !multi ? DisplayStyle.Flex : DisplayStyle.None;
            if (multi) _multiLabel.text = $"{_selection.Count} keys selected";
            if (editing) UpdateDetail();
        }

        /// <summary>Opens an entry in the editor. For a new draft, <paramref name="entry"/> holds the initial key, type and value.</summary>
        void Show(PrefEntry entry, bool isNew)
        {
            _editing = entry;
            _isNew = isNew;
            _renaming = false;
            _externalNote = null;
            _savedPlain = entry.Type == PrefType.Unknown || entry.Value == null ? "" : PrefJson.FormatValue(entry.Type, entry.Value);
            _savedEmbedded = null;
            _embedded = false;
            if (entry.Type == PrefType.String && PrefJson.TryExpandEmbedded((string)entry.Value, out var pretty))
            {
                _savedEmbedded = pretty;
                _embeddedIndented = ((string)entry.Value).IndexOf('\n') >= 0;
                _embedded = _preferEmbedded && !isNew;
            }

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
            _isNew = _renaming = _embedded = false;
            _externalNote = null;
            UpdatePanes();
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
            return PrefJson.TryParseValue(_value.value, EditorType, out value, out error);
        }

        void UpdateDetail()
        {
            if (_editing == null) return;
            bool keyEditable = _isNew || _renaming;
            var key = _key.value ?? "";

            string error = null, warning = _externalNote;
            if (keyEditable && key.Length == 0) error = "Enter a key name.";
            else if (!_isNew && _editing.Type == PrefType.Unknown && EditorType == PrefType.Unknown)
                error = "The type of this key could not be detected. Pick a type and enter a value to overwrite it.";
            else TryGetEditorValue(out _, out error);
            if (keyEditable && key.Length > 0 && key.Trim() != key)
                warning = (warning == null ? "" : warning + "\n") + "The key name starts or ends with a space.";

            // Offer "Edit as JSON" for strings whose content is a JSON object or array.
            bool canEmbed = _embedded || EditorType == PrefType.String &&
                            PrefJson.TryParseValue(_value.value, PrefType.String, out var s, out _) && PrefJson.TryExpandEmbedded((string)s, out _);
            _embedToggle.style.display = canEmbed ? DisplayStyle.Flex : DisplayStyle.None;

            if (EditorType == PrefType.String && error == null && TryGetEditorValue(out var str, out _))
                _info.text = $"{((string)str).Length:N0} characters";
            else _info.text = "";

            bool dirty = IsDirty;
            _key.isReadOnly = !keyEditable;
            _key.tooltip = keyEditable ? "" : "Use Rename to change the key name.";
            _rename.style.display = keyEditable ? DisplayStyle.None : DisplayStyle.Flex;
            _dirty.style.display = dirty ? DisplayStyle.Flex : DisplayStyle.None;
            _dirty.text = _isNew ? "● new key, not saved yet" : _renaming && key != _editing.Key ? "● unsaved changes (rename)" : "● unsaved changes";
            _warning.text = warning ?? "";
            _warning.style.display = warning == null ? DisplayStyle.None : DisplayStyle.Flex;
            _error.text = error ?? "";
            _error.tooltip = error ?? "";
            _error.style.display = error == null ? DisplayStyle.None : DisplayStyle.Flex;

            _save.SetEnabled(dirty && error == null);
            _revert.SetEnabled(dirty);
            _revert.text = _isNew ? "Discard" : "Revert";
            _copy.SetEnabled(error == null && key.Length > 0);
            _format.SetEnabled(error == null);
            foreach (var b in new[] { _pin, _duplicate, _delete })
                b.style.display = _isNew ? DisplayStyle.None : DisplayStyle.Flex;
            if (!_isNew) _pin.text = _pinned.Contains(_editing.Key) ? "Unpin" : "Pin";
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
            // Carry the value over when it survives the change (5 → 5.0 → "5"); otherwise leave it for the user to fix.
            if (PrefJson.TryConvertText(_value.value, from, to, out var converted)) _value.SetValueWithoutNotify(converted);
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
                    SetStatus(error ?? "The string does not hold a JSON object or array.", true);
                    return;
                }
                if (_savedEmbedded == null) _embeddedIndented = ((string)s).IndexOf('\n') >= 0;
                _value.SetValueWithoutNotify(pretty);
            }
            else
            {
                if (!PrefJson.TryCollapseEmbedded(_value.value, _embeddedIndented, out var s, out var error))
                {
                    _embedToggle.SetValueWithoutNotify(true);
                    SetStatus("Fix the JSON before switching back: " + error, true);
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
            if (!TryGetEditorValue(out var value, out var error))
            {
                SetStatus(error, true);
                return;
            }
            _value.value = _embedded && PrefJson.TryExpandEmbedded((string)value, out var formatted) ? formatted : PrefJson.FormatValue(EditorType, value);
        }

        bool ConfirmDiscard()
        {
            if (!IsDirty) return true;
            var name = string.IsNullOrEmpty(_key.value) ? "the new key" : $"\"{_key.value}\"";
            return EditorUtility.DisplayDialog("Discard changes?", $"Discard unsaved changes to {name}?", "Discard", "Keep Editing");
        }

        // ---------- actions ----------

        void Commit(string label, IReadOnlyCollection<PrefEntry> writes, IReadOnlyCollection<string> deletes)
        {
            _committing = true;
            try
            {
                PrefHistory.instance.Commit(label, writes, deletes);
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
            SetStatus($"Duplicating \"{source.Key}\". Edit the name and value, then Save.");
        }

        void StartRename()
        {
            if (_editing == null || _isNew) return;
            _renaming = true;
            UpdateDetail();
            _key.Focus();
            SetStatus("Enter the new key name, then Save. Revert cancels.");
        }

        void TogglePin(string key) => SetPins(new[] { key }, !_pinned.Contains(key));

        void SetPins(IEnumerable<string> keys, bool pinned)
        {
            foreach (var k in keys) PlayerPrefStore.SetPinned(k, pinned);
            Reload();
        }

        void Save()
        {
            if (_editing == null || !IsDirty) return;
            if (!TryGetEditorValue(out var value, out var error))
            {
                SetStatus($"Not saved: {error}", true);
                return;
            }
            var type = EditorType;
            var key = _isNew || _renaming ? _key.value : _editing.Key;
            if (string.IsNullOrEmpty(key))
            {
                SetStatus("Not saved: enter a key name.", true);
                return;
            }
            string oldKey = _renaming && key != _editing.Key ? _editing.Key : null;

            if (_isNew && PlayerPrefs.HasKey(key))
            {
                int choice = EditorUtility.DisplayDialogComplex("Key already exists",
                    $"\"{key}\" already exists in PlayerPrefs.\n\nOpen its current value, or overwrite it with the value you entered?",
                    "Open Existing", "Cancel", "Overwrite");
                if (choice == 1) return;
                if (choice == 0)
                {
                    PlayerPrefStore.Track(new[] { key });
                    _isNew = false;
                    _editing = null;
                    Reload();
                    Show(PlayerPrefStore.Read(key), false);
                    SetStatus($"Opened existing key \"{key}\".");
                    return;
                }
            }
            if (oldKey != null && PlayerPrefs.HasKey(key) &&
                !EditorUtility.DisplayDialog("Key already exists", $"\"{key}\" already exists. Replace it with \"{oldKey}\"?", "Replace", "Cancel"))
                return;

            if (!_isNew && !Same(PlayerPrefStore.Read(_editing.Key), _editing))
            {
                int choice = EditorUtility.DisplayDialogComplex("Changed outside the editor",
                    $"\"{_editing.Key}\" changed after you opened it (for example in Play Mode).\n\nOverwrite it with your value?",
                    "Overwrite", "Cancel", "Load Current Value");
                if (choice == 1) return;
                if (choice == 2)
                {
                    var current = PlayerPrefStore.Read(_editing.Key);
                    if (current != null) Show(current, false);
                    else ClearEditor();
                    Reload();
                    return;
                }
            }

            var entry = new PrefEntry(key, type, value);
            try
            {
                Commit(oldKey != null ? $"Rename \"{oldKey}\" to \"{key}\"" : $"Save \"{key}\"",
                    new[] { entry }, oldKey != null ? new[] { oldKey } : Array.Empty<string>());
            }
            catch (Exception e)
            {
                SetStatus($"Save failed: {e.Message}", true);
                return;
            }
            if (oldKey != null && _pinned.Contains(oldKey))
            {
                PlayerPrefStore.SetPinned(oldKey, false);
                PlayerPrefStore.SetPinned(key, true);
            }
            Show(entry, false);
            Reload();
            SetStatus(oldKey != null ? $"Renamed \"{oldKey}\" to \"{key}\"." : $"Saved \"{key}\" ({PrefJson.TypeName(type)}).");
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
            var current = PlayerPrefStore.Read(_editing.Key);
            if (current != null) Show(current, false);
            else ClearEditor();
            SetStatus("Reverted.");
        }

        void DeleteKeys(List<string> keys, string description)
        {
            if (keys.Count == 0) return;
            var what = description ?? (keys.Count == 1 ? $"\"{keys[0]}\"" : $"{keys.Count} keys");
            var unknown = keys.Count(k => _all.Find(e => e.Key == k)?.Type == PrefType.Unknown);
            var message = $"Delete {what}?" + (keys.Count > 1 && description != null ? $" ({keys.Count} keys)" : "")
                          + (unknown > 0 ? $"\n\n{unknown} of them have an unknown type and cannot be restored with Undo." : "\n\nYou can restore them with Undo.");
            if (!EditorUtility.DisplayDialog("Delete PlayerPrefs", message, "Delete", "Cancel")) return;

            Commit(keys.Count == 1 ? $"Delete \"{keys[0]}\"" : $"Delete {keys.Count} keys", Array.Empty<PrefEntry>(), keys);
            if (_editing != null && keys.Contains(_editing.Key)) ClearEditor();
            _selection.Clear();
            Reload();
            SetStatus(keys.Count == 1 ? $"Deleted \"{keys[0]}\"." : $"Deleted {keys.Count} keys.");
        }

        void Undo()
        {
            var label = PrefHistory.instance.Undo();
            if (label == null) return;
            Reload();
            SetStatus($"Undid: {label}.");
        }

        void Redo()
        {
            var label = PrefHistory.instance.Redo();
            if (label == null) return;
            Reload();
            SetStatus($"Redid: {label}.");
        }

        void UpdateUndoButtons()
        {
            var h = PrefHistory.instance;
            _undo.SetEnabled(h.CanUndo);
            _undo.tooltip = h.CanUndo ? $"Undo: {h.UndoLabel}" : "Nothing to undo";
            _redo.SetEnabled(h.CanRedo);
            _redo.tooltip = h.CanRedo ? $"Redo: {h.RedoLabel}" : "Nothing to redo";
        }

        void CopyJson()
        {
            if (!TryGetEditorValue(out var value, out var error))
            {
                SetStatus(error, true);
                return;
            }
            var key = _key.value;
            EditorGUIUtility.systemCopyBuffer = PrefJson.FormatDocument(new[] { new PrefEntry(key, EditorType, value) });
            SetStatus($"Copied \"{key}\" as JSON.");
        }

        void CopyEntries(List<PrefEntry> entries, string what)
        {
            EditorGUIUtility.systemCopyBuffer = PlayerPrefStore.ExportJson(entries, out int exported, out int skipped);
            SetStatus($"Copied {what} ({exported} keys) as JSON{SkippedNote(skipped)}.");
        }

        void ExportToFile(List<PrefEntry> entries, string defaultName)
        {
            var path = EditorUtility.SaveFilePanel("Export PlayerPrefs", "", defaultName, "json");
            if (string.IsNullOrEmpty(path)) return;
            try
            {
                File.WriteAllText(path, PlayerPrefStore.ExportJson(entries, out int exported, out int skipped));
                SetStatus($"Exported {exported} keys to {path}{SkippedNote(skipped)}.");
            }
            catch (Exception e)
            {
                SetStatus($"Export failed: {e.Message}", true);
            }
        }

        static string SkippedNote(int skipped) => skipped > 0 ? $" ({skipped} of unknown type skipped)" : "";

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
                SetStatus($"Import failed: {e.Message}", true);
                return;
            }
            ImportText(json, Path.GetFileName(path));
        }

        void ImportText(string json, string source)
        {
            if (!PrefJson.TryParseDocument(json, out var entries, out var error))
            {
                SetStatus($"Import failed, nothing was written. {error}", true);
                EditorUtility.DisplayDialog("Import failed", $"{source} is not a valid PlayerPrefs document. Nothing was written.\n\n{error}", "OK");
                return;
            }
            if (entries.Count == 0)
            {
                SetStatus($"{source} has no keys to import.");
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

        void SetStatus(string message, bool isError = false)
        {
            _status.text = message;
            _status.tooltip = message;
            _status.style.color = isError ? new StyleColor(PrefStyles.Error) : new StyleColor(StyleKeyword.Null);
        }

        // ---------- shortcuts (active while this window has focus) ----------

        [Shortcut("kinatraa/Player Pref Editor/Save", typeof(PlayerPrefEditorWindow), KeyCode.S, ShortcutModifiers.Action)]
        static void SaveShortcut(ShortcutArguments args) => (args.context as PlayerPrefEditorWindow)?.Save();

        [Shortcut("kinatraa/Player Pref Editor/Refresh", typeof(PlayerPrefEditorWindow), KeyCode.F5)]
        static void RefreshShortcut(ShortcutArguments args) => (args.context as PlayerPrefEditorWindow)?.Reload();

        [Shortcut("kinatraa/Player Pref Editor/Find", typeof(PlayerPrefEditorWindow), KeyCode.F, ShortcutModifiers.Action)]
        static void FindShortcut(ShortcutArguments args) => (args.context as PlayerPrefEditorWindow)?._search.Focus();

        [Shortcut("kinatraa/Player Pref Editor/Add New", typeof(PlayerPrefEditorWindow), KeyCode.N, ShortcutModifiers.Action)]
        static void AddShortcut(ShortcutArguments args) => (args.context as PlayerPrefEditorWindow)?.AddNew();
    }
}
