# Changelog

All notable changes to this package are documented in this file.
The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [0.4.0] - 2026-10-09

### Changed
- Redesigned window. The key is a title rather than a read-only field, rarely used actions live in a ⋮ menu next to it, and the footer uses Unity's Revert / Save pattern with Save as the one primary button. Type pills replace `[int]` text badges, and pinned keys show a star.
- Settings and rarely used actions (Auto Refresh, Show Internal Keys, Delete All, Clear Undo History, Show Storage Location) moved to the window's tab menu.
- Errors, warnings and notices appear as inline banners with icons; the status bar shows an icon for warnings and errors and flags an unreadable OS store.
- Empty states for an empty store, no search results and nothing selected, each with the relevant action.
- The import preview matches the main window: a Merge / Replace switch with an explanation, colored summary pills, warnings, a table with column headers, rows grouped by change kind, and an empty state.
- Styling moved to a shared USS file with theme-specific colors. Pills are opaque, and previews dim by opacity, so both stay readable on any selection color in the Dark and Light themes.
- Parse errors lead with the line and position. Text typed for a string without quotes gets a hint about JSON quotes.
- Shorter footer states (● Unsaved, ● New key, ● Renamed), no red error for a new key that has no name yet, and "Converted from int" feedback when changing the type.

### Added
- **Load Theirs / Keep Mine** when the open key changes outside the editor while it has unsaved edits.
- F2 and double-click to rename, Esc to cancel a rename, Enter in the list to jump to the value.
- Confirmation before an import deletes keys (Replace mode).
- `PrefHistory.LastUnrestorable`, `NativePrefs.ReadKeysAsync`, `NativePrefs.Snapshot`, and `PlayerPrefStore.GetKeys` / `ReadAll` overloads that take a snapshot.

### Fixed
- Auto Refresh in Play Mode blocked the Editor for about 17 ms each second on macOS (the `defaults` process). The OS store is now read on a worker thread.
- When the type probe was ambiguous, the OS store's type was trusted even if that getter returned nothing, which could show a float as an empty string and overwrite it on Save. The hint is now used only if its getter answered.
- Saving over a key of unknown type replaced it without warning; it now asks first. Undo and Redo report keys they could not restore instead of silently skipping them.
- A damaged undo step could still apply its deletes, and a step that failed was dropped. Both now leave everything unchanged and keep the step.
- The import preview could apply a diff computed before PlayerPrefs changed, and closed itself on script reload. It now re-checks before applying and survives reloads.
- A 1.2 MB JSON string took 1.3 s to open and 400 ms per keystroke. Large values now open in 35 ms and are validated when typing pauses.
- `Documentation~/GettingStarted.md` was empty in 0.3.1.
- Linux: both `~/.config/unity3d` and `~/.local/share/unity3d` prefs files are read. macOS: `defaults` output is decoded as UTF-8.

## [0.3.1] - 2026-10-09

### Fixed
- Unsaved edits were lost when scripts reloaded, for example when entering Play Mode. The open edit, including a new key, a rename or **Edit as JSON** mode, now survives the reload and is still marked unsaved.

### Added
- The window tab shows when there are unsaved changes, and closing the window or quitting Unity asks to Save or Discard them.

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

[0.4.0]: https://github.com/kinatraa/com.kinatraa.playerprefeditor/releases/tag/0.4.0
[0.3.1]: https://github.com/kinatraa/com.kinatraa.playerprefeditor/releases/tag/0.3.1
[0.3.0]: https://github.com/kinatraa/com.kinatraa.playerprefeditor/releases/tag/0.3.0
[0.2.0]: https://github.com/kinatraa/com.kinatraa.playerprefeditor/releases/tag/0.2.0
[0.1.0]: https://github.com/kinatraa/com.kinatraa.playerprefeditor/releases/tag/0.1.0
