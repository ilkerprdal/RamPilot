namespace RamPilot.Core;

/// <summary>
/// Built-in hard deny list. Processes matching it are never opened for quota changes, even if a user adds them to
/// the allowlist in config.json. Covers anti-cheat (touching a protected game can get an account banned), game
/// clients/launchers, security software, and Windows-critical processes.
/// Matching is case-insensitive on the image name with '*' and '?' wildcards; path fragments match anywhere in
/// the full image path.
/// </summary>
public static class DenyList
{
    public static readonly IReadOnlyList<string> ExePatterns =
    [
        // Anti-cheat
        "EasyAntiCheat*", "EAAntiCheat*", "BEService*", "BEDaisy*", "BattlEye*", "vgc.exe", "vgtray.exe", "vgk*",
        "FACEIT*", "faceitclient*", "GameGuard*", "GameMon*", "nProtect*", "xigncode*", "x3.xem", "PnkBstr*",
        "mhyprot*", "ACE-*", "SGuard*", "TP3Helper*", "EOSOverlay*", "ricochet*", "atvi-*", "eac_*",
        // Game clients and launchers (their overlays are injected into games)
        "steam.exe", "steamwebhelper.exe", "steamservice.exe", "EpicGamesLauncher*", "EpicWebHelper*",
        "Battle.net*", "Agent.exe", "RiotClient*", "LeagueClient*", "VALORANT*", "EADesktop*", "EABackgroundService*",
        "Origin*.exe", "upc.exe", "UbisoftConnect*", "UplayWebCore*", "GalaxyClient*", "GOG Galaxy*",
        "XboxApp*", "XboxPcApp*", "GamingServices*", "GameBar*", "gamelaunchhelper*", "Rockstar*", "SocialClub*",
        // Security software
        "MsMpEng.exe", "MpDefenderCoreService.exe", "NisSrv.exe", "MsSense.exe", "SecurityHealth*", "smartscreen.exe",
        "avp*.exe", "kav*.exe", "ekrn.exe", "egui.exe", "bdagent*", "vsserv*", "bdservicehost*", "AvastSvc*",
        "AvastUI*", "avgsvc*", "AVGUI*", "mcshield*", "mfe*", "Norton*", "ns.exe", "MBAM*", "mbamtray*",
        "SentinelAgent*", "SentinelUI*", "CSFalcon*", "cyserver*", "cytray*", "SophosUI*", "Sophos*", "WRSA*",
        "ccSvcHst*", "CylanceSvc*", "elastic-agent*", "osqueryd*",
        // Windows-critical and this tool itself
        "System", "Registry", "Idle", "Memory Compression", "smss.exe", "csrss.exe", "wininit.exe", "winlogon.exe",
        "services.exe", "lsass.exe", "lsaiso.exe", "svchost.exe", "dwm.exe", "fontdrvhost.exe", "audiodg.exe",
        "explorer.exe", "sihost.exe", "ctfmon.exe", "conhost.exe", "RamPilot*",
    ];

    /// <summary>Install locations of games; a process running from here is never touched.</summary>
    public static readonly IReadOnlyList<string> PathFragments =
    [
        @"\steamapps\", @"\Epic Games\", @"\Riot Games\", @"\Battle.net\", @"\EA Games\", @"\Origin Games\",
        @"\Ubisoft Game Launcher\games\", @"\GOG Galaxy\Games\", @"\XboxGames\", @"\WindowsApps\Microsoft.Gaming",
    ];

    public static bool IsDeniedExe(string? exe)
    {
        if (string.IsNullOrEmpty(exe)) return true;
        foreach (var p in ExePatterns)
            if (Glob(p, exe)) return true;
        return false;
    }

    /// <summary>True if the full image path is a game install location. Unknown path is not denied by itself.</summary>
    public static bool IsDeniedPath(string? imagePath)
    {
        if (string.IsNullOrEmpty(imagePath)) return false;
        foreach (var f in PathFragments)
            if (imagePath.Contains(f, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>Case-insensitive glob with '*' (any run) and '?' (one char). Iterative, no allocation.</summary>
    public static bool Glob(string pattern, string text)
    {
        int p = 0, t = 0, star = -1, mark = 0;
        while (t < text.Length)
        {
            if (p < pattern.Length && (pattern[p] == '?' || char.ToUpperInvariant(pattern[p]) == char.ToUpperInvariant(text[t])))
            {
                p++; t++;
            }
            else if (p < pattern.Length && pattern[p] == '*')
            {
                star = p++; mark = t;
            }
            else if (star >= 0)
            {
                p = star + 1; t = ++mark;
            }
            else return false;
        }
        while (p < pattern.Length && pattern[p] == '*') p++;
        return p == pattern.Length;
    }
}
