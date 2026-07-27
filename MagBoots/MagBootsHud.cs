using UnityEngine;

namespace MagBoots
{
    // Draws a HUD hint approximating the game's own button-prompt style (e.g. "SCANNER [T]",
    // "HELMET LIGHT [5]"): a right-aligned uppercase label followed by a small bordered key chip.
    // Built with OnGUI rather than the real ContextualButtonPrompt/Doozy UI, since that class is
    // wired to a closed set of bound GameplayActionSet actions and baked control-sprite lookups,
    // which our config-file-bound hotkey isn't part of.
    internal static class MagBootsHud
    {
        // Base sizes at Scale = 1, which is twice the mod's original (pre-1.2) hint size.
        private const float BaseScaleMultiplier = 2f;
        private const int PromptHeight = 34;
        private const int KeyChipMinWidth = 34;
        private const int Padding = 10;
        private const int RightMargin = 30;
        private const int BottomMargin = 30;
        private const int BatterySegments = 5;
        private const int LabelFontSize = 16;
        private const int KeyFontSize = 16;
        private const int BatteryFontSize = 22;

        private static readonly Color OffColor = new Color(1f, 1f, 1f, 0.85f);
        private static readonly Color LockingColor = new Color(1f, 0.82f, 0.2f);
        private static readonly Color LockedColor = new Color(0.55f, 1f, 0.6f);
        private static readonly Color ErrorColor = new Color(1f, 0.35f, 0.3f);
        private static readonly Color NoPowerColor = new Color(0.55f, 0.55f, 0.55f);

        private static GUIStyle? _labelStyle;
        private static GUIStyle? _keyStyle;
        private static GUIStyle? _batteryStyle;
        private static Texture2D? _chipTexture;
        private static Texture2D? _keyBorderTexture;
        private static float _builtStyleScale = -1f;

        private static void EnsureStyles(float scale)
        {
            if (_labelStyle != null && Mathf.Approximately(_builtStyleScale, scale))
                return;

            _builtStyleScale = scale;
            float mult = BaseScaleMultiplier * scale;

            _chipTexture ??= MakeSolidTexture(new Color(0.05f, 0.05f, 0.05f, 0.72f));
            _keyBorderTexture ??= MakeSolidTexture(new Color(1f, 1f, 1f, 0.9f));

            _labelStyle = new GUIStyle
            {
                fontSize = Mathf.RoundToInt(LabelFontSize * mult),
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleRight,
                normal = { textColor = Color.white },
            };

            _keyStyle = new GUIStyle
            {
                fontSize = Mathf.RoundToInt(KeyFontSize * mult),
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleCenter,
                normal = { textColor = Color.white },
            };

            _batteryStyle = new GUIStyle
            {
                fontSize = Mathf.RoundToInt(BatteryFontSize * mult),
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleRight,
                normal = { textColor = Color.white },
            };
        }

        private static Texture2D MakeSolidTexture(Color color)
        {
            var tex = new Texture2D(1, 1);
            tex.SetPixel(0, 0, color);
            tex.Apply();
            return tex;
        }

