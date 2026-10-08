# Getting Started

1. Install the package from `https://github.com/kinatraa/com.kinatraa.playerprefeditor.git#0.1.0` (see the [README](../README.md#install)).
2. Check **Project Settings ▸ Player ▸ Company Name / Product Name**. PlayerPrefs are stored per company and product.
3. Open **Tools ▸ kinatraa ▸ Player Pref Editor**.

## Common tasks

| Task | Steps |
|---|---|
| Find a key | Type in the search field, or pick a type in the **Type** menu. |
| Edit a value | Select the key, edit the JSON value, click **Save**. **Revert** restores the saved value. |
| Change a key's type | Pick another type, enter a matching value, click **Save**. |
| Add a key | **Add New**, enter a name, pick a type, enter the value, **Save**. |
| Show a key your game wrote (macOS/Linux) | **Add New**, enter its name, **Save**, then **Load Existing**. |
| Delete a key | Select it, **Delete**, confirm. |
| Back up all values | **Export** to a file, or **Copy All** to the clipboard. |
| Restore or seed values | **Import** a file, check the added/changed/unchanged counts, confirm. |

See the [README](../README.md#json-format) for the JSON format and platform limitations.
