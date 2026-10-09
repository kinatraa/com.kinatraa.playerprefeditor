# Getting Started

1. Install the package from `https://github.com/kinatraa/com.kinatraa.playerprefeditor.git#0.4.1` (see the [README](../README.md#install)).
2. Check **Project Settings ▸ Player ▸ Company Name / Product Name**. PlayerPrefs are stored per company and product.
3. Open **Tools ▸ kinatraa ▸ Player Pref Editor**.

## Common tasks

| Task | Steps |
|---|---|
| Find a key | Type in the search field (it searches values too). **All Types ▾** filters by type and sets regex and sort options. |
| Edit a value | Select the key, edit the JSON value, click **Save** (Ctrl/Cmd+S). **Revert** goes back to the stored value. |
| Change a key's type | Pick another type. The value is converted when it can be; otherwise enter a new one. **Save**. |
| Add a key | **+** in the toolbar (Ctrl/Cmd+N), enter a name, pick a type, enter the value, **Save**. |
| Rename a key | **F2**, double-click the key name, or **⋮ ▸ Rename**. Type the new name, **Save**. **Esc** cancels. |
| Duplicate, pin or copy a key | **⋮** next to the key name, or right-click it in the list. |
| Edit JSON stored in a string | Tick **Edit as JSON**, edit, **Save**. It is stored back as a string. |
| Delete keys | Select one or more, press **Delete** (or right-click ▸ **Delete…**), confirm. |
| Undo a mistake | **Undo** in the toolbar. Works for saves, renames, deletes and imports until Unity closes. |
| Back up values | **Export ▾** all, visible or selected keys to a file or the clipboard. |
| Restore or seed values | **Import ▾** a file or the clipboard, or drop a `.json` file on the window. Review the preview, untick rows, choose Merge or Replace, **Import**. |
| A key changed while you were editing it | Use **Load Theirs** to take the new value, or **Keep Mine** to keep editing and replace it on Save. |
| Watch values in Play Mode | Keep **⋮ ▸ Auto Refresh in Play Mode** (window tab menu) on. |
| Make the running game pick up an edit | Subscribe to `PlayerPrefEvents.Changed` and re-read the key (see the [README](../README.md#react-to-edits-in-play-mode)). |

See the [README](../README.md#json-format) for the JSON format, platform notes and what has been tested.
