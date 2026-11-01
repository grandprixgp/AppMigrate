using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;

namespace TeaChest.Services;

public static class ThemeService
{
    private const int DwmUseImmersiveDarkMode = 20;
    private const int DwmWindowCornerPreference = 33;
    private const int DwmBorderColor = 34;
    private const int DwmCaptionColor = 35;
    private const int DwmTextColor = 36;
    private static readonly string SettingsDirectory = AppPaths.DataDirectory;
    private static readonly string SettingsPath = Path.Combine(SettingsDirectory, "settings.json");

    public static bool IsDark { get; private set; } = true;

    public static bool LoadPreference()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var settings = JsonSerializer.Deserialize<ThemeSettings>(File.ReadAllText(SettingsPath));
                IsDark = settings?.Dark ?? true;
            }
        }
        catch
        {
            IsDark = true;
        }
        return IsDark;
    }

    public static void SavePreference()
    {
        try
        {
            Directory.CreateDirectory(SettingsDirectory);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(new ThemeSettings { Dark = IsDark }));
        }
        catch
        {
            // Theme persistence is best-effort.
        }
    }

    public static void Apply(bool dark)
    {
        IsDark = dark;
        Application app = Application.Current;
        if (app == null) return;

        var dictionaries = app.Resources.MergedDictionaries;
        ResourceDictionary? oldTheme = dictionaries.FirstOrDefault(d =>
            d.Source != null &&
            (d.Source.OriginalString.EndsWith("Dark.xaml", StringComparison.OrdinalIgnoreCase) ||
             d.Source.OriginalString.EndsWith("Light.xaml", StringComparison.OrdinalIgnoreCase)));

        var newTheme = new ResourceDictionary
        {
            Source = new Uri(dark ? "Themes/Dark.xaml" : "Themes/Light.xaml", UriKind.Relative)
        };

        if (oldTheme != null)
        {
            int index = dictionaries.IndexOf(oldTheme);
            dictionaries[index] = newTheme;
        }
        else
        {
            dictionaries.Insert(0, newTheme);
        }

        foreach (Window window in app.Windows)
        {
            ApplyWindowTheme(window, dark);
        }
    }

    public static void ApplyWindowTheme(Window window, bool dark)
    {
        try
        {
            IntPtr handle = new WindowInteropHelper(window).Handle;
            if (handle == IntPtr.Zero) return;
            int value = dark ? 1 : 0;
            if (DwmSetWindowAttribute(handle, DwmUseImmersiveDarkMode, ref value, sizeof(int)) != 0)
            {
                DwmSetWindowAttribute(handle, 19, ref value, sizeof(int));
            }
            int corner = 2;
            DwmSetWindowAttribute(handle, DwmWindowCornerPreference, ref corner, sizeof(int));

            // Pin the non-client colors to the app palette. Without this, Windows tints
            // the caption with the user's accent color, which clashes with both themes.
            int border = dark ? 0x0026303A : 0x00C1D3DE;
            DwmSetWindowAttribute(handle, DwmBorderColor, ref border, sizeof(int));
            int caption = dark ? 0x000B0F12 : 0x00D7E6EE;
            DwmSetWindowAttribute(handle, DwmCaptionColor, ref caption, sizeof(int));
            int text = dark ? 0x00DFECF4 : 0x0018212A;
            DwmSetWindowAttribute(handle, DwmTextColor, ref text, sizeof(int));
        }
        catch
        {
            // Title bar theming is cosmetic; ignore unsupported versions.
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    private sealed class ThemeSettings
    {
        public bool Dark { get; set; } = true;
    }
}
