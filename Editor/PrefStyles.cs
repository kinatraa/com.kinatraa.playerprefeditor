using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace kinatraa.PlayerPrefEditor
{
    /// <summary>Theme-aware colors and small UI helpers. A theme switch reloads scripts, so reading isProSkin once per build is enough.</summary>
    static class PrefStyles
    {
        static bool Dark => EditorGUIUtility.isProSkin;

        public static Color Error => Dark ? new Color(1f, 0.45f, 0.45f) : new Color(0.75f, 0.1f, 0.1f);
        public static Color Warning => Dark ? new Color(1f, 0.8f, 0.3f) : new Color(0.65f, 0.4f, 0f);
        public static Color Dim => Dark ? new Color(0.62f, 0.62f, 0.62f) : new Color(0.38f, 0.38f, 0.38f);
        public static Color Added => Dark ? new Color(0.55f, 0.9f, 0.55f) : new Color(0.1f, 0.5f, 0.1f);
        public static Color Removed => Error;
        public static Color Changed => Warning;

        public static Color TypeColor(PrefType type)
        {
            switch (type)
            {
                case PrefType.Int: return Dark ? new Color(0.45f, 0.75f, 1f) : new Color(0.05f, 0.35f, 0.7f);
                case PrefType.Float: return Dark ? new Color(0.55f, 0.9f, 0.55f) : new Color(0.1f, 0.5f, 0.1f);
                case PrefType.String: return Dark ? new Color(0.95f, 0.75f, 0.45f) : new Color(0.6f, 0.35f, 0f);
                default: return Dim;
            }
        }

        public static Color DiffColor(DiffKind kind)
        {
            switch (kind)
            {
                case DiffKind.Added: return Added;
                case DiffKind.Changed: return Changed;
                case DiffKind.Removed: return Removed;
                default: return Dim;
            }
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

        /// <summary>Single line that ends in "…" instead of overflowing.</summary>
        public static T Ellipsis<T>(this T label) where T : VisualElement
        {
            label.style.whiteSpace = WhiteSpace.NoWrap;
            label.style.overflow = Overflow.Hidden;
            label.style.textOverflow = TextOverflow.Ellipsis;
            label.style.minWidth = 0;
            label.style.flexShrink = 1;
            return label;
        }

        public static VisualElement Row(params VisualElement[] children)
        {
            var row = new VisualElement { style = { flexDirection = FlexDirection.Row, alignItems = Align.Center, flexShrink = 0 } };
            foreach (var c in children) row.Add(c);
            return row;
        }

        public static VisualElement Spacer() => new VisualElement { style = { flexGrow = 1, flexShrink = 1 } };

        /// <summary>A toolbar button showing a built-in icon, or <paramref name="fallback"/> text when the icon is missing.</summary>
        public static UnityEditor.UIElements.ToolbarButton IconButton(string icon, string fallback, string tooltip, System.Action click)
        {
            var button = new UnityEditor.UIElements.ToolbarButton(click) { tooltip = tooltip };
            var texture = Dark ? EditorGUIUtility.FindTexture("d_" + icon) : null;
            if (texture == null) texture = EditorGUIUtility.FindTexture(icon);
            if (texture == null)
            {
                button.text = fallback;
                return button;
            }
            button.Add(new Image { image = texture, scaleMode = ScaleMode.ScaleToFit, style = { width = 16, height = 16, alignSelf = Align.Center } });
            button.style.paddingLeft = button.style.paddingRight = 3;
            return button;
        }
    }
}
