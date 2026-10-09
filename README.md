# kinatraa PlayerPref Editor

An Editor window for Unity's `PlayerPrefs`. View, search, create, edit, rename, delete, import and export every value, and see, edit and exchange each one as formatted JSON (2-space indent). Every change can be undone.

> **Screenshot placeholder:** the Player Pref Editor window (toolbar, key list with value previews and type badges, JSON value panel, status line).

- **Every key, on every desktop Editor.** Keys are read from the OS store on Windows (registry), macOS (preferences plist) and Linux (prefs file), so values your game wrote show up too, with their real type.
- **Find keys fast.** Live search over keys *and* values (plain text or regex), a type filter, sorting, pinned keys at the top, and a value preview column. Unity's own internal keys are hidden unless you ask for them.
- **Edit as JSON.** Values sit in a monospace JSON field with live validation. A string that holds JSON can be edited as formatted JSON and is stored back as a string. Changing the type converts the value when it can (`5` → `5.0` → `"5"`).
- **Undo and redo** for every save, rename, delete, import and Delete All, for the whole Editor session.
- **Import preview.** See every key that would be added, changed or removed, with before → after values, pick which ones to apply, and choose Merge or Replace. Import from a file, the clipboard, or by dropping a `.json` file on the window.
- **Safe editing.** An `● unsaved changes` marker that survives script reloads and entering Play Mode, a Save / Discard prompt when you close the window with unsaved changes, Revert, confirmation before deleting, a warning when a key changes outside the editor while you edit it (for example in Play Mode), and live auto-refresh in Play Mode.
- **Bulk actions.** Multi-select to copy, export or delete many keys at once. Rename and duplicate single keys.
- **Live in Play Mode.** Game code can subscribe to `PlayerPrefEvents.Changed` to pick up values you edit while the game runs.
- **Editor tooling.** The window and all its code are Editor-only. The only thing that ships in builds is one tiny static event class, which never fires there. The tool never touches scenes or assets.

