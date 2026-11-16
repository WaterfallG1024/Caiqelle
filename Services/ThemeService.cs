using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using System;
using System.Collections.Generic;
using Windows.UI;

namespace Caiqelle.Services
{
    public enum AccentTheme
    {
        Emerald = 0,
        Cyan = 1,
        Purple = 2,
        Orange = 3,
        Pink = 4,
        Red = 5,
        Gold = 6,
        Lime = 7,
        Blue = 8,
        White = 9
    }

    public record AccentThemeInfo(AccentTheme Theme, string DisplayName, string Hex, Color Color);

    public class ThemeService
    {
        public static IReadOnlyList<AccentThemeInfo> AvailableThemes { get; } = new List<AccentThemeInfo>
        {
            new(AccentTheme.Emerald, "エメラルドグリーン", "#00FF7F", Color.FromArgb(255, 0, 255, 127)),
            new(AccentTheme.Cyan,    "シアンブルー",       "#00BFFF", Color.FromArgb(255, 0, 191, 255)),
            new(AccentTheme.Purple,  "バイオレットパープル", "#A855F7", Color.FromArgb(255, 168, 85, 247)),
            new(AccentTheme.Orange,  "サンセットオレンジ", "#FF8C00", Color.FromArgb(255, 255, 140, 0)),
            new(AccentTheme.Pink,    "ホットピンク",       "#FF1493", Color.FromArgb(255, 255, 20, 147)),
            new(AccentTheme.Red,     "クリムゾンレッド",   "#FF3B30", Color.FromArgb(255, 255, 59, 48)),
            new(AccentTheme.Gold,    "アンバーゴールド",   "#FFCC00", Color.FromArgb(255, 255, 204, 0)),
            new(AccentTheme.Lime,    "ライムネオン",       "#76FF03", Color.FromArgb(255, 118, 255, 3)),
            new(AccentTheme.Blue,    "ディープアズール",   "#3B82F6", Color.FromArgb(255, 59, 130, 246)),
            new(AccentTheme.White,   "プラチナホワイト",   "#F1F5F9", Color.FromArgb(255, 241, 245, 249))
        };

        public static Color GetAccentColor(AccentTheme theme) => theme switch
        {
            AccentTheme.Cyan   => Color.FromArgb(255, 0, 191, 255),
            AccentTheme.Purple => Color.FromArgb(255, 168, 85, 247),
            AccentTheme.Orange => Color.FromArgb(255, 255, 140, 0),
            AccentTheme.Pink   => Color.FromArgb(255, 255, 20, 147),
            AccentTheme.Red    => Color.FromArgb(255, 255, 59, 48),
            AccentTheme.Gold   => Color.FromArgb(255, 255, 204, 0),
            AccentTheme.Lime   => Color.FromArgb(255, 118, 255, 3),
            AccentTheme.Blue   => Color.FromArgb(255, 59, 130, 246),
            AccentTheme.White  => Color.FromArgb(255, 241, 245, 249),
            _                  => Color.FromArgb(255, 0, 255, 127)
        };

        public event Action<AccentTheme>? ThemeChanged;

        public AccentTheme CurrentTheme { get; private set; } = AccentTheme.Emerald;

