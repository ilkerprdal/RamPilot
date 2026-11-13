using System.Runtime.InteropServices;

namespace RamPilot;

/// <summary>
/// Plain Win32 notification-area icon (Shell_NotifyIcon) on its own thread with a hidden window.
/// Left click: open the panel. Right click: menu (open panel / pause-resume / quit).
/// The icon is loaded from the exe's own icon resource at the small-icon size for the current DPI
/// (LoadIconMetric), falling back to RamPilot.ico next to the exe, then to the generic application icon.
/// </summary>
internal static unsafe class Tray
{
    public static Action? OpenPanel, TogglePause, Quit;
    public static Func<bool>? IsPaused;
    public static Func<string>? Tooltip;
    public static Action<string>? Log;

    private const uint WM_APP_TRAY = 0x8001, WM_COMMAND = 0x0111, WM_TIMER = 0x0113, WM_DESTROY = 0x0002, WM_CLOSE = 0x0010;
    private const uint WM_DPICHANGED = 0x02E0, WM_DISPLAYCHANGE = 0x007E;
    private const uint WM_LBUTTONUP = 0x0202, WM_RBUTTONUP = 0x0205, WM_CONTEXTMENU = 0x007B, NIN_SELECT = 0x0400;
    private const uint NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2, NIM_SETVERSION = 4;
    private const uint NIF_MESSAGE = 1, NIF_ICON = 2, NIF_TIP = 4, NIF_SHOWTIP = 0x80;
    private const int ID_OPEN = 1, ID_PAUSE = 2, ID_QUIT = 3;
    private const nint RT_GROUP_ICON = 14;
    private const int IDI_APPLICATION = 32512;
    private const int SM_CXSMICON = 49, SM_CYSMICON = 50;
    private static uint _taskbarCreated;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATAW
    {
        public uint cbSize; public nint hWnd; public uint uID; public uint uFlags; public uint uCallbackMessage; public nint hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState, dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags; public Guid guidItem; public nint hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEXW
    {
        public uint cbSize, style; public delegate* unmanaged<nint, uint, nint, nint, nint> lpfnWndProc;
        public int cbClsExtra, cbWndExtra; public nint hInstance, hIcon, hCursor, hbrBackground;
        public char* lpszMenuName; public char* lpszClassName; public nint hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int x, y; }
    [StructLayout(LayoutKind.Sequential)] private struct MSG { public nint hwnd; public uint message; public nint wParam, lParam; public uint time; public POINT pt; }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern int Shell_NotifyIconW(uint msg, ref NOTIFYICONDATAW d);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern ushort RegisterClassExW(ref WNDCLASSEXW c);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint CreateWindowExW(uint ex, char* cls, char* name, uint style, int x, int y, int w, int h, nint parent, nint menu, nint inst, nint param);
    [DllImport("user32.dll")] private static extern nint DefWindowProcW(nint h, uint m, nint w, nint l);
    [DllImport("user32.dll")] private static extern int GetMessageW(MSG* m, nint h, uint a, uint b);
    [DllImport("user32.dll")] private static extern int TranslateMessage(MSG* m);
    [DllImport("user32.dll")] private static extern nint DispatchMessageW(MSG* m);
    [DllImport("user32.dll")] private static extern void PostQuitMessage(int code);
    [DllImport("user32.dll")] private static extern int PostMessageW(nint h, uint m, nint w, nint l);
    [DllImport("user32.dll")] private static extern int DestroyWindow(nint h);
    [DllImport("user32.dll")] private static extern nint CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int AppendMenuW(nint m, uint flags, nint id, string? text);
    [DllImport("user32.dll")] private static extern int TrackPopupMenu(nint m, uint flags, int x, int y, int r, nint h, nint rect);
    [DllImport("user32.dll")] private static extern int DestroyMenu(nint m);
    [DllImport("user32.dll")] private static extern int SetForegroundWindow(nint h);
    [DllImport("user32.dll")] private static extern int GetCursorPos(out POINT p);
    [DllImport("user32.dll")] private static extern nint SetTimer(nint h, nint id, uint ms, nint fn);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessageW(string s);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint LoadImageW(nint inst, string name, uint type, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern nint LoadIconW(nint inst, nint name);
    [DllImport("user32.dll")] private static extern int DestroyIcon(nint h);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("comctl32.dll")] private static extern int LoadIconMetric(nint inst, nint name, int lims, out nint icon);
    [DllImport("kernel32.dll")] private static extern nint GetModuleHandleW(char* name);
    [DllImport("kernel32.dll")] private static extern int EnumResourceNamesW(nint module, nint type, delegate* unmanaged<nint, nint, nint, nint, int> proc, nint param);

    private static nint _hwnd, _icon;
    private static bool _iconOwned;
    private static nint _iconResourceId;

    /// <summary>Which source the current icon came from (for logs and tests).</summary>
    public static string IconSource { get; private set; } = "none";

    public static void Start()
    {
        var t = new Thread(Run) { IsBackground = true, Name = "tray" };
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
    }

    public static void Stop()
    {
        if (_hwnd != 0) _ = PostMessageW(_hwnd, WM_CLOSE, 0, 0);
    }

    private static void Run()
    {
        nint inst = GetModuleHandleW(null);
        fixed (char* cls = "RamPilotTray")
        {
            var wc = new WNDCLASSEXW { cbSize = (uint)sizeof(WNDCLASSEXW), lpfnWndProc = &WndProc, lpszClassName = cls, hInstance = inst };
            _ = RegisterClassExW(ref wc);
            _hwnd = CreateWindowExW(0, cls, cls, 0, 0, 0, 0, 0, 0, 0, inst, 0);
        }
        _taskbarCreated = RegisterWindowMessageW("TaskbarCreated");
        LoadIcon();
        AddIcon();
        _ = SetTimer(_hwnd, 1, 5000, 0);   // refresh the tooltip

        MSG m;
        while (GetMessageW(&m, 0, 0, 0) > 0)
        {
            _ = TranslateMessage(&m);
            _ = DispatchMessageW(&m);
        }
    }

    /// <summary>Resource id of the first RT_GROUP_ICON in the exe (32512 for csc/ILC builds, but checked, not assumed).</summary>
    private static nint FindIconResource(nint inst)
    {
        _iconResourceId = 0;
        _ = EnumResourceNamesW(inst, RT_GROUP_ICON, &OnIconResource, 0);
        return _iconResourceId;
    }

    [UnmanagedCallersOnly]
    private static int OnIconResource(nint module, nint type, nint name, nint param)
    {
        if ((ulong)name <= 0xFFFF) { _iconResourceId = name; return 0; }   // integer id: done
        return 1;
    }

    private static void LoadIcon()
    {
        nint old = _icon;
        bool oldOwned = _iconOwned;
        nint inst = GetModuleHandleW(null);
        _icon = 0;
        nint id = FindIconResource(inst);
        try
        {
            if (id != 0 && LoadIconMetric(inst, id, 0 /* LIM_SMALL */, out nint h) >= 0 && h != 0)
            {
                _icon = h; _iconOwned = true; IconSource = $"resource {id}";
            }
        }
        catch (EntryPointNotFoundException) { }   // comctl32 v5 (no manifest)
        if (_icon == 0)
        {
            var ico = Path.Combine(AppContext.BaseDirectory, "RamPilot.ico");
            if (File.Exists(ico))
            {
                nint h = LoadImageW(0, ico, 1 /*IMAGE_ICON*/, GetSystemMetrics(SM_CXSMICON), GetSystemMetrics(SM_CYSMICON), 0x10 /*LR_LOADFROMFILE*/);
                if (h != 0) { _icon = h; _iconOwned = true; IconSource = "file"; }
            }
        }
        if (_icon == 0)
        {
            _icon = LoadIconW(0, IDI_APPLICATION);
            _iconOwned = false;
            IconSource = "generic";
        }
        Log?.Invoke($"tray icon: {IconSource}, {GetSystemMetrics(SM_CXSMICON)} px");
        if (old != 0 && oldOwned) _ = DestroyIcon(old);
    }

    private static NOTIFYICONDATAW Data(uint flags) => new()
    {
        cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATAW>(),
        hWnd = _hwnd,
        uID = 1,
        uFlags = flags,
        uCallbackMessage = WM_APP_TRAY,
        hIcon = _icon,
        szTip = Trim(Tooltip?.Invoke() ?? "RamPilot"),
        szInfo = "",
        szInfoTitle = "",
        uVersion = 4,
    };

    private static string Trim(string s) => s.Length > 127 ? s[..127] : s;

    private static void AddIcon()
    {
        var d = Data(NIF_MESSAGE | NIF_ICON | NIF_TIP | NIF_SHOWTIP);
        if (Shell_NotifyIconW(NIM_ADD, ref d) == 0) Log?.Invoke("tray: Shell_NotifyIcon(NIM_ADD) failed");
        _ = Shell_NotifyIconW(NIM_SETVERSION, ref d);
    }

    private static void ShowMenu()
    {
        nint menu = CreatePopupMenu();
        _ = AppendMenuW(menu, 0, ID_OPEN, Strings.OpenPanel);
        _ = AppendMenuW(menu, 0, ID_PAUSE, IsPaused?.Invoke() == true ? Strings.Resume : Strings.Pause);
        _ = AppendMenuW(menu, 0x800 /*MF_SEPARATOR*/, 0, null);
        _ = AppendMenuW(menu, 0, ID_QUIT, Strings.Quit);
        _ = GetCursorPos(out var p);
        _ = SetForegroundWindow(_hwnd);
        _ = TrackPopupMenu(menu, 0x0020 /*TPM_RIGHTALIGN*/ | 0x0008 /*TPM_BOTTOMALIGN*/, p.x, p.y, 0, _hwnd, 0);
        _ = DestroyMenu(menu);
    }

    [UnmanagedCallersOnly]
    private static nint WndProc(nint h, uint msg, nint w, nint l)
    {
        try
        {
            if (msg == WM_APP_TRAY)
            {
                uint ev = (uint)(l & 0xFFFF);
                if (ev == WM_LBUTTONUP || ev == NIN_SELECT) OpenPanel?.Invoke();
                else if (ev == WM_RBUTTONUP || ev == WM_CONTEXTMENU) ShowMenu();
                return 0;
            }
            if (msg == WM_COMMAND)
            {
                switch ((int)(w & 0xFFFF))
                {
                    case ID_OPEN: OpenPanel?.Invoke(); break;
                    case ID_PAUSE: TogglePause?.Invoke(); break;
                    case ID_QUIT: Quit?.Invoke(); break;
                }
                return 0;
            }
            if (msg == WM_TIMER)
            {
                var d = Data(NIF_TIP | NIF_SHOWTIP);
                _ = Shell_NotifyIconW(NIM_MODIFY, ref d);
                return 0;
            }
            if (msg == WM_DPICHANGED || msg == WM_DISPLAYCHANGE)
            {
                LoadIcon();
                var d = Data(NIF_ICON);
                _ = Shell_NotifyIconW(NIM_MODIFY, ref d);
                return 0;
            }
            if (msg == _taskbarCreated && _taskbarCreated != 0)   // explorer restarted
            {
                LoadIcon();
                AddIcon();
                return 0;
            }
            if (msg == WM_CLOSE)
            {
                var d = Data(0);
                _ = Shell_NotifyIconW(NIM_DELETE, ref d);
                _ = DestroyWindow(h);
                return 0;
            }
            if (msg == WM_DESTROY)
            {
                PostQuitMessage(0);
                return 0;
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log?.Invoke("tray: " + ex.Message);
        }
        return DefWindowProcW(h, msg, w, l);
    }
}
