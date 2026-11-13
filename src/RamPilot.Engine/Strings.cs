using System.Globalization;
using RamPilot.Win32;

namespace RamPilot;

/// <summary>Tray menu and tooltip text, English or Turkish by the OS UI language. Nothing here goes over IPC.</summary>
internal static class Strings
{
    private const ushort LangTurkish = 0x1F;

    public static bool Turkish { get; } = (Native.GetUserDefaultUILanguage() & 0x3FF) == LangTurkish;

    public static string OpenPanel => Turkish ? "Kontrol panelini aç" : "Open control panel";
    public static string Pause => Turkish ? "Duraklat" : "Pause";
    public static string Resume => Turkish ? "Devam et" : "Resume";
    public static string Quit => Turkish ? "Çıkış (sınırları kaldır)" : "Quit (release all limits)";
    public static string TooltipPaused => Turkish ? "RamPilot — duraklatıldı" : "RamPilot — paused";

    public static string TooltipNet(double netMb)
    {
        string mb = netMb.ToString("F0", CultureInfo.InvariantCulture);
        return Turkish ? $"RamPilot — net {mb} MB boşaltıldı" : $"RamPilot — {mb} MB net freed";
    }
}
