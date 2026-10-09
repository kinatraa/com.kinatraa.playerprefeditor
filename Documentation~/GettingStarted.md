# Getting Started

1. Install the package from `https://github.com/kinatraa/com.kinatraa.playerprefeditor.git#0.3.0` (see the [README](../README.md#install)).
2. Check **Project Settings ▸ Player ▸ Company Name / Product Name**. PlayerPrefs are stored per company and product.
3. Open **Tools ▸ kinatraa ▸ Player Pref Editor**.

## Common tasks

| Task | Steps |
|---|---|
| Find a key | Type in the search field, or pick a type in the filter menu next to it. |
| Edit a value | Select the key, edit the JSON value, click **Save**. **Revert** restores the saved value. |
| Change a key's type | Pick another type (the value is converted when it can be), adjust it, click **Save**. |
| Add a key | **Add New**, enter a name, pick a type, enter the value, **Save**. |
| Search values too | Type in the search field; the filter menu toggles value search and regex. |
| Rename or duplicate a key | **Rename** next to the key, or right-click ▸ **Duplicate**. |
| Edit JSON stored in a string | Tick **Edit as JSON**, edit, **Save**. |
| Keep a key at the top | **Pin**. |
| Delete keys | Select one or more, **Delete** (or the Delete key), confirm. |
| Undo a mistake | **Undo** in the toolbar. Works for saves, renames, deletes and imports. |
| Back up values | **Export ▾** all, visible or selected keys to a file or the clipboard. |
| Restore or seed values | **Import ▾** a file or the clipboard, or drop a `.json` file on the window. Review the preview, untick rows, choose Merge or Replace, apply. |
| Watch values in Play Mode | Leave **More ▾ Auto Refresh in Play Mode** on. |
| Make the running game pick up an edit | Subscribe to `PlayerPrefEvents.Changed` and re-read the key (see the [README](../README.md#react-to-edits-in-play-mode)). |

See the [README](../README.md#json-format) for the JSON format and platform limitations.
