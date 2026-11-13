namespace RamPilot.Panel;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        var options = PanelOptions.Parse(args);

        // Single instance: when the tray opens the panel a second time, bring the existing window forward.
        string mutexName = options.Demo || options.DemoMismatch ? @"Local\RamPilot.Panel.Test" : @"Local\RamPilot.Panel";
        using var mutex = new Mutex(true, mutexName, out bool first);
        if (!first)
        {
            Native.ActivateOtherInstance();
            return;
        }

        ApplicationConfiguration.Initialize();

        using FakeEngineServer? fake = options.DemoMismatch ? new FakeEngineServer() : null;
        IEngineLink link = options.Demo
            ? new DemoLink()
            : new PipeLink(fake?.PipeName ?? options.PipeName ?? PipeLink.DefaultPipeName());

        using var form = new MainForm(link, options);
        Application.Run(form);
    }
}

/// <summary>Command line flags. Test-only flags are undocumented in the UI.</summary>
internal sealed record PanelOptions(
    bool Demo,
    bool DemoMismatch,
    string? PipeName,
    string? Theme,
    string? Lang,
    bool DevTools)
{
    public static PanelOptions Parse(string[] args)
    {
        bool Has(string name) => args.Any(a => a.Equals(name, StringComparison.OrdinalIgnoreCase));
        string? Value(string name)
        {
            int i = Array.FindIndex(args, a => a.Equals(name, StringComparison.OrdinalIgnoreCase));
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }
        static string? OneOf(string? v, params string[] allowed) =>
            v is null ? null : allowed.FirstOrDefault(a => a.Equals(v, StringComparison.OrdinalIgnoreCase));

        return new PanelOptions(
            Demo: Has("--demo"),
            DemoMismatch: Has("--demo-mismatch"),
            PipeName: Value("--pipe"),
            Theme: OneOf(Value("--theme"), "dark", "light"),
            Lang: OneOf(Value("--lang"), "en", "tr"),
            DevTools: Has("--devtools"));
    }
}
