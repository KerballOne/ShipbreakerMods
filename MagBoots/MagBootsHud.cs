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
        private const int StrideFontSize = 12;
        private const int StrideWidth = 90;

        // Plain ASCII words rather than a symbol/emoji - Unity's legacy OnGUI/GUIStyle text reliably
        // renders basic Latin text, but silently drops both color emoji (🥾/🏃) and non-Latin Unicode
        // symbols (⏻) with the default font, so neither actually showed up in-game. Drawn as their own
        // small label to the left of each chip, outside its border, rather than packed inside the chip
        // alongside the hotkey name.
        private const string ToggleRowLabel = "POWER";
        private const string RunRowLabel = "SPEED";

        private static readonly Color OffColor = new Color(1f, 1f, 1f, 0.85f);
        private static readonly Color LockingColor = new Color(1f, 0.82f, 0.2f);
        private static readonly Color LockedColor = new Color(0.55f, 1f, 0.6f);
        private static readonly Color ErrorColor = new Color(1f, 0.35f, 0.3f);
        private static readonly Color NoPowerColor = new Color(0.55f, 0.55f, 0.55f);

        private static GUIStyle? _labelStyle;
        private static GUIStyle? _rowLabelStyle;
        private static GUIStyle? _keyStyle;
        private static GUIStyle? _keyStyleActive;
        private static GUIStyle? _batteryStyle;
        private static GUIStyle? _strideStyle;
        private static Texture2D? _chipTexture;
        private static Texture2D? _activeChipTexture;
        private static Texture2D? _keyBorderTexture;
        private static float _builtStyleScale = -1f;

        private static void EnsureStyles(float scale)
        {
            if (_labelStyle != null && Mathf.Approximately(_builtStyleScale, scale))
                return;

            _builtStyleScale = scale;
            float mult = BaseScaleMultiplier * scale;

            _chipTexture ??= MakeSolidTexture(new Color(0.05f, 0.05f, 0.05f, 0.72f));
            _activeChipTexture ??= MakeSolidTexture(new Color(1f, 1f, 1f, 0.95f));
            _keyBorderTexture ??= MakeSolidTexture(new Color(1f, 1f, 1f, 0.9f));

            _labelStyle = new GUIStyle
            {
                fontSize = Mathf.RoundToInt(LabelFontSize * mult),
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleRight,
                normal = { textColor = Color.white },
            };

            // Key chips are now half their old height (two stacked in the same space one used to take),
            // so the font shrinks to match - the old KeyFontSize was sized for the full-height chip.
            int keyFontSize = Mathf.RoundToInt(KeyFontSize * mult * 0.6f);

            _rowLabelStyle = new GUIStyle
            {
                fontSize = keyFontSize,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleRight,
                normal = { textColor = new Color(1f, 1f, 1f, 0.85f) },
            };

            _keyStyle = new GUIStyle
            {
                fontSize = keyFontSize,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleLeft,
                normal = { textColor = Color.white },
            };

            // Same style but black-on-white, swapped in whenever a chip's action is active (Locked /
            // Running) instead of restyling text color per-draw.
            _keyStyleActive = new GUIStyle(_keyStyle)
            {
                normal = { textColor = Color.black },
            };

            _strideStyle = new GUIStyle
            {
                fontSize = Mathf.RoundToInt(StrideFontSize * mult),
                fontStyle = FontStyle.Normal,
                alignment = TextAnchor.MiddleLeft,
                normal = { textColor = new Color(1f, 1f, 1f, 0.75f) },
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

        public static void Draw(MagBootsState state, string keyLabel, string runKeyLabel, bool isRunning, Vector2 offsetPixels, bool showBattery,
            BatteryDisplayMode batteryMode, float batteryFraction, float batteryMinutesRemaining, float scale,
            bool showStride, float strideDistance)
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
            bool isLocked = state == MagBootsState.Locked;

            string label = LetterSpace(labelText);
            string battery = showBattery ? BatteryText(batteryMode, batteryFraction, batteryMinutesRemaining) : string.Empty;

            float promptHeight = PromptHeight * mult;
            float padding = Padding * mult;
            float rowLabelGap = 6f * mult;

            float labelWidth = _labelStyle!.CalcSize(new GUIContent(label)).x;
            // Row labels ("Boots"/"Run") sit to the left of, and outside, their chip - sized to fit
            // whichever of the two words is wider so both line up on the same left edge.
            float rowLabelWidth = Mathf.Max(
                _rowLabelStyle!.CalcSize(new GUIContent(ToggleRowLabel)).x,
                _rowLabelStyle.CalcSize(new GUIContent(RunRowLabel)).x);
            // Measures each chip's own hotkey text so the chip is always sized to fit whatever the
            // configured hotkey turns out to be (a single letter, or a long rebind like "LeftShift"),
            // rather than a fixed guess that could clip on an unusually long key name.
            float toggleContentWidth = _keyStyle!.CalcSize(new GUIContent(keyLabel)).x;
            float runContentWidth = _keyStyle.CalcSize(new GUIContent(runKeyLabel)).x;
            float keyWidth = Mathf.Max(KeyChipMinWidth * mult, Mathf.Max(toggleContentWidth, runContentWidth) + 18 * mult);
            float batteryWidth = showBattery ? _batteryStyle!.CalcSize(new GUIContent(battery)).x : 0f;

            float totalWidth = labelWidth + padding + (showBattery ? batteryWidth + padding : 0f)
                + rowLabelWidth + rowLabelGap + keyWidth;

            // offsetPixels is measured from screen center and positions the CENTER of the whole hint
            // block (label + battery + row labels + key chip together), not any one piece's edge - so
            // the numbers in the config read the same regardless of how wide the text happens to be.
            float centerX = Screen.width * 0.5f + offsetPixels.x;
            float centerY = Screen.height * 0.5f + offsetPixels.y;
            float x = centerX - totalWidth * 0.5f;
            float y = centerY - promptHeight * 0.5f;

            var labelRect = new Rect(x, y, labelWidth, promptHeight);
            var batteryRect = new Rect(labelRect.xMax + padding, y, batteryWidth, promptHeight);
            float rowLabelX = showBattery ? batteryRect.xMax + padding : labelRect.xMax + padding;
            float keyX = rowLabelX + rowLabelWidth + rowLabelGap;

            // Two half-height chips stacked to fill the same vertical space the single full-height chip
            // used to occupy: attach/detach on top, run below it, each independently inverting to a solid
            // white chip with black text when its own action is currently active. The row label for each
            // sits in the same row, just outside the chip's left edge.
            float keyHeight = 26 * mult;
            float chipHeight = keyHeight * 0.5f;
            float stackY = y + (promptHeight - keyHeight) * 0.5f;
            var toggleRowLabelRect = new Rect(rowLabelX, stackY, rowLabelWidth, chipHeight);
            var runRowLabelRect = new Rect(rowLabelX, stackY + chipHeight, rowLabelWidth, chipHeight);
            var toggleKeyRect = new Rect(keyX, stackY, keyWidth, chipHeight);
            var runKeyRect = new Rect(keyX, stackY + chipHeight, keyWidth, chipHeight);

            DrawShadowedLabel(labelRect, label, _labelStyle, stateColor, mult);

            if (showBattery)
                DrawShadowedLabel(batteryRect, battery, _batteryStyle!, BatteryColor(batteryFraction), mult);

            GUI.Label(toggleRowLabelRect, ToggleRowLabel, _rowLabelStyle);
            GUI.Label(runRowLabelRect, RunRowLabel, _rowLabelStyle);

            DrawKeyChip(toggleKeyRect, keyLabel, isLocked, mult);
            DrawKeyChip(runKeyRect, runKeyLabel, isRunning, mult);

            // Small, quiet readout of the stride distance just right of the key chips - lets the player
            // see their stride shrink in real time as they pitch their view up/down, so the head-tilt
            // stride control (see ConfigMaxStridePitch/MinStridePitch) is immediately legible rather than
            // a hidden feel they'd have to infer from movement alone.
            if (showStride)
            {
                string strideText = $"STRIDE {strideDistance:F2}m";
                float strideGap = 12f * mult;
                var strideRect = new Rect(keyX + keyWidth + strideGap, y, StrideWidth * mult, promptHeight);
                DrawShadowedLabel(strideRect, strideText, _strideStyle!, _strideStyle!.normal.textColor, mult);
            }
        }

        // Inverts to a solid white chip with black text while active (Locked / Running), instead of the
        // normal dark chip with white text - a quick, unmistakable at-a-glance "this is on" signal that
        // doesn't rely on reading the label text itself.
        private static void DrawKeyChip(Rect rect, string label, bool active, float mult)
        {
            GUI.color = Color.white;
            GUI.DrawTexture(rect, active ? _activeChipTexture : _chipTexture);
            DrawBorder(rect, _keyBorderTexture!, 1.5f * mult);

            GUIStyle style = active ? _keyStyleActive! : _keyStyle!;
            float inset = 6f * mult;
            var contentRect = new Rect(rect.x + inset, rect.y, rect.width - inset, rect.height);
            GUI.Label(contentRect, label, style);

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
