using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;

namespace RamPilot.Panel;

/// <summary>
/// <c>--demo</c>: produces realistic protocol v2 Status data without an engine, and reacts to commands
/// (pause, per-app toggle, autostart, quit, start) the way the engine would.
/// The model: each managed app has an "uncapped" trajectory (what its private WS would be without a cap);
/// caps descend gradually (×0.85 per step) toward a learned floor; reclaimed = uncapped − current;
/// the compression store absorbs part of it, so net &lt; reclaimed.
/// </summary>
internal sealed class DemoLink : IEngineLink
{
    private const double TotalMb = 15996;
    private const double StoreCostRatio = 0.24;   // dirty pages compressed at roughly 1/3, ~70 % of trims dirty
    private const int DescentStepSec = 10;          // real engine: 30 s; faster here so the demo moves

    private sealed class App
    {
        public required string Exe;
        public required string Name;
        public required string Role;
        public bool Enabled = true;
        public bool Running = true;
        public bool Focused;
        public bool Audio;
        public int Procs;
        public double Shared;     // WS - private WS
        public double Uncapped;   // estimated private WS without a cap
        public double Private;    // current private WS
        public double Learned;    // learned floor cap
        public double? Cap;
        public string Phase = "Holding"; // Descending | Holding | Mixed | Free
        public int PhaseSec;
    }

    private readonly Channel<string> _commands = Channel.CreateUnbounded<string>();
    private readonly Random _rnd = new(7);
    private readonly List<App> _apps;
    private readonly List<JsonObject> _events = [];
    private readonly List<(long T, double Avail, double Managed, double Net)> _history = [];
    private bool _paused;
    private bool _autostart = true;
    private bool _running = true;
    private int _uptime = 1864;
    private int _tick;
    private int _startCountdown = -1;
    private double _aggressiveness = 0.35;

    public DemoLink()
    {
        _apps =
        [
            new App { Exe = "WhatsApp.Root.exe", Name = "WhatsApp", Role = "renderer", Procs = 9, Shared = 114, Uncapped = 382, Learned = 118, Cap = 118, Private = 114 },
            new App { Exe = "Discord.exe", Name = "Discord", Role = "renderer", Procs = 7, Shared = 141, Uncapped = 418, Learned = 170, Private = 418, Audio = true, Phase = "Free" },
            new App { Exe = "ChatGPT.exe", Name = "ChatGPT", Role = "renderer", Procs = 6, Shared = 98, Uncapped = 301, Learned = 112, Cap = 174, Private = 171, Phase = "Descending", PhaseSec = 4 },
            new App { Exe = "ms-teams.exe", Name = "Microsoft Teams", Role = "renderer", Procs = 8, Shared = 162, Uncapped = 494, Learned = 214, Cap = 278, Private = 262, Phase = "Mixed", PhaseSec = 0 },
            new App { Exe = "Spotify.exe", Name = "Spotify", Role = "main", Procs = 5, Shared = 84, Uncapped = 241, Learned = 120, Private = 241, Focused = true, Phase = "Free" },
            new App { Exe = "Notion.exe", Name = "Notion", Role = "renderer", Procs = 6, Shared = 90, Uncapped = 268, Learned = 140, Private = 268, Enabled = false, Phase = "Free" },
            new App { Exe = "slack.exe", Name = "Slack", Role = "main", Running = false, Phase = "Free" },
        ];
        SeedHistory();
        SeedEvents();
    }

    public event Action<string>? Message;

    public void Send(string commandJson) => _commands.Writer.TryWrite(commandJson);

