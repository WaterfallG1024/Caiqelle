using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Windows.UI;
using Windows.UI.ViewManagement;
using WinRT.Interop;

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

    public enum AppThemeMode
    {
        System = 0,
        Light = 1,
        Dark = 2
    }

    public record AccentThemeInfo(AccentTheme Theme, string DisplayName, string Hex, Color Color);

    public class ThemeService
    {
        private readonly List<(Window Window, FrameworkElement Root)> _trackedWindows = new();
        private readonly UISettings _uiSettings = new();

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
        public event Action<ElementTheme>? AppThemeChanged;

        public AccentTheme CurrentTheme { get; private set; } = AccentTheme.Emerald;
        public AppThemeMode CurrentAppTheme { get; private set; } = AppThemeMode.System;

        public ElementTheme EffectiveElementTheme => CurrentAppTheme switch
        {
            AppThemeMode.Light => ElementTheme.Light,
            AppThemeMode.Dark => ElementTheme.Dark,
            _ => ElementTheme.Default
        };

        public bool IsDarkActive
        {
            get
            {
                if (CurrentAppTheme == AppThemeMode.Dark) return true;
                if (CurrentAppTheme == AppThemeMode.Light) return false;
                var bg = _uiSettings.GetColorValue(UIColorType.Background);
                return (0.299 * bg.R + 0.587 * bg.G + 0.114 * bg.B) < 128.0;
            }
        }

        public ThemeService()
        {
            _uiSettings.ColorValuesChanged += (s, e) =>
            {
                if (CurrentAppTheme == AppThemeMode.System && _trackedWindows.Count > 0)
                {
                    _trackedWindows[0].Window.DispatcherQueue?.TryEnqueue(() =>
                    {
                        ApplyTheme(CurrentTheme, CurrentAppTheme);
                    });
                }
            };
        }

        /// <summary>
        /// ウィンドウを登録し、テーマ（ライト/ダーク）変更時に自動で同期させる
        /// </summary>
        public void RegisterWindow(Window window, FrameworkElement rootElement)
        {
            _trackedWindows.RemoveAll(x => x.Window == window);
            _trackedWindows.Add((window, rootElement));
            window.Closed += (s, e) => _trackedWindows.RemoveAll(x => x.Window == window);
            ApplyElementThemeToWindow(window, rootElement);
        }

        public void ApplyAppTheme(AppThemeMode appTheme)
        {
            ApplyTheme(CurrentTheme, appTheme);
        }

        /// <summary>
        /// アプリ全体のリソースブラシを更新し、全ウィンドウにテーマを適用する
        /// </summary>
        public void ApplyTheme(AccentTheme theme, AppThemeMode? appTheme = null)
        {
            CurrentTheme = theme;
            if (appTheme.HasValue)
            {
                CurrentAppTheme = appTheme.Value;
            }

            var resources = Application.Current?.Resources;
            if (resources == null) return;

            bool isDark = IsDarkActive;
            Color rawPrimary = GetAccentColor(theme);

            // ライトモードでは白背景上での視認性（コントラスト比）を確保するためトーンを落ち着かせる
            Color effectivePrimary = isDark
                ? rawPrimary
                : (theme == AccentTheme.White ? Color.FromArgb(255, 71, 85, 105) : ScaleColor(rawPrimary, 0.56, 255));

            Color secondary = isDark
                ? BlendWithWhite(rawPrimary, 0.28)
                : ScaleColor(rawPrimary, 0.44, 255);

            Color container = isDark
                ? ScaleColor(rawPrimary, 0.28, 255)
                : BlendWithWhite(effectivePrimary, 0.84);
            Color containerGlass = isDark
                ? Color.FromArgb(0x44, rawPrimary.R, rawPrimary.G, rawPrimary.B)
                : Color.FromArgb(0x28, effectivePrimary.R, effectivePrimary.G, effectivePrimary.B);

            Color secondaryContainer = isDark
                ? ScaleColor(secondary, 0.24, 255)
                : BlendWithWhite(secondary, 0.86);
            Color secondaryContainerGlass = isDark
                ? Color.FromArgb(0x40, secondary.R, secondary.G, secondary.B)
                : Color.FromArgb(0x24, secondary.R, secondary.G, secondary.B);

            // 背景の輝度に応じてボタン上の文字色（白/黒）を自動調整
            double luminance = 0.299 * effectivePrimary.R + 0.587 * effectivePrimary.G + 0.114 * effectivePrimary.B;
            Color onPrimary = luminance >= 150.0
                ? Color.FromArgb(255, 17, 17, 22)
                : Color.FromArgb(255, 255, 255, 255);

            Color onPrimaryContainer = isDark ? BlendWithWhite(rawPrimary, 0.78) : ScaleColor(effectivePrimary, 0.45, 255);
            Color onSecondaryContainer = isDark ? BlendWithWhite(secondary, 0.80) : ScaleColor(secondary, 0.45, 255);

            SetOrUpdateBrush(resources, "M3PrimaryBrush", effectivePrimary);
            SetOrUpdateBrush(resources, "M3PrimaryContainerBrush", container);
            SetOrUpdateBrush(resources, "M3PrimaryContainerGlassBrush", containerGlass);
            SetOrUpdateBrush(resources, "M3OnPrimaryBrush", onPrimary);
            SetOrUpdateBrush(resources, "M3OnPrimaryContainerBrush", onPrimaryContainer);

            SetOrUpdateBrush(resources, "M3SecondaryBrush", secondary);
            SetOrUpdateBrush(resources, "M3SecondaryContainerBrush", secondaryContainer);
            SetOrUpdateBrush(resources, "M3SecondaryContainerGlassBrush", secondaryContainerGlass);
            SetOrUpdateBrush(resources, "M3OnSecondaryContainerBrush", onSecondaryContainer);
            SetOrUpdateBrush(resources, "AfterGlowAccentBrush", effectivePrimary);

            // Fluent 2 ライト / ダーク対応 共通サーフェス＆テキストブラシ
            if (isDark)
            {
                SetOrUpdateBrush(resources, "M3SurfaceBrush", Color.FromArgb(0x9E, 0x0A, 0x0A, 0x0A));
                SetOrUpdateBrush(resources, "M3SurfaceContainerLowestBrush", Color.FromArgb(0x99, 0x16, 0x16, 0x18));
                SetOrUpdateBrush(resources, "M3SurfaceContainerLowBrush", Color.FromArgb(0xA8, 0x20, 0x20, 0x24));
                SetOrUpdateBrush(resources, "M3SurfaceContainerBrush", Color.FromArgb(0xBA, 0x2B, 0x2B, 0x30));
                SetOrUpdateBrush(resources, "M3SurfaceContainerHighBrush", Color.FromArgb(0xCC, 0x34, 0x34, 0x3A));
                SetOrUpdateBrush(resources, "M3SurfaceContainerHighestBrush", Color.FromArgb(0xDD, 0x3E, 0x3E, 0x45));

                SetOrUpdateBrush(resources, "AfterGlowTextPrimaryBrush", Color.FromArgb(255, 255, 255, 255));
                SetOrUpdateBrush(resources, "AfterGlowTextSecondaryBrush", Color.FromArgb(255, 198, 198, 204));
                SetOrUpdateBrush(resources, "AfterGlowTextTertiaryBrush", Color.FromArgb(255, 146, 146, 154));
                SetOrUpdateBrush(resources, "FluentCardStrokeBrush", Color.FromArgb(0x18, 255, 255, 255));
                SetOrUpdateBrush(resources, "FluentSurfaceStrokeBrush", Color.FromArgb(0x28, 255, 255, 255));

                // 削除・解除等の危険操作用カラー（ダーク背景で高コントラスト）
                SetOrUpdateBrush(resources, "FuncPinkBrush", Color.FromArgb(255, 255, 117, 138));
                SetOrUpdateBrush(resources, "FuncPinkBgBrush", Color.FromArgb(0x28, 255, 92, 130));
            }
            else
            {
                SetOrUpdateBrush(resources, "M3SurfaceBrush", Color.FromArgb(0xE8, 0xF3, 0xF3, 0xF3));
                SetOrUpdateBrush(resources, "M3SurfaceContainerLowestBrush", Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF));
                SetOrUpdateBrush(resources, "M3SurfaceContainerLowBrush", Color.FromArgb(0xCC, 0xF7, 0xF7, 0xF9));
                SetOrUpdateBrush(resources, "M3SurfaceContainerBrush", Color.FromArgb(0xEC, 0xFF, 0xFF, 0xFF));
                SetOrUpdateBrush(resources, "M3SurfaceContainerHighBrush", Color.FromArgb(0xF5, 0xF0, 0xF2, 0xF5));
                SetOrUpdateBrush(resources, "M3SurfaceContainerHighestBrush", Color.FromArgb(0xFF, 0xE5, 0xE7, 0xEB));

                SetOrUpdateBrush(resources, "AfterGlowTextPrimaryBrush", Color.FromArgb(255, 24, 24, 27));
                SetOrUpdateBrush(resources, "AfterGlowTextSecondaryBrush", Color.FromArgb(255, 82, 82, 91));
                SetOrUpdateBrush(resources, "AfterGlowTextTertiaryBrush", Color.FromArgb(255, 113, 113, 122));
                SetOrUpdateBrush(resources, "FluentCardStrokeBrush", Color.FromArgb(0x14, 0, 0, 0));
                SetOrUpdateBrush(resources, "FluentSurfaceStrokeBrush", Color.FromArgb(0x22, 0, 0, 0));

                // 削除・解除等の危険操作用カラー（ライト背景で高コントラスト）
                SetOrUpdateBrush(resources, "FuncPinkBrush", Color.FromArgb(255, 196, 43, 28));
                SetOrUpdateBrush(resources, "FuncPinkBgBrush", Color.FromArgb(0x18, 196, 43, 28));
            }

            if (resources.TryGetValue("AfterGlowAccentGradientBrush", out var gradObj) && gradObj is LinearGradientBrush gradBrush && gradBrush.GradientStops.Count >= 2)
            {
                gradBrush.GradientStops[0].Color = effectivePrimary;
                gradBrush.GradientStops[1].Color = secondary;
            }

            // オーバーレイ等のグラデーション背景もライト/ダークに対応
            if (resources.TryGetValue("ThemeWindowBackgroundGradientBrush", out var bgGradObj) && bgGradObj is LinearGradientBrush bgGrad && bgGrad.GradientStops.Count >= 3)
            {
                if (isDark)
                {
                    var darkBase = Color.FromArgb(255, 18, 18, 22);
                    bgGrad.GradientStops[0].Color = BlendDarkWithAccent(darkBase, rawPrimary, 0.14, 0xD8);
                    bgGrad.GradientStops[1].Color = BlendDarkWithAccent(darkBase, rawPrimary, 0.04, 0xD0);
                    bgGrad.GradientStops[2].Color = BlendDarkWithAccent(darkBase, secondary, 0.10, 0xDA);
                }
                else
                {
                    var lightBase = Color.FromArgb(255, 248, 249, 251);
                    bgGrad.GradientStops[0].Color = BlendWithAccent(lightBase, rawPrimary, 0.08, 0xEB);
                    bgGrad.GradientStops[1].Color = BlendWithAccent(lightBase, rawPrimary, 0.02, 0xE6);
                    bgGrad.GradientStops[2].Color = BlendWithAccent(lightBase, secondary, 0.06, 0xEE);
                }
            }

            // WinUI 標準コントロール（ToggleSwitch / ComboBox / Slider / NavigationView 等）にもテーマカラーを適用
            resources["SystemAccentColor"] = effectivePrimary;
            resources["SystemAccentColorLight1"] = BlendWithWhite(effectivePrimary, 0.20);
            resources["SystemAccentColorLight2"] = BlendWithWhite(effectivePrimary, 0.40);
            resources["SystemAccentColorLight3"] = BlendWithWhite(effectivePrimary, 0.60);
            resources["SystemAccentColorDark1"] = ScaleColor(effectivePrimary, 0.80, 255);
            resources["SystemAccentColorDark2"] = ScaleColor(effectivePrimary, 0.60, 255);
            resources["SystemAccentColorDark3"] = ScaleColor(effectivePrimary, 0.40, 255);

            SetOrUpdateBrush(resources, "ToggleSwitchFillOn", effectivePrimary);
            SetOrUpdateBrush(resources, "ToggleSwitchFillOnPointerOver", BlendWithWhite(effectivePrimary, 0.15));
            SetOrUpdateBrush(resources, "ToggleSwitchFillOnPressed", ScaleColor(effectivePrimary, 0.85, 255));
            SetOrUpdateBrush(resources, "ToggleSwitchKnobFillOn", onPrimary);
            SetOrUpdateBrush(resources, "ToggleSwitchKnobFillOnPointerOver", onPrimary);
            SetOrUpdateBrush(resources, "ToggleSwitchKnobFillOnPressed", onPrimary);
            SetOrUpdateBrush(resources, "ToggleButtonBackgroundChecked", effectivePrimary);
            SetOrUpdateBrush(resources, "ToggleButtonBackgroundCheckedPointerOver", BlendWithWhite(effectivePrimary, 0.12));
            SetOrUpdateBrush(resources, "ToggleButtonBackgroundCheckedPressed", ScaleColor(effectivePrimary, 0.85, 255));
            SetOrUpdateBrush(resources, "ToggleButtonForegroundChecked", onPrimary);
            SetOrUpdateBrush(resources, "ToggleButtonForegroundCheckedPointerOver", onPrimary);
            SetOrUpdateBrush(resources, "ToggleButtonForegroundCheckedPressed", onPrimary);
            SetOrUpdateBrush(resources, "TextControlBorderBrushFocused", effectivePrimary);
            SetOrUpdateBrush(resources, "SliderTrackValueFill", effectivePrimary);
            SetOrUpdateBrush(resources, "SliderThumbBackground", effectivePrimary);

            // 登録済み全ウィンドウへ ElementTheme / ImmersiveDarkMode を反映
            foreach (var (win, root) in _trackedWindows.ToArray())
            {
                ApplyElementThemeToWindow(win, root);
            }

            ThemeChanged?.Invoke(theme);
            AppThemeChanged?.Invoke(EffectiveElementTheme);
        }

        private void ApplyElementThemeToWindow(Window window, FrameworkElement root)
        {
            try
            {
                root.RequestedTheme = EffectiveElementTheme;
                IntPtr hwnd = WindowNative.GetWindowHandle(window);
                int useDark = IsDarkActive ? 1 : 0;
                const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
                DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref useDark, sizeof(int));
            }
            catch { }
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

        private static Color BlendDarkWithAccent(Color darkBase, Color accent, double ratio, byte alpha)
        {
            byte r = (byte)Math.Clamp(darkBase.R * (1 - ratio) + accent.R * ratio, 0, 255);
            byte g = (byte)Math.Clamp(darkBase.G * (1 - ratio) + accent.G * ratio, 0, 255);
            byte b = (byte)Math.Clamp(darkBase.B * (1 - ratio) + accent.B * ratio, 0, 255);
            return Color.FromArgb(alpha, r, g, b);
        }

        private static Color BlendWithAccent(Color baseColor, Color accent, double ratio, byte alpha)
        {
            byte r = (byte)Math.Clamp(baseColor.R * (1 - ratio) + accent.R * ratio, 0, 255);
            byte g = (byte)Math.Clamp(baseColor.G * (1 - ratio) + accent.G * ratio, 0, 255);
            byte b = (byte)Math.Clamp(baseColor.B * (1 - ratio) + accent.B * ratio, 0, 255);
            return Color.FromArgb(alpha, r, g, b);
        }

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);
    }
}
