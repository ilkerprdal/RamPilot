using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using Microsoft.Win32;

namespace RamPilot.Panel;

internal sealed class MainForm : Form
{
    private const string Host = "panel.rampilot";
    private static readonly Color DarkBg = Color.FromArgb(0x1f, 0x1f, 0x1f);
    private static readonly Color LightBg = Color.FromArgb(0xf3, 0xf3, 0xf3);

    private readonly WebView2 _web = new() { Dock = DockStyle.Fill };
    private readonly IEngineLink _link;
    private readonly PanelOptions _options;
    private readonly string _osLang = Native.OsUiLanguage();
    private readonly CancellationTokenSource _cts = new();
    private string? _lastEngine;
    private string? _lastStatus;
    private bool _pageReady;

    public MainForm(IEngineLink link, PanelOptions options)
    {
        _link = link;
        _options = options;
        Text = "RamPilot";
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        // Tall enough to show the app list without scrolling; clamp to the work area on small screens.
        var workArea = Screen.FromPoint(Cursor.Position).WorkingArea;
        Size = new Size(
            Math.Min(LogicalToDeviceUnits(1040), workArea.Width - 40),
            Math.Min(LogicalToDeviceUnits(1000), workArea.Height - 40));
        MinimumSize = new Size(LogicalToDeviceUnits(860), LogicalToDeviceUnits(560));
        // Load the multi-size .ico (16..256 px) so the caption and taskbar each get a crisp frame;
        // ExtractAssociatedIcon only yields a single 32 px image that is downscaled in the caption.
        using (var iconStream = typeof(MainForm).Assembly.GetManifestResourceStream("RamPilot.Panel.RamPilot.ico"))
        {
            if (iconStream is not null) Icon = new Icon(iconStream);
        }
        ApplyTheme();
        Controls.Add(_web);
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        _link.Message += OnLinkMessage;
    }

    private bool Dark => _options.Theme switch
    {
        "dark" => true,
        "light" => false,
        _ => Native.SystemUsesDarkTheme(),
    };