        /// <summary>
        /// アプリ全体のリソースブラシを更新し、全ウィンドウにテーマを適用する
        /// </summary>
        public void ApplyTheme(AccentTheme theme)
        {
            CurrentTheme = theme;
            var resources = Application.Current?.Resources;
            if (resources == null) return;

            Color primary = GetAccentColor(theme);
            Color secondary = BlendWithWhite(primary, 0.28);
            Color container = ScaleColor(primary, 0.28, 255);
            Color containerGlass = Color.FromArgb(0x44, primary.R, primary.G, primary.B);
            Color secondaryContainer = ScaleColor(secondary, 0.24, 255);
            Color secondaryContainerGlass = Color.FromArgb(0x40, secondary.R, secondary.G, secondary.B);

            // 背景の輝度に応じてボタン上の文字色（白/黒）を自動調整
            double luminance = 0.299 * primary.R + 0.587 * primary.G + 0.114 * primary.B;
            Color onPrimary = luminance >= 150.0
                ? Color.FromArgb(255, 17, 17, 22)
                : Color.FromArgb(255, 255, 255, 255);

            Color onPrimaryContainer = BlendWithWhite(primary, 0.78);
            Color onSecondaryContainer = BlendWithWhite(secondary, 0.80);

            UpdateBrushColor(resources, "M3PrimaryBrush", primary);
            UpdateBrushColor(resources, "M3PrimaryContainerBrush", container);
            UpdateBrushColor(resources, "M3PrimaryContainerGlassBrush", containerGlass);
            UpdateBrushColor(resources, "M3OnPrimaryBrush", onPrimary);
            UpdateBrushColor(resources, "M3OnPrimaryContainerBrush", onPrimaryContainer);

            UpdateBrushColor(resources, "M3SecondaryBrush", secondary);
            UpdateBrushColor(resources, "M3SecondaryContainerBrush", secondaryContainer);
            UpdateBrushColor(resources, "M3SecondaryContainerGlassBrush", secondaryContainerGlass);
            UpdateBrushColor(resources, "M3OnSecondaryContainerBrush", onSecondaryContainer);

            UpdateBrushColor(resources, "AfterGlowAccentBrush", primary);

            if (resources.TryGetValue("AfterGlowAccentGradientBrush", out var gradObj) && gradObj is LinearGradientBrush gradBrush && gradBrush.GradientStops.Count >= 2)
            {
                gradBrush.GradientStops[0].Color = primary;
                gradBrush.GradientStops[1].Color = secondary;
            }

            // WinUI 標準コントロール（ToggleSwitch / ComboBox / Slider 等）にもテーマカラーを適用
            resources["SystemAccentColor"] = primary;
            resources["SystemAccentColorLight1"] = BlendWithWhite(primary, 0.20);
            resources["SystemAccentColorLight2"] = BlendWithWhite(primary, 0.40);
            resources["SystemAccentColorLight3"] = BlendWithWhite(primary, 0.60);
            resources["SystemAccentColorDark1"] = ScaleColor(primary, 0.80, 255);
            resources["SystemAccentColorDark2"] = ScaleColor(primary, 0.60, 255);
            resources["SystemAccentColorDark3"] = ScaleColor(primary, 0.40, 255);

            SetOrUpdateBrush(resources, "ToggleSwitchFillOn", primary);
            SetOrUpdateBrush(resources, "ToggleSwitchFillOnPointerOver", BlendWithWhite(primary, 0.15));
            SetOrUpdateBrush(resources, "ToggleSwitchFillOnPressed", ScaleColor(primary, 0.85, 255));
            SetOrUpdateBrush(resources, "ToggleSwitchKnobFillOn", onPrimary);
            SetOrUpdateBrush(resources, "ToggleSwitchKnobFillOnPointerOver", onPrimary);
            SetOrUpdateBrush(resources, "ToggleSwitchKnobFillOnPressed", onPrimary);
            SetOrUpdateBrush(resources, "TextControlBorderBrushFocused", primary);
            SetOrUpdateBrush(resources, "SliderTrackValueFill", primary);
            SetOrUpdateBrush(resources, "SliderThumbBackground", primary);

            ThemeChanged?.Invoke(theme);
        }

        private static void UpdateBrushColor(ResourceDictionary resources, string key, Color color)
        {
            if (resources.TryGetValue(key, out var obj) && obj is SolidColorBrush brush)
            {
                brush.Color = color;
            }
        }

        private static void SetOrUpdateBrush(ResourceDictionary resources, string key, Color color)
        {
            if (resources.TryGetValue(key, out var obj) && obj is SolidColorBrush brush)
            {
                brush.Color = color;
            }
            else
            {
                resources[key] = new SolidColorBrush(color);
            }
        }

        private static Color BlendWithWhite(Color c, double ratio)
        {
            byte r = (byte)Math.Clamp(c.R + (255 - c.R) * ratio, 0, 255);
            byte g = (byte)Math.Clamp(c.G + (255 - c.G) * ratio, 0, 255);
            byte b = (byte)Math.Clamp(c.B + (255 - c.B) * ratio, 0, 255);
            return Color.FromArgb(255, r, g, b);
        }

        private static Color ScaleColor(Color c, double factor, byte alpha)
        {
            byte r = (byte)Math.Clamp(c.R * factor, 0, 255);
            byte g = (byte)Math.Clamp(c.G * factor, 0, 255);
            byte b = (byte)Math.Clamp(c.B * factor, 0, 255);
            return Color.FromArgb(alpha, r, g, b);
        }
    }
}