    public void StartEngine()
    {
        if (_running)
        {
            return;
        }
        Message?.Invoke(Envelopes.Engine("starting"));
        _startCountdown = 2;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        Message?.Invoke(Envelopes.Engine("connecting"));
        try
        {
            await Task.Delay(250, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        while (!ct.IsCancellationRequested)
        {
            while (_commands.Reader.TryRead(out var cmd))
            {
                Handle(cmd);
            }

            if (_running)
            {
                Step();
                Message?.Invoke(Envelopes.Status(BuildStatus().ToJsonString()));
            }
            else if (_startCountdown > 0)
            {
                if (--_startCountdown == 0)
                {
                    Restart();
                    continue;
                }
            }
            else
            {
                Message?.Invoke(Envelopes.Engine("down"));
            }

            using var tick = CancellationTokenSource.CreateLinkedTokenSource(ct);
            tick.CancelAfter(1000);
            try
            {
                await _commands.Reader.WaitToReadAsync(tick.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private void Restart()
    {
        _running = true;
        _paused = false;
        _uptime = 0;
        _startCountdown = -1;
        _history.Clear();
        foreach (var a in _apps)
        {
            a.Cap = null;
            a.Private = a.Uncapped;
            a.Phase = "Free";
            a.PhaseSec = 0;
        }
        AddEvent("info", "engine_started");
    }

    private void Handle(string json)
    {
        JsonObject? o;
        try
        {
            o = JsonNode.Parse(json) as JsonObject;
        }
        catch (JsonException)
        {
            return;
        }
        string? cmd = o?["cmd"]?.GetValue<string>();
        string? error = null;
        if (!_running)
        {
            error = "engine_down";
        }
        else
        {
            switch (cmd)
            {
                case "pause":
                    _paused = true;
                    AddEvent("info", "paused");
                    foreach (var a in _apps.Where(a => a.Cap is not null))
                    {
                        Release(a, "paused");
                    }
                    break;
                case "resume":
                    _paused = false;
                    AddEvent("info", "resumed");
                    break;
                case "setAutostart":
                    _autostart = o!["enabled"]!.GetValue<bool>();
                    AddEvent("info", _autostart ? "autostart_on" : "autostart_off");
                    break;
                case "setApp":
                    var exe = o!["exe"]!.GetValue<string>();
                    var app = _apps.FirstOrDefault(x => x.Exe.Equals(exe, StringComparison.OrdinalIgnoreCase));
                    if (app is null)
                    {
                        error = "not_allowed";
                        break;
                    }
                    app.Enabled = o["enabled"]!.GetValue<bool>();
                    AddEvent("info", app.Enabled ? "app_enabled" : "app_disabled", new() { ["app"] = app.Exe });
                    if (!app.Enabled && app.Cap is not null)
                    {
                        Release(app, "disabled");
                    }
                    break;
                case "quit":
                    foreach (var a in _apps.Where(a => a.Cap is not null))
                    {
                        Release(a, "paused");
                    }
                    _running = false;
                    _startCountdown = -1;
                    break;
                default:
                    error = "unknown_cmd";
                    break;
            }
        }
        Message?.Invoke(Envelopes.Result(cmd, error is null, error));
        if (!_running && error is null)
        {
            Message?.Invoke(Envelopes.Engine("down"));
        }
    }

    private bool Managed(App a) => a.Running && a.Enabled && !_paused;

    private bool Eligible(App a) => Managed(a) && !a.Focused && !a.Audio;

    private void Release(App a, string reason)
    {
        a.Cap = null;
        a.Phase = "Free";
        a.PhaseSec = 0;
        AddEvent("free", "cap_released", new() { ["app"] = a.Exe, ["role"] = a.Role, ["reason"] = reason });
    }

    private void StartDescent(App a)
    {
        a.Cap = Math.Round(a.Private * 0.8);
        a.Phase = "Descending";
        a.PhaseSec = 0;
        AddEvent("cap", "descent_started", new() { ["app"] = a.Exe, ["role"] = a.Role, ["capMb"] = a.Cap });
    }

    private void Step()
    {
        _tick++;
        _uptime++;
        var spotify = _apps[4];
        var chat = _apps[2];
        var discord = _apps[1];

        // Focus moves around now and then: Spotify -> ChatGPT -> nobody -> Spotify.
        if (_tick % 50 == 0)
        {
            if (spotify.Focused)
            {
                spotify.Focused = false;
                chat.Focused = true;
                if (chat.Cap is not null)
                {
                    Release(chat, "focus");
                }
            }
            else if (chat.Focused)
            {
                chat.Focused = false;
            }
            else
            {
                spotify.Focused = true;
                if (spotify.Cap is not null)
                {
                    Release(spotify, "focus");
                }
            }
        }

        // Discord's voice call ends and starts again.
        if (_tick % 140 == 0)
        {
            discord.Audio = !discord.Audio;
            if (discord.Audio && discord.Cap is not null)
            {
                Release(discord, "audio");
            }
        }

        foreach (var a in _apps.Where(a => a.Running))
        {
            a.PhaseSec++;
            a.Uncapped += (_rnd.NextDouble() - 0.45) * 0.6; // slow organic growth

            if (!Eligible(a))
            {
                if (a.Cap is not null)
                {
                    Release(a, a.Audio ? "audio" : a.Focused ? "focus" : "paused");
                }
                a.Private += (a.Uncapped - a.Private) * 0.06;
                continue;
            }

            if (a.Cap is null && a.PhaseSec >= 8)
            {
                StartDescent(a);
            }
            else if (a.Phase == "Descending" && a.PhaseSec % DescentStepSec == 0)
            {
                double prev = a.Cap!.Value;
                double next = Math.Max(a.Learned, Math.Round(prev * (1 - 0.15 * (_aggressiveness / 0.35))));
                if (next < prev)
                {
                    a.Cap = next;
                    AddEvent("cap", "cap_lowered", new() { ["app"] = a.Exe, ["role"] = a.Role, ["capMb"] = next, ["prevCapMb"] = prev });
                }
                if (next <= a.Learned)
                {
                    a.Phase = "Holding";
                }
            }
            else if (a.Phase == "Mixed" && a.PhaseSec >= 40)
            {
                // The raised process settles; descend back toward the learned cap.
                a.Phase = "Descending";
                a.PhaseSec = 0;
            }

            // Teams occasionally refaults: the cap goes up, then descends again.
            if (a.Exe == "ms-teams.exe" && a.Phase == "Holding" && a.PhaseSec % 75 == 0)
            {
                double prev = a.Cap!.Value;
                a.Cap = Math.Round(prev * 1.3);
                a.Phase = "Mixed";
                a.PhaseSec = 0;
                AddEvent("warn", "cap_raised", new() { ["app"] = a.Exe, ["role"] = a.Role, ["capMb"] = a.Cap, ["prevCapMb"] = prev, ["reason"] = "refaults" });
            }

            if (a.Cap is double cap)
            {
                double target = Math.Min(a.Uncapped, cap * 0.97);
                a.Private += (target - a.Private) * 0.15;
            }
            a.Private += (_rnd.NextDouble() - 0.5) * 1.6;
        }

        double ratio = Avail() / TotalMb;
        double targetAggr = ratio > 0.4 ? 0.35 : ratio > 0.2 ? 0.6 : 0.9;
        _aggressiveness += (targetAggr - _aggressiveness) * 0.1;

        if (_tick % 10 == 0)
        {
            _history.Add((DateTimeOffset.UtcNow.ToUnixTimeSeconds(), Avail(), ManagedMb(), Net()));
            if (_history.Count > 180)
            {
                _history.RemoveAt(0);
            }
        }
    }

    private double ReclaimedOf(App a)
    {
        double gap = Math.Max(0, a.Uncapped - a.Private);
        // A released app regrows toward its trajectory; once within noise it has nothing reclaimed.
        return !Managed(a) || (a.Cap is null && gap < 4) ? 0 : gap;
    }

    private double Reclaimed() => _apps.Sum(ReclaimedOf);

    private double StoreCost() => Reclaimed() * StoreCostRatio;

    private double Net() => Reclaimed() - StoreCost();

    private double ManagedMb() => _apps.Where(Managed).Sum(a => a.Private);

    private double Compression() => 182 + StoreCost() + (Math.Sin(_uptime / 90.0) * 6);

    private double Avail() => 6420 + (Net() * 0.92) + (Math.Sin(_uptime / 140.0) * 110) + (Math.Sin(_uptime / 23.0) * 18);

    private string StateOf(App a)
    {
        if (!a.Running)
        {
            return "NotRunning";
        }
        if (!a.Enabled || _paused)
        {
            return "Disabled";
        }
        return a.Cap is null ? "Free" : a.Phase;
    }

    private JsonObject BuildStatus()
    {
        var apps = new JsonArray();
        foreach (var a in _apps)
        {
            string state = StateOf(a);
            apps.Add(new JsonObject
            {
                ["exe"] = a.Exe,
                ["name"] = a.Name,
                ["enabled"] = a.Enabled,
                ["running"] = a.Running,
                ["focused"] = a.Running && a.Focused,
                ["audio"] = a.Running && a.Audio,
                ["state"] = state,
                ["procCount"] = a.Running ? a.Procs : 0,
                ["wsMb"] = a.Running ? Math.Round(a.Private + a.Shared, 1) : 0,
                ["privateWsMb"] = a.Running ? Math.Round(a.Private, 1) : 0,
                ["capMb"] = a.Cap,
                ["reclaimedMb"] = Math.Round(ReclaimedOf(a), 1),
            });
        }

        var history = new JsonArray();
        foreach (var h in _history)
        {
            history.Add(new JsonObject { ["t"] = h.T, ["availMb"] = Math.Round(h.Avail), ["managedMb"] = Math.Round(h.Managed), ["netMb"] = Math.Round(h.Net, 1) });
        }

        var events = new JsonArray();
        foreach (var e in _events)
        {
            events.Add(e.DeepClone());
        }

        double avail = Avail();
        double ratio = avail / TotalMb;
        return new JsonObject
        {
            ["protocol"] = PipeLink.ProtocolVersion,
            ["engineVersion"] = "0.1.0",
            ["paused"] = _paused,
            ["autostart"] = _autostart,
            ["uptimeSec"] = _uptime,
            ["engine"] = new JsonObject { ["wsMb"] = Math.Round(11.8 + (Math.Sin(_uptime / 7.0) * 0.3), 1), ["privateMb"] = 4.9 },
            ["system"] = new JsonObject
            {
                ["totalMb"] = TotalMb,
                ["availMb"] = Math.Round(avail),
                ["compressionMb"] = Math.Round(Compression()),
                ["loadPct"] = (int)Math.Round(100 * (1 - ratio)),
                ["commitPct"] = (int)Math.Round(58 + (Math.Sin(_uptime / 200.0) * 3)),
            },
            ["governor"] = new JsonObject
            {
                ["pressure"] = ratio > 0.4 ? "low" : ratio > 0.2 ? "medium" : "high",
                ["aggressiveness"] = Math.Round(_aggressiveness, 2),
            },
            ["savings"] = new JsonObject
            {
                ["reclaimedMb"] = Math.Round(Reclaimed(), 1),
                ["storeCostMb"] = Math.Round(StoreCost(), 1),
                ["netMb"] = Math.Round(Net(), 1),
            },
            ["apps"] = apps,
            ["events"] = events,
            ["history"] = history,
        };
    }

    private void AddEvent(string kind, string code, JsonObject? fields = null, long? at = null)
    {
        var e = new JsonObject { ["t"] = at ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds(), ["kind"] = kind, ["code"] = code };
        if (fields is not null)
        {
            foreach (var (k, v) in fields)
            {
                e[k] = v?.DeepClone();
            }
        }
        _events.Add(e);
        if (_events.Count > 50)
        {
            _events.RemoveAt(0);
        }
    }

    private void SeedHistory()
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        double finalNet = Net();
        for (int i = 179; i >= 0; i--)
        {
            double age = ((179 - i) * 10.0) + 40;               // seconds since the engine started
            double ramp = 1 - Math.Exp(-age / 420.0);           // gradual descent, not a slam
            double dip = i is > 95 and < 104 ? 0.78 : i is > 40 and < 46 ? 0.88 : 1.0; // focus moments
            double net = finalNet * ramp * dip * (1 + (Math.Sin(age / 75.0) * 0.012));
            int u = _uptime - (i * 10);
            double avail = 6420 + (net * 0.92) + (Math.Sin(u / 140.0) * 110) + (Math.Sin(u / 23.0) * 18)
                           - (i is > 120 and < 132 ? 360 : 0);
            _history.Add((now - (i * 10), avail, 1850 - (net * 1.3), net));
        }
    }

    private void SeedEvents()
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        void At(int ago, string kind, string code, JsonObject? f = null) => AddEvent(kind, code, f, now - ago);
        JsonObject F(string exe, string role, double? cap = null, double? prev = null, string? reason = null)
        {
            var o = new JsonObject { ["app"] = exe, ["role"] = role };
            if (cap is not null)
            {
                o["capMb"] = cap;
            }
            if (prev is not null)
            {
                o["prevCapMb"] = prev;
            }
            if (reason is not null)
            {
                o["reason"] = reason;
            }
            return o;
        }

        At(1862, "info", "engine_started");
        At(1861, "warn", "leftover_cleared", new() { ["app"] = "WhatsApp.Root.exe", ["count"] = 2 });
        At(1860, "info", "snapshot_saved"); // unknown to this panel: rendered as a generic line
        At(1852, "cap", "descent_started", F("WhatsApp.Root.exe", "renderer", 290));
        At(1850, "cap", "descent_started", F("ms-teams.exe", "renderer", 392));
        At(1820, "cap", "cap_lowered", F("WhatsApp.Root.exe", "renderer", 247, 290));
        At(1790, "cap", "cap_lowered", F("ms-teams.exe", "renderer", 333, 392));
        At(1760, "cap", "cap_lowered", F("WhatsApp.Root.exe", "renderer", 210, 247));
        At(1640, "cap", "cap_lowered", F("WhatsApp.Root.exe", "renderer", 178, 210));
        At(1580, "free", "cap_released", F("Discord.exe", "renderer", reason: "audio"));
        At(1450, "cap", "cap_lowered", F("ms-teams.exe", "renderer", 283, 333));
        At(1300, "cap", "cap_lowered", F("WhatsApp.Root.exe", "renderer", 151, 178));
        At(1120, "cap", "cap_lowered", F("ms-teams.exe", "renderer", 241, 283));
        At(980, "cap", "cap_lowered", F("WhatsApp.Root.exe", "renderer", 128, 151));
        At(860, "cap", "cap_lowered", F("ms-teams.exe", "renderer", 214, 241));
        At(720, "warn", "cap_tampered", F("WhatsApp.Root.exe", "renderer"));
        At(600, "cap", "cap_lowered", F("WhatsApp.Root.exe", "renderer", 118, 128));
        At(420, "free", "cap_released", F("ChatGPT.exe", "renderer", reason: "focus"));
        At(300, "free", "cap_released", F("Spotify.exe", "main", reason: "focus"));
        At(150, "cap", "cap_restored_learned", F("ChatGPT.exe", "renderer", 240));
        At(96, "cap", "cap_lowered", F("ChatGPT.exe", "renderer", 204, 240));
        At(46, "cap", "cap_lowered", F("ChatGPT.exe", "renderer", 174, 204));
        At(20, "warn", "cap_raised", F("ms-teams.exe", "renderer", 278, 214, "refaults"));
    }
}