    private void ApplyTheme()
    {
        var bg = Dark ? DarkBg : LightBg;
        BackColor = bg;
        _web.DefaultBackgroundColor = bg;
        if (IsHandleCreated)
        {
            Native.ApplyTitleBar(Handle, Dark, bg);
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Native.ApplyTitleBar(Handle, Dark, Dark ? DarkBg : LightBg);
    }

    private void OnUserPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category is UserPreferenceCategory.General or UserPreferenceCategory.Color)
        {
            BeginInvokeSafe(ApplyTheme);
        }
    }

    protected override async void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        try
        {
            string userData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RamPilot", "PanelWebView2");
            var env = await CoreWebView2Environment.CreateAsync(null, userData).ConfigureAwait(true);
            await _web.EnsureCoreWebView2Async(env).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is WebView2RuntimeNotFoundException or InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            // The page cannot load, so this is the only text shown from C#.
            string msg = _osLang == "tr"
                ? "Kontrol paneli için Microsoft Edge WebView2 Runtime gerekiyor."
                : "The control panel requires the Microsoft Edge WebView2 Runtime.";
            MessageBox.Show(this, msg + "\n\n" + ex.Message, "RamPilot", MessageBoxButtons.OK, MessageBoxIcon.Error);
            Close();
            return;
        }

        var core = _web.CoreWebView2;
        if (_options.Theme is not null)
        {
            core.Profile.PreferredColorScheme = Dark ? CoreWebView2PreferredColorScheme.Dark : CoreWebView2PreferredColorScheme.Light;
        }
        var s = core.Settings;
        s.AreDefaultContextMenusEnabled = false;
        s.AreDevToolsEnabled = _options.DevTools;
        s.IsStatusBarEnabled = false;
        s.IsZoomControlEnabled = false;
        s.AreBrowserAcceleratorKeysEnabled = false;
        s.IsGeneralAutofillEnabled = false;
        s.IsPasswordAutosaveEnabled = false;

        core.SetVirtualHostNameToFolderMapping(Host, Path.Combine(AppContext.BaseDirectory, "ui"),
            CoreWebView2HostResourceAccessKind.DenyCors);
        core.NavigationStarting += (_, a) =>
        {
            if (!a.Uri.StartsWith($"https://{Host}/", StringComparison.OrdinalIgnoreCase))
            {
                a.Cancel = true;
            }
        };
        core.NewWindowRequested += (_, a) => a.Handled = true;
        core.DocumentTitleChanged += (_, _) => Text = string.IsNullOrWhiteSpace(core.DocumentTitle) ? "RamPilot" : core.DocumentTitle;
        core.WebMessageReceived += OnWebMessage;

        string query = $"?osLang={_osLang}"
            + (_options.Lang is null ? "" : $"&lang={_options.Lang}")
            + (_options.Demo ? "&demo=1" : "");
        core.Navigate($"https://{Host}/index.html{query}");

        _ = Task.Run(() => _link.RunAsync(_cts.Token));
    }

    private void OnLinkMessage(string json)
    {
        // Arrives on the link's thread; WebView2 may only be used on the UI thread.
        BeginInvokeSafe(() =>
        {
            if (json.StartsWith("{\"type\":\"status\"", StringComparison.Ordinal))
            {
                _lastStatus = json;
                _lastEngine = null;
            }
            else if (json.StartsWith("{\"type\":\"engine\"", StringComparison.Ordinal))
            {
                _lastEngine = json;
            }
            if (_pageReady)
            {
                _web.CoreWebView2?.PostWebMessageAsJson(json);
            }
        });
    }

    private void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        JsonObject? o;
        try
        {
            o = JsonNode.Parse(e.WebMessageAsJson) as JsonObject;
        }
        catch (JsonException)
        {
            return;
        }
        if (o is null)
        {
            return;
        }

        switch (Str(o, "panel"))
        {
            case "ready":
                _pageReady = true;
                if (_lastStatus is not null)
                {
                    _web.CoreWebView2.PostWebMessageAsJson(_lastStatus);
                }
                if (_lastEngine is not null)
                {
                    _web.CoreWebView2.PostWebMessageAsJson(_lastEngine);
                }
                return;
            case "startEngine":
                _link.StartEngine();
                return;
        }

        // Rebuild the page's command per the IPC contract: only known fields pass through.
        JsonObject? cmd = Str(o, "cmd") switch
        {
            "pause" => new JsonObject { ["cmd"] = "pause" },
            "resume" => new JsonObject { ["cmd"] = "resume" },
            "quit" => new JsonObject { ["cmd"] = "quit" },
            "setAutostart" when Bool(o, "enabled") is bool en =>
                new JsonObject { ["cmd"] = "setAutostart", ["enabled"] = en },
            "setApp" when Str(o, "exe") is { Length: > 0 and <= 260 } exe && Bool(o, "enabled") is bool en =>
                new JsonObject { ["cmd"] = "setApp", ["exe"] = exe, ["enabled"] = en },
            _ => null,
        };
        if (cmd is not null)
        {
            _link.Send(cmd.ToJsonString());
        }
    }

    private static string? Str(JsonObject o, string key) =>
        o[key] is JsonValue v && v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : null;

    private static bool? Bool(JsonObject o, string key) =>
        o[key] is JsonValue v && v.GetValueKind() is JsonValueKind.True or JsonValueKind.False ? v.GetValue<bool>() : null;

    private void BeginInvokeSafe(Action a)
    {
        if (IsDisposed || !IsHandleCreated)
        {
            return;
        }
        try
        {
            BeginInvoke(a);
        }
        catch (InvalidOperationException)
        {
            // Window is closing.
        }
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        // Closing the window ends the process completely: stop the link loop and release WebView2.
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        _link.Message -= OnLinkMessage;
        _cts.Cancel();
        _web.Dispose();
        base.OnFormClosed(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _cts.Dispose();
            _web.Dispose();
        }
        base.Dispose(disposing);
    }
}
