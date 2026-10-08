# kinatraa PlayerPref Editor

An Editor window for Unity's `PlayerPrefs`. View, search, create, edit, delete, import and export every value, and see, edit and exchange each one as formatted JSON (2-space indent).

> **Screenshot placeholder:** the Player Pref Editor window (toolbar, key list with type badges, JSON value panel, status line).

- **Find keys fast.** Live search, a type filter (All / int / float / string / unknown), and an alphabetical list with a `[type]` badge on every key.
- **Edit as JSON.** Values sit in a monospace JSON field. Invalid JSON and type mismatches show a red error line and nothing is written.
- **Safe changes.** An `● unsaved changes` marker, Revert, and confirmation dialogs before Delete and Import.
- **Import / Export.** Export the whole store to a file or the clipboard. Import validates the file and previews how many keys are added, changed and unchanged before applying anything.
- **Editor-only.** All code is in an Editor assembly, so nothing ships in builds. The tool never touches scenes or assets.

It depends only on [Newtonsoft JSON](https://docs.unity3d.com/Packages/com.unity.nuget.newtonsoft-json@3.2/manual/index.html) (`com.unity.nuget.newtonsoft-json`), which the Package Manager installs automatically.

## Install

**Package Manager (git URL)**: *Window ▸ Package Manager ▸ + ▸ Add package from git URL…*

```
https://github.com/kinatraa/com.kinatraa.playerprefeditor.git#0.1.0
```

or add it to `Packages/manifest.json`:

```json
"com.kinatraa.playerprefeditor": "https://github.com/kinatraa/com.kinatraa.playerprefeditor.git#0.1.0"
```

Requires Unity 2021.3 or newer.

## Open the window

**Tools ▸ kinatraa ▸ Player Pref Editor**

| Area | What it does |
|---|---|
| Toolbar | Search, type filter, **Refresh**, **Add New**, **Import**, **Export**, **Copy All** |
| Left panel | Keys sorted alphabetically with a `[type]` badge, and `Keys: visible / total` |
| Right panel | Key (read-only for existing keys), type, JSON value, **Save**, **Revert**, **Copy JSON**, **Delete** |
| Status line | The result of the last action, or the error |

The list reloads when the window gets focus, so values written in Play Mode show up when you come back to it.

## JSON format

Export, Import, Copy All and Copy JSON all use one schema: an object that maps each key to its type and value.

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
| `float` | any number | `0.8` |
| `string` | a JSON string in double quotes | `"Hakien"` |

The value field in the right panel holds only the value (`0.8`, `"Hakien"`), and the type comes from the Type dropdown. A string that contains JSON stays a string: `"{\"lang\":\"en\"}"`.

Validation rules:

- Saving parses the JSON first. A parse error is shown with its line and position, and nothing is written.
- An Import file is checked in full before anything is written. One invalid entry rejects the whole file. Duplicate keys are rejected too.
- Parsing uses Newtonsoft, which also accepts a few non-standard forms such as single-quoted strings and comments. Everything the tool writes is standard JSON.

## Platform support and known limitations

Unity has no API that lists PlayerPrefs keys, so how keys are found depends on the platform.

| Editor platform | How keys are listed |
|---|---|
| Windows | Read from the registry under `HKCU\Software\Unity\UnityEditor\<companyName>\<productName>` (the Editor's PlayerPrefs location; builds use `HKCU\Software\<companyName>\<productName>`). All keys appear, including ones written by your game or by Unity. |
| macOS, Linux | Native enumeration is not implemented. The window lists a per-project key list stored in `EditorPrefs`. |

On macOS and Linux:

- Every key written through the tool (Save, Import, or the `PlayerPrefStore` API) is added to the list automatically.
- To show a key your game wrote, click **Add New**, type its name and click **Save**, then choose **Load Existing**.
- Keys that no longer exist in PlayerPrefs drop off the list on the next refresh.

`companyName` and `productName` come from **Project Settings ▸ Player**. If you change them, PlayerPrefs (and the tracked key list) move to a new location.

**Type detection is best-effort.** PlayerPrefs does not store a type you can query, so the tool reads each key with two different defaults per getter (`GetInt`, `GetFloat`, `GetString`) to see which one holds a value. If that does not give exactly one type, the key is shown as `[unknown]`. Its value can't be read then, so pick a type and enter a value to overwrite it. Keys of unknown type are left out of Export and Copy All, and the status line says how many were skipped.

## Scripting

The window is built on two public Editor classes you can call from your own Editor scripts:

```csharp
using kinatraa.PlayerPrefEditor;

PlayerPrefStore.Write(new PrefEntry("level", PrefType.Int, 5));   // set, Save(), track the key
PrefEntry e = PlayerPrefStore.Read("level");                      // e.Type == PrefType.Int, e.Value == 5
string json = PlayerPrefStore.ExportJson(out int exported, out int skipped);
if (PrefJson.TryParseDocument(json, out var entries, out var error))
    PlayerPrefStore.Apply(entries);                                // write all, then one Save()
PlayerPrefEditorWindow.Open();
```

## Samples

Import **Basic Usage** from the package's page in the Package Manager. It contains `sample-playerprefs.json` to try Import with.

## License

[MIT](LICENSE) © kinatraa
