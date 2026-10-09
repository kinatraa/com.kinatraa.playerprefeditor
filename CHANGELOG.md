# Changelog

All notable changes to this package are documented in this file.
The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [0.3.0] - 2026-10-09

### Added
- `PlayerPrefEvents.Changed` (new runtime assembly `kinatraa.PlayerPrefEditor`): raised once per key after the package saves a change (Save, Rename, Delete, Import, Delete All, Undo, Redo), so game code can re-read cached values in Play Mode. Handlers that throw are logged without affecting the change. Handlers are cleared on entering Play Mode. In builds the event never fires.

## [0.2.0] - 2026-10-09

### Added
- Native key listing on macOS (preferences plist) and Linux (prefs file), alongside the Windows registry. Types come from the OS store when detection is ambiguous.
- Undo and redo for every save, rename, delete, import and Delete All (`PrefHistory`), lasting for the Editor session.
- Import preview window: per-key added/changed/unchanged/removed rows with before → after values, row checkboxes, Merge or Replace mode. Import from the clipboard or by dropping a `.json` file on the window.
- Value preview column, multi-select with bulk copy/export/delete, pinned keys, a right-click menu, and Delete / Ctrl+C in the list.
- Search over values, regex search, sort options, and hiding of Unity's internal keys.
- Rename and Duplicate. Changing the type converts the value when it survives (`5` → `5.0` → `"5"`).
- **Edit as JSON** for strings that hold a JSON object or array.
- Warning when the open key changes outside the editor, and a confirmation before overwriting it.
- Auto-refresh in Play Mode, Export of visible or selected keys, Delete All, Show Storage Location.
- Shortcuts: Ctrl/Cmd+S, Ctrl/Cmd+F, Ctrl/Cmd+N, F5.
- `NaN`, `Infinity` and `-Infinity` floats round-trip as JSON strings.

### Changed
- The layout works down to 460 × 280 without overlapping: the toolbar groups actions into menus, buttons wrap, long text ends in "…", and the list/editor divider keeps your width when there is room.
- Writes are validated before anything changes, so an invalid entry never leaves a half-applied import.
- `PlayerPrefStore.Read` returns null for a missing key.

### Removed
- `PlayerPrefStore.Preview`; use `PrefDiff.Compute`.

## [0.1.0] - 2026-10-08

### Added
- **Tools ▸ kinatraa ▸ Player Pref Editor** window (UI Toolkit) with live search, type filter, alphabetical key list with type badges and a visible/total count.
- Detail panel with a monospace JSON value field, live validation, an unsaved-changes marker and Save, Revert, Copy JSON and Delete.
- Import with validation and an added/changed/unchanged preview before anything is written; Export and Copy All as formatted JSON.
- Key enumeration from the registry on Windows, and a per-project tracked key list (EditorPrefs) on every platform.
- Best-effort type detection (int, float, string, unknown).
- EditMode tests for JSON round-trips, type validation, invalid input and PlayerPrefs read/write.
- Basic Usage sample with an importable JSON document.

[0.3.0]: https://github.com/kinatraa/com.kinatraa.playerprefeditor/releases/tag/0.3.0
[0.2.0]: https://github.com/kinatraa/com.kinatraa.playerprefeditor/releases/tag/0.2.0
[0.1.0]: https://github.com/kinatraa/com.kinatraa.playerprefeditor/releases/tag/0.1.0
