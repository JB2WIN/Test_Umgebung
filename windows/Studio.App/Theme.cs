using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;

namespace Lernheft.Studio.App;

/// <summary>Hell, dunkel oder wie Windows – mit passender Titelleiste.</summary>
public static class Theme
{
    public enum Mode { System, Light, Dark }

    public static Mode Current { get; private set; } = Mode.System;

    public static bool IsDark { get; private set; }

    public static event Action? Changed;

    private static readonly Dictionary<string, (string Light, string Dark)> Palette = new()
    {
        ["Chrome"] = ("#EEF1F6", "#111519"),
        ["Sidebar"] = ("#E6EAF1", "#15191F"),
        ["Surface"] = ("#FFFFFF", "#1C2128"),
        ["SurfaceAlt"] = ("#F4F6FA", "#232932"),
        ["Hover"] = ("#E3E8F0", "#2A313B"),
        ["Pressed"] = ("#D6DDE8", "#333B47"),
        ["Line"] = ("#D5DCE5", "#2E3640"),
        ["LineStrong"] = ("#B5C0CE", "#46505D"),
        ["Text"] = ("#0F1724", "#EEF2F6"),
        ["Muted"] = ("#4A5667", "#A9B4C1"),
        ["Faint"] = ("#6E7A8A", "#7D8896"),
        ["Accent"] = ("#2D4BE0", "#7B93FF"),
        ["AccentHover"] = ("#2340C9", "#92A6FF"),
        ["AccentSoft"] = ("#E1E7FD", "#26315A"),
        ["AccentText"] = ("#FFFFFF", "#0E1220"),
        ["Warn"] = ("#B45309", "#FFB35C"),
        ["WarnSoft"] = ("#FDF0DD", "#3A2D1A"),
        ["Good"] = ("#12804A", "#5ED39A"),
        ["GoodSoft"] = ("#DDF4E7", "#183327"),
        ["Bad"] = ("#C62D2D", "#FF8479"),
        ["BadSoft"] = ("#FBE3E3", "#3D1E1E"),
        ["Paper"] = ("#FDFDFA", "#1B1F26"),
        ["PaperLine"] = ("#C6D9E8", "#2D3948"),
        ["PaperMargin"] = ("#E68A80", "#6E3B38"),
        ["Ink"] = ("#16202C", "#E9EEF4"),
        ["Desk"] = ("#E3E7EE", "#0D1014"),
        ["Selection"] = ("#B9C8FB", "#33427A")
    };

    public static void Restore()
    {
        var saved = Services.Settings.Get(Keys.Appearance, "system");
        Apply(saved switch { "light" => Mode.Light, "dark" => Mode.Dark, _ => Mode.System }, save: false);
        SystemEvents.UserPreferenceChanged += (_, args) =>
        {
            if (args.Category == UserPreferenceCategory.General && Current == Mode.System)
            {
                Application.Current?.Dispatcher.BeginInvoke(() => Apply(Mode.System, save: false));
            }
        };
    }

    public static void Apply(Mode mode, bool save = true)
    {
        Current = mode;
        if (save) Services.Settings.Set(Keys.Appearance, mode switch { Mode.Light => "light", Mode.Dark => "dark", _ => "system" });
        var dark = mode switch { Mode.Dark => true, Mode.Light => false, _ => !SystemPrefersLight() };
        var changed = dark != IsDark;
        IsDark = dark;
        var resources = Application.Current.Resources;
        foreach (var (key, (light, darkHex)) in Palette)
        {
            var color = Hex(dark ? darkHex : light);
            if (resources[key] is SolidColorBrush brush && !brush.IsFrozen) brush.Color = color;
            else resources[key] = new SolidColorBrush(color);
        }
        resources["ShadowColor"] = dark ? Colors.Black : Hex("#5B6B80");
        foreach (Window window in Application.Current.Windows) ApplyTitleBar(window);
        if (changed || save) Changed?.Invoke();
    }

    public static string Label(Mode mode) => mode switch
    {
        Mode.Light => "Hell",
        Mode.Dark => "Dunkel",
        _ => "Wie Windows"
    };

    private static bool SystemPrefersLight()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return (key?.GetValue("AppsUseLightTheme") as int?) != 0;
        }
        catch (Exception)
        {
            return true;
        }
    }

    public static Color Hex(string hex)
    {
        var text = hex.TrimStart('#');
        if (text.Length == 8)
        {
            return Color.FromArgb(Convert.ToByte(text[..2], 16), Convert.ToByte(text.Substring(2, 2), 16),
                Convert.ToByte(text.Substring(4, 2), 16), Convert.ToByte(text.Substring(6, 2), 16));
        }
        if (text.Length != 6) return Color.FromRgb(0x1A, 0x1F, 0x2B);
        try
        {
            return Color.FromRgb(Convert.ToByte(text[..2], 16), Convert.ToByte(text.Substring(2, 2), 16),
                Convert.ToByte(text.Substring(4, 2), 16));
        }
        catch (FormatException)
        {
            return Color.FromRgb(0x1A, 0x1F, 0x2B);
        }
    }

    public static string ToHex(Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";

    /// <summary>
    /// Dunkle Tinte wird auf dunklem Papier hell – wie bei PencilKit auf dem iPad.
    /// Farbige Tinte bleibt farbig, wird aber etwas aufgehellt, damit sie lesbar ist.
    /// </summary>
    public static Color AdaptInk(Color color, bool dark)
    {
        if (!dark) return color;
        var luminance = (0.299 * color.R + 0.587 * color.G + 0.114 * color.B) / 255;
        var spread = Math.Max(color.R, Math.Max(color.G, color.B)) - Math.Min(color.R, Math.Min(color.G, color.B));
        if (luminance < 0.3 && spread < 60) return Color.FromArgb(color.A, 0xEB, 0xEF, 0xF4);
        if (luminance < 0.45)
        {
            // Farbe behalten, nur heller machen.
            byte Lift(byte value) => (byte)Math.Min(255, value + (255 - value) * 0.35);
            return Color.FromArgb(color.A, Lift(color.R), Lift(color.G), Lift(color.B));
        }
        return color;
    }

    // MARK: - Titelleiste

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    public static void ApplyTitleBar(Window window)
    {
        try
        {
            var handle = new WindowInteropHelper(window).Handle;
            if (handle == IntPtr.Zero)
            {
                window.SourceInitialized -= OnSourceInitialized;
                window.SourceInitialized += OnSourceInitialized;
                return;
            }
            var value = IsDark ? 1 : 0;
            DwmSetWindowAttribute(handle, 20, ref value, sizeof(int));
            // Titelleiste in der Farbe des Fensters (Windows 11).
            var chrome = (Application.Current.Resources["Chrome"] as SolidColorBrush)?.Color ?? Colors.White;
            var caption = chrome.R | (chrome.G << 8) | (chrome.B << 16);
            DwmSetWindowAttribute(handle, 35, ref caption, sizeof(int));
            var text = IsDark ? 0x00F6F2EE : 0x00241710;
            DwmSetWindowAttribute(handle, 36, ref text, sizeof(int));
        }
        catch (Exception)
        {
            // Ältere Windows-Fassungen kennen das nicht – dann eben die normale Leiste.
        }
    }

    private static void OnSourceInitialized(object? sender, EventArgs e)
    {
        if (sender is Window window) ApplyTitleBar(window);
    }
}