It depends only on [Newtonsoft JSON](https://docs.unity3d.com/Packages/com.unity.nuget.newtonsoft-json@3.2/manual/index.html) (`com.unity.nuget.newtonsoft-json`), which the Package Manager installs automatically.

## Install

**Package Manager (git URL)**: *Window ▸ Package Manager ▸ + ▸ Add package from git URL…*

```
https://github.com/kinatraa/com.kinatraa.playerprefeditor.git#0.3.1
```

or add it to `Packages/manifest.json`:

```json
"com.kinatraa.playerprefeditor": "https://github.com/kinatraa/com.kinatraa.playerprefeditor.git#0.3.1"
```

Requires Unity 2021.3 or newer.

## Open the window

**Tools ▸ kinatraa ▸ Player Pref Editor**

| Area | What it does |
|---|---|
| Toolbar | Search, filter menu (type, search values, regex, internal keys, sort), Refresh, Add, Undo, Redo, **Import ▾**, **Export ▾**, **More ▾** |
| Left panel | Keys with a value preview and a `[type]` badge, pinned keys first. Multi-select with Shift/Ctrl/Cmd. Right-click for Copy, Duplicate, Rename, Pin and Delete. |
| Right panel | Key, type, JSON value, **Save**, **Revert**, **Copy JSON**, **Pin**, **Duplicate**, **Delete**, or bulk actions when several keys are selected |
| Status line | The result of the last action or the error, and where keys are read from |

Drag the divider between the panels to resize the list. The window works down to 460 × 280: the list gives way first and the buttons wrap.

### Shortcuts (while the window has focus)

| Keys | Action |
|---|---|
| Ctrl/Cmd+S | Save |
| Ctrl/Cmd+F | Focus search |
| Ctrl/Cmd+N | Add a new key |
| F5 | Refresh |
| Delete, or Cmd+Backspace (in the list) | Delete the selected keys |
| Ctrl/Cmd+C (in the list) | Copy the selected keys as JSON |

Rebind them under *Edit ▸ Shortcuts ▸ kinatraa*.

## JSON format

Export, Import, Copy and Copy JSON all use one schema: an object that maps each key to its type and value.

```json
{
  "player_name": { "type": "string", "value": "Hakien" },
  "master_volume": { "type": "float", "value": 0.8 },
  "level": { "type": "int", "value": 5 }
}
```

| `type` | `value` must be | Example |
|---|---|---|
| `int` | a whole number from -2147483648 to 2147483647 (`5.0` is rejected) | `5` |
| `float` | a number, or `"NaN"`, `"Infinity"`, `"-Infinity"` | `0.8` |
| `string` | a JSON string in double quotes | `"Hakien"` |

The value field in the right panel holds only the value (`0.8`, `"Hakien"`); the type comes from the Type dropdown.

**Strings that hold JSON.** When a string's content is a JSON object or array, **Edit as JSON** shows it formatted. Saving stores it back as a string: compact if the original was on one line, indented if it was multi-line. Untick it to edit the raw JSON string. Numbers inside are re-serialized, so `1.50` is stored as `1.5`.

Validation rules:

- Saving parses the JSON first. A parse error is shown with its line and position, and nothing is written.
- An import is checked in full before the preview opens. One invalid entry rejects the whole document, and duplicate keys are rejected too.
- Parsing uses Newtonsoft, which also accepts a few non-standard forms such as single-quoted strings and comments. Everything the tool writes is standard JSON.

## Import and export

- **Import ▾ From File…**, **From Clipboard**, or drop a `.json` file on the window. A preview lists every key as added, changed, unchanged or removed, with its old and new value. Untick rows you don't want, then click **Import N Changes**.
  - **Merge** adds and updates keys.
  - **Replace** also deletes keys that are missing from the import. It never deletes Unity's internal keys.
- **Export ▾** writes all keys, the keys matching the current search and filter, or the selected keys to a file, or copies them to the clipboard.
- **More ▾ Delete All Keys…** deletes every listed key (internal keys only when they are shown). Like everything else, it can be undone.

Keys of unknown type can't be exported or restored by Undo, because their value can't be read. The status line says how many were skipped.

## Platform support and known limitations

Unity has no API that lists PlayerPrefs keys, so keys are read from where the Editor stores them:

| Editor platform | Location |
|---|---|
| Windows | Registry: `HKCU\Software\Unity\UnityEditor\<companyName>\<productName>` (builds use `HKCU\Software\<companyName>\<productName>`) |
| macOS | `~/Library/Preferences/unity.<companyName>.<productName>.plist`, read through `defaults` so it is always current |
| Linux | `~/.config/unity3d/<companyName>/<productName>/prefs` |

`companyName` and `productName` come from **Project Settings ▸ Player**. If you change them, the Editor uses a new PlayerPrefs location. **More ▾ Show Storage Location** reveals the file (macOS, Linux) and copies the path.

Every key written through this tool is also remembered in a per-project list in `EditorPrefs`. That covers keys the OS store doesn't list yet, for example values set in Play Mode before `PlayerPrefs.Save()` runs (Linux only writes the file on save). If the OS store can't be read, the window falls back to that list and says so in the status line. To show a key that isn't listed, click **Add New**, enter its name and **Save**, then choose **Open Existing**.

**Type detection is best-effort.** PlayerPrefs has no API that returns a key's type, so each key is read with two different defaults per getter (`GetInt`, `GetFloat`, `GetString`) to see which one holds a value, and the OS store's type is used when that is ambiguous. If neither gives an answer, the key is shown as `[unknown]`: pick a type and enter a value to overwrite it.

Tested on macOS with Unity 6. The Windows registry and Linux prefs readers are covered by parser tests but have not been run on those platforms yet.

## React to edits in Play Mode

Saving in the window writes straight into the PlayerPrefs your game reads, so the next `PlayerPrefs.GetInt` call returns the new value. Code that read a value once and cached it won't notice on its own. Subscribe to `PlayerPrefEvents.Changed` to re-read it:

```csharp
using kinatraa.PlayerPrefEditor;
using UnityEngine;

public class Settings : MonoBehaviour
{
    float _volume;

    void OnEnable() => PlayerPrefEvents.Changed += OnPrefChanged;
    void OnDisable() => PlayerPrefEvents.Changed -= OnPrefChanged;
    void Start() => _volume = PlayerPrefs.GetFloat("master_volume", 1f);

    void OnPrefChanged(string key)
    {
        if (key == "master_volume") _volume = PlayerPrefs.GetFloat("master_volume", 1f);
    }
}
```

- It fires once per changed key after the change is saved: Save, Rename (old and new key), Delete, Import, Delete All, Undo and Redo. A deleted key fires too, so always read with a default.
- It runs on the main thread, in Edit Mode as well as Play Mode. A handler that throws is logged and does not affect the change or other handlers.
- It only fires for changes made through this package, not for `PlayerPrefs.Set*` calls in your own code.
- Handlers are cleared when Play Mode starts, including with domain reload disabled, so subscribe in `OnEnable` or `Start`.
- In builds the event exists but never fires, so you can leave the subscription in shipping code.

## Scripting

The window is built on public Editor classes you can call from your own Editor scripts:

```csharp
using kinatraa.PlayerPrefEditor;

// Undoable from the window's Undo button.
PrefHistory.instance.Commit("Reset progress",
    new[] { new PrefEntry("level", PrefType.Int, 1) },   // writes
    new[] { "checkpoint" });                              // deletes

PrefEntry e = PlayerPrefStore.Read("level");              // e.Type == PrefType.Int, e.Value == 1
List<string> keys = PlayerPrefStore.GetKeys();
string json = PlayerPrefStore.ExportJson(out int exported, out int skipped);

if (PrefJson.TryParseDocument(json, out var entries, out var error))
    ImportPreviewWindow.Open("backup.json", entries, message => Debug.Log(message));

PlayerPrefEditorWindow.Open();
```

`PlayerPrefStore.Write`, `Apply`, `Delete` and `Commit` change PlayerPrefs directly, without undo.

## Samples

Import **Basic Usage** from the package's page in the Package Manager. It contains `sample-playerprefs.json` to try Import with.

## License

[MIT](LICENSE) © kinatraa
