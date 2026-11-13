using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace RamPilot.Panel;

internal static partial class Native
{
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaCaptionColor = 35;
    private const int SwRestore = 9;
    private const int LangTurkish = 0x1F;

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetForegroundWindow(IntPtr hwnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ShowWindow(IntPtr hwnd, int cmd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsIconic(IntPtr hwnd);

    [LibraryImport("kernel32.dll")]
    private static partial ushort GetUserDefaultUILanguage();

    /// <summary>"tr" when the Windows display language is Turkish, otherwise "en".
    /// (InvariantGlobalization is on, so CultureInfo cannot be used for this.)</summary>
    public static string OsUiLanguage() => (GetUserDefaultUILanguage() & 0x3FF) == LangTurkish ? "tr" : "en";

    /// <summary>Whether apps use the dark theme (Settings → Personalization → Colors).</summary>
    public static bool SystemUsesDarkTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is not int v || v == 0;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    /// <summary>Matches the title bar to the theme; <paramref name="caption"/> is the page background.</summary>
    public static void ApplyTitleBar(IntPtr hwnd, bool dark, Color caption)
    {
        int on = dark ? 1 : 0;
        _ = DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref on, sizeof(int));
        int colorRef = caption.R | (caption.G << 8) | (caption.B << 16); // COLORREF = 0x00BBGGRR
        _ = DwmSetWindowAttribute(hwnd, DwmwaCaptionColor, ref colorRef, sizeof(int));
    }

    public static void ActivateOtherInstance()
    {
        using var self = Process.GetCurrentProcess();
        foreach (var p in Process.GetProcessesByName("RamPilot.Panel"))
        {
            using (p)
            {
                if (p.Id == self.Id || p.MainWindowHandle == IntPtr.Zero)
                {
                    continue;
                }
                if (IsIconic(p.MainWindowHandle))
                {
                    ShowWindow(p.MainWindowHandle, SwRestore);
                }
                SetForegroundWindow(p.MainWindowHandle);
                return;
            }
        }
    }
}
