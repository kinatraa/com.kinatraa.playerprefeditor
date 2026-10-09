using System;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace kinatraa.PlayerPrefEditor
{
    /// <summary>
    /// UI helpers shared by the package's windows. Styling lives in PlayerPrefEditorWindow.uss; this only builds elements
    /// and picks classes. A theme switch reloads scripts and rebuilds the windows, so the theme class is set once.
    /// </summary>
    static class PrefStyles
    {
        const string SheetPath = "Packages/com.kinatraa.playerprefeditor/Editor/PlayerPrefEditorWindow.uss";

        public enum Severity { Info, Warning, Error }

        /// <summary>Adds the package style sheet and the theme class to a window root.</summary>
        public static void ApplyTheme(VisualElement root)
        {
            var sheet = AssetDatabase.LoadAssetAtPath<StyleSheet>(SheetPath);
            if (sheet != null) root.styleSheets.Add(sheet);
            else Debug.LogError("kinatraa PlayerPref Editor: style sheet missing at " + SheetPath + ". Reimport the package.");
            root.AddToClassList("ppe-root");
            root.AddToClassList(EditorGUIUtility.isProSkin ? "ppe-dark" : "ppe-light");
        }

        public static T Classes<T>(this T element, params string[] classes) where T : VisualElement
        {
            foreach (var c in classes) element.AddToClassList(c);
            return element;
        }

        public static Label Text(string text, params string[] classes) => new Label(text).Classes(classes);

        public static VisualElement Box(params string[] classes) => new VisualElement().Classes(classes);

        public static VisualElement Row(params VisualElement[] children)
        {
            var row = Box("ppe-row");
            foreach (var c in children) row.Add(c);
            return row;
        }

        public static VisualElement Spacer() => Box("ppe-grow");

        /// <summary>Single line that ends in "…" instead of overflowing.</summary>
        public static T Ellipsis<T>(this T element) where T : VisualElement => element.Classes("ppe-ellipsis");

        public static Texture2D FindIcon(string name)
        {
            var texture = EditorGUIUtility.isProSkin ? EditorGUIUtility.FindTexture("d_" + name) : null;
            return texture != null ? texture : EditorGUIUtility.FindTexture(name);
        }

        public static VisualElement Icon(string name, string cls = "ppe-icon")
        {
            var icon = Box(cls);
            var texture = FindIcon(name);
            if (texture != null) icon.style.backgroundImage = texture;
            return icon;
        }

        public static string SeverityIcon(Severity severity) =>
            severity == Severity.Error ? "console.erroricon.sml" : severity == Severity.Warning ? "console.warnicon.sml" : "console.infoicon.sml";

        /// <summary>A toolbar button showing a built-in icon, or <paramref name="fallback"/> text when the icon is missing.</summary>
        public static UnityEditor.UIElements.ToolbarButton IconButton(string icon, string fallback, string tooltip, Action click)
        {
            var button = new UnityEditor.UIElements.ToolbarButton(click) { tooltip = tooltip };
            var texture = FindIcon(icon);
            if (texture == null)
            {
                button.text = fallback;
                return button;
            }
            button.Add(new Image { image = texture, scaleMode = ScaleMode.ScaleToFit, style = { width = 16, height = 16, alignSelf = Align.Center } });
            button.AddToClassList("ppe-toolbar-icon");
            return button;
        }

        /// <summary>A borderless 16 px icon button, for overflow menus inside panels.</summary>
        public static Button SmallIconButton(string icon, string tooltip, Action click)
        {
            var button = new Button(click) { tooltip = tooltip }.Classes("ppe-icon-button");
            button.Add(Icon(icon));
            return button;
        }

        public static Label TypePill(PrefType type) => SetType(Text("", "ppe-pill"), type);

        public static Label SetType(Label pill, PrefType type)
        {
            foreach (PrefType t in Enum.GetValues(typeof(PrefType))) pill.RemoveFromClassList("ppe-type--" + PrefJson.TypeName(t));
            pill.AddToClassList("ppe-type--" + PrefJson.TypeName(type));
            pill.text = PrefJson.TypeName(type);
            return pill;
        }

        public static Label SetDiff(Label pill, DiffKind kind, string text)
        {
            foreach (DiffKind k in Enum.GetValues(typeof(DiffKind))) pill.RemoveFromClassList("ppe-diff--" + k.ToString().ToLowerInvariant());
            pill.AddToClassList("ppe-diff--" + kind.ToString().ToLowerInvariant());
            pill.text = text;
            return pill;
        }

        static Font _mono;

        public static Font Monospace
        {
            get
            {
                if (_mono != null) return _mono;
                _mono = EditorGUIUtility.Load("Fonts/RobotoMono/RobotoMono-Regular.ttf") as Font;
                if (_mono == null) _mono = Font.CreateDynamicFontFromOSFont(new[] { "Menlo", "Consolas", "DejaVu Sans Mono", "Courier New" }, 12);
                return _mono;
            }
        }

        /// <summary>Applies the monospace font to an element and everything inside it (text fields keep their font on an inner element).</summary>
        public static T Mono<T>(this T element) where T : VisualElement
        {
            var font = new StyleFontDefinition(Monospace);
            element.style.unityFontDefinition = font;
            element.Query<VisualElement>().ForEach(e => e.style.unityFontDefinition = font);
            return element;
        }
    }

    /// <summary>An inline message with an icon and optional actions; hidden when it has no text.</summary>
    sealed class PrefBanner : VisualElement
    {
        readonly VisualElement _icon;
        readonly Label _text;
        readonly VisualElement _actions;

        public PrefBanner()
        {
            AddToClassList("ppe-banner");
            Add(_icon = PrefStyles.Box("ppe-banner-icon"));
            Add(_text = PrefStyles.Text("", "ppe-banner-text"));
            Add(_actions = PrefStyles.Box("ppe-banner-actions"));
            style.display = DisplayStyle.None;
        }

        public string Message => _text.text;

        /// <summary>Shows <paramref name="message"/>, or hides the banner when it is null. Actions are (label, callback) pairs.</summary>
        public void Set(string message, PrefStyles.Severity severity = PrefStyles.Severity.Info, params (string label, Action click)[] actions)
        {
            style.display = message == null ? DisplayStyle.None : DisplayStyle.Flex;
            if (message == null) return;
            foreach (PrefStyles.Severity s in Enum.GetValues(typeof(PrefStyles.Severity))) RemoveFromClassList("ppe-banner--" + s.ToString().ToLowerInvariant());
            AddToClassList("ppe-banner--" + severity.ToString().ToLowerInvariant());
            var texture = PrefStyles.FindIcon(PrefStyles.SeverityIcon(severity));
            _icon.style.backgroundImage = texture != null ? new StyleBackground(texture) : new StyleBackground(StyleKeyword.None);
            _text.text = message;
            _text.tooltip = message;
            _actions.Clear();
            foreach (var (label, click) in actions) _actions.Add(new Button(click) { text = label });
            _actions.style.display = actions.Length > 0 ? DisplayStyle.Flex : DisplayStyle.None;
        }
    }
}