        public static void Draw(MagBootsState state, string keyLabel, Vector2 offsetPixels, bool showBattery,
            BatteryDisplayMode batteryMode, float batteryFraction, float batteryMinutesRemaining, float scale)
        {
            EnsureStyles(scale);
            float mult = BaseScaleMultiplier * scale;

            (string labelText, Color stateColor) = state switch
            {
                MagBootsState.Locking => ("MAG BOOTS: LOCKING", LockingColor),
                MagBootsState.Locked   => ("MAG BOOTS: LOCKED", LockedColor),
                MagBootsState.Error    => ("MAG BOOTS: ERROR", ErrorColor),
                MagBootsState.NoPower  => ("MAG BOOTS: NO POWER", NoPowerColor),
                _                      => ("MAG BOOTS", OffColor),
            };

            string label = LetterSpace(labelText);
            string battery = showBattery ? BatteryText(batteryMode, batteryFraction, batteryMinutesRemaining) : string.Empty;

            float promptHeight = PromptHeight * mult;
            float padding = Padding * mult;

            float labelWidth = _labelStyle!.CalcSize(new GUIContent(label)).x;
            float keyWidth = Mathf.Max(KeyChipMinWidth * mult, _keyStyle!.CalcSize(new GUIContent(keyLabel)).x + 18 * mult);
            float batteryWidth = showBattery ? _batteryStyle!.CalcSize(new GUIContent(battery)).x : 0f;

            float totalWidth = labelWidth + padding + (showBattery ? batteryWidth + padding : 0f) + keyWidth;

            // offsetPixels is measured from screen center and positions the CENTER of the whole hint
            // block (label + battery + key chip together), not any one piece's edge - so the numbers in
            // the config read the same regardless of how wide the label/battery text happens to be.
            float centerX = Screen.width * 0.5f + offsetPixels.x;
            float centerY = Screen.height * 0.5f + offsetPixels.y;
            float x = centerX - totalWidth * 0.5f;
            float y = centerY - promptHeight * 0.5f;

            var labelRect = new Rect(x, y, labelWidth, promptHeight);
            var batteryRect = new Rect(labelRect.xMax + padding, y, batteryWidth, promptHeight);
            float keyX = showBattery ? batteryRect.xMax + padding : labelRect.xMax + padding;
            float keyHeight = 26 * mult;
            var keyRect = new Rect(keyX, y + (promptHeight - keyHeight) * 0.5f, keyWidth, keyHeight);

            DrawShadowedLabel(labelRect, label, _labelStyle, stateColor, mult);

            if (showBattery)
                DrawShadowedLabel(batteryRect, battery, _batteryStyle!, BatteryColor(batteryFraction), mult);

            GUI.color = Color.white;
            GUI.DrawTexture(keyRect, _chipTexture);
            DrawBorder(keyRect, _keyBorderTexture!, 1.5f * mult);
            GUI.Label(keyRect, keyLabel, _keyStyle); // sits on its own dark chip - no shadow needed.

            GUI.color = Color.white;
        }

        // OnGUI's GUIStyle has no built-in drop-shadow, so approximate one the same way QuickCutscene's
        // hint does: draw the text once offset in black, then again on top in the real color.
        private static void DrawShadowedLabel(Rect rect, string text, GUIStyle style, Color color, float mult)
        {
            float shadowOffset = 2f * mult;
            var shadowRect = new Rect(rect.x + shadowOffset, rect.y + shadowOffset, rect.width, rect.height);

            GUI.color = new Color(0f, 0f, 0f, 0.85f);
            GUI.Label(shadowRect, text, style);

            GUI.color = color;
            GUI.Label(rect, text, style);
        }

        private static Color BatteryColor(float fraction)
        {
            if (fraction <= 0.2f)
                return ErrorColor;
            if (fraction <= 0.4f)
                return LockingColor;
            return Color.white;
        }

        private static string BatteryText(BatteryDisplayMode mode, float fraction, float minutesRemaining)
        {
            return mode switch
            {
                BatteryDisplayMode.Percentage => $"{Mathf.RoundToInt(Mathf.Clamp01(fraction) * 100f)}%",
                BatteryDisplayMode.Timer => FormatMinutesAsTimer(minutesRemaining),
                _ => BatteryBarText(fraction), // Gauge (and fallback)
            };
        }

        private static string FormatMinutesAsTimer(float minutesRemaining)
        {
            int totalSeconds = Mathf.Max(0, Mathf.CeilToInt(minutesRemaining * 60f));
            int minutes = totalSeconds / 60;
            int seconds = totalSeconds % 60;
            return $"{minutes}:{seconds:00}";
        }

        private static string BatteryBarText(float fraction)
        {
            int filled = Mathf.RoundToInt(Mathf.Clamp01(fraction) * BatterySegments);
            var sb = new System.Text.StringBuilder(BatterySegments + 2);
            sb.Append('[');
            for (int i = 0; i < BatterySegments; i++)
                sb.Append(i < filled ? '■' : '□'); // U+25A0 / U+25A1 - same glyph family, same cell size.
            sb.Append(']');
            return sb.ToString();
        }

        private static void DrawBorder(Rect rect, Texture2D tex, float thickness)
        {
            GUI.DrawTexture(new Rect(rect.x, rect.y, rect.width, thickness), tex);
            GUI.DrawTexture(new Rect(rect.x, rect.yMax - thickness, rect.width, thickness), tex);
            GUI.DrawTexture(new Rect(rect.x, rect.y, thickness, rect.height), tex);
            GUI.DrawTexture(new Rect(rect.xMax - thickness, rect.y, thickness, rect.height), tex);
        }

        // GUIStyle has no real letter-spacing property, so approximate it with a thin space
        // (U+2009) between characters rather than a full space, which was far too wide.
        private static string LetterSpace(string text)
        {
            return string.Join(" ", text.ToCharArray());
        }
    }
}
