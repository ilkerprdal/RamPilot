using RamPilot.Core;

namespace RamPilot.Core.Tests;

public class ProcessSnapshotTests
{
    private static readonly HashSet<string> Targets = new(StringComparer.OrdinalIgnoreCase) { "WhatsApp.Root.exe", "Discord.exe" };
    private static readonly HashSet<string> Helpers = new(StringComparer.OrdinalIgnoreCase) { "msedgewebview2.exe" };

    private static ProcEntry P(uint pid, uint parent, string exe, long create) => new(pid, parent, exe, create, 0, 0, 0, 0, 0, 0);

    private static ProcessSnapshot Tree(params ProcEntry[] e)
    {
        var s = new ProcessSnapshot { Valid = true };
        foreach (var x in e) s.Add(x);
        return s;
    }

    [Fact]
    public void FindsRootThroughHelpers()
    {
        var s = Tree(P(4, 0, "explorer.exe", 1), P(100, 4, "WhatsApp.Root.exe", 10), P(200, 100, "msedgewebview2.exe", 20),
            P(300, 200, "msedgewebview2.exe", 30));
        Assert.Equal(100u, s.FindAppRoot(300, Targets, Helpers)?.Pid);
        Assert.Equal(100u, s.FindAppRoot(100, Targets, Helpers)?.Pid);
        Assert.Null(s.FindAppRoot(4, Targets, Helpers));
    }

    [Fact]
    public void RecycledParentPidIsNotAnAncestor()
    {
        // The real parent (pid 100) exited; pid 100 now belongs to a NEWER WhatsApp instance.
        var s = Tree(P(100, 4, "WhatsApp.Root.exe", 500), P(200, 100, "msedgewebview2.exe", 20));
        Assert.Null(s.FindAppRoot(200, Targets, Helpers));
    }

    [Fact]
    public void RecycledGrandparentStopsTheWalkAtTheLastValidTarget()
    {
        // 300 -> 200 (helper) -> 100 (target) -> 50: pid 50 recycled by a newer Discord.exe.
        var s = Tree(P(50, 4, "Discord.exe", 900), P(100, 50, "WhatsApp.Root.exe", 10), P(200, 100, "msedgewebview2.exe", 20),
            P(300, 200, "msedgewebview2.exe", 30));
        Assert.Equal(100u, s.FindAppRoot(300, Targets, Helpers)?.Pid);
    }

    [Fact]
    public void UnrelatedImageBreaksTheChain()
    {
        var s = Tree(P(100, 4, "WhatsApp.Root.exe", 10), P(150, 100, "cmd.exe", 15), P(200, 150, "msedgewebview2.exe", 20));
        Assert.Null(s.FindAppRoot(200, Targets, Helpers));
    }

    [Fact]
    public void HelperOutsideAnyTargetTreeHasNoRoot()
    {
        var s = Tree(P(4, 0, "explorer.exe", 1), P(200, 4, "msedgewebview2.exe", 20));
        Assert.Null(s.FindAppRoot(200, Targets, Helpers));
    }

    [Fact]
    public void TopMostTargetWins()
    {
        var s = Tree(P(100, 4, "Discord.exe", 10), P(110, 100, "Discord.exe", 11), P(120, 110, "Discord.exe", 12));
        Assert.Equal(100u, s.FindAppRoot(120, Targets, Helpers)?.Pid);
    }

    [Fact]
    public void SelfParentAndCyclesTerminate()
    {
        var s = Tree(P(100, 100, "WhatsApp.Root.exe", 10), P(7, 8, "msedgewebview2.exe", 5), P(8, 7, "msedgewebview2.exe", 5));
        Assert.Equal(100u, s.FindAppRoot(100, Targets, Helpers)?.Pid);
        Assert.Null(s.FindAppRoot(7, Targets, Helpers));
    }
}

public class RoleClassifierTests
{
    [Fact]
    public void UnknownCommandLineFailsClosed()
    {
        Assert.True(RoleClassifier.Classify(null).Excluded);
        Assert.True(RoleClassifier.Classify("").Excluded);
        Assert.True(RoleClassifier.Classify("   ").Excluded);
    }

    [Fact]
    public void LongChromiumCommandLineIsStillClassified()
    {
        string cmd = "\"C:\\x\\msedgewebview2.exe\" " + string.Concat(Enumerable.Repeat("--enable-features=SomeLongFeatureName ", 400))
            + "--type=gpu-process --gpu-preferences=abc";
        Assert.True(cmd.Length > 8192);
        var c = RoleClassifier.Classify(cmd);
        Assert.True(c.Excluded);
        Assert.Equal("gpu-process", c.Role);
    }

    [Theory]
    [InlineData("\"a.exe\" --type=renderer --lang=en", "renderer", false)]
    [InlineData("\"a.exe\" --type=utility --utility-sub-type=network.mojom.NetworkService", "utility:network.mojom.NetworkService", false)]
    [InlineData("\"a.exe\" --type=utility --utility-sub-type=audio.mojom.AudioService", "utility:audio.mojom.AudioService", true)]
    [InlineData("\"a.exe\" --type=crashpad-handler --database=x", "crashpad-handler", true)]
    [InlineData("\"C:\\Program Files\\WhatsApp\\WhatsApp.Root.exe\"", "main", false)]
    [InlineData("\"a.exe\" --embedded-browser-webview=1 --webview-exe-name=WhatsApp.Root.exe", "main", false)]
    public void Roles(string cmd, string role, bool excluded)
    {
        var c = RoleClassifier.Classify(cmd);
        Assert.Equal(role, c.Role);
        Assert.Equal(excluded, c.Excluded);
    }

    [Fact]
    public void TypeInsideAnotherArgumentIsIgnored()
    {
        Assert.Equal("main", RoleClassifier.RoleOf("a.exe --foo=x--type=renderer"));
    }

    [Fact]
    public void UserExclusionsApply()
    {
        Assert.True(RoleClassifier.Classify("a.exe --type=renderer --extension-process", ["--extension-process"]).Excluded);
    }
}

public class ConfigTests
{
    [Fact]
    public void DefaultsSurviveSanitizeUnchanged()
    {
        var w = new List<string>();
        var c = new EngineConfig().Sanitize(w);
        Assert.Empty(w);
        Assert.Contains(c.Apps, a => a.Exe == "WhatsApp.Root.exe");
        Assert.Equal(2, c.Policy.TickSeconds);
    }

    [Fact]
    public void ValuesAreClamped()
    {
        var w = new List<string>();
        var c = new EngineConfig
        {
            Policy = new PolicyConfig { TickSeconds = 0.01, StepFactor = 0.1, MinCapMb = 1, StartRatio = double.NaN, StallBudget = 5 },
            Governor = new GovernorConfig { HighAvailPct = 40, MediumAvailPct = 20, LowAggressiveness = 0.9, MediumAggressiveness = 0.2 },
        }.Sanitize(w);
        Assert.Equal(1, c.Policy.TickSeconds);
        Assert.Equal(0.6, c.Policy.StepFactor);
        Assert.Equal(16, c.Policy.MinCapMb);
        Assert.Equal(0.9, c.Policy.StartRatio);
        Assert.Equal(0.02, c.Policy.StallBudget);
        Assert.True(c.Governor.HighAvailPct < c.Governor.MediumAvailPct);
        Assert.True(c.Governor.MediumAggressiveness >= c.Governor.LowAggressiveness);
        Assert.True(w.Count >= 6);
    }

    [Theory]
    [InlineData("steam.exe")]
    [InlineData("EasyAntiCheat_EOS.exe")]
    [InlineData("BEService_x64.exe")]
    [InlineData("vgc.exe")]
    [InlineData("MsMpEng.exe")]
    [InlineData("csrss.exe")]
    [InlineData("RamPilot.exe")]
    public void DenyListCannotBeOverridden(string exe)
    {
        var w = new List<string>();
        var c = new EngineConfig { Apps = [new AppEntry { Exe = exe }], Helpers = [exe] }.Sanitize(w);
        Assert.Empty(c.Apps);
        Assert.Empty(c.Helpers);
        Assert.Contains(w, x => x.Contains("deny", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("")]
    [InlineData("noext")]
    [InlineData("C:\\path\\a.exe")]
    [InlineData("*.exe")]
    public void InvalidImageNamesAreDropped(string exe)
    {
        var c = new EngineConfig { Apps = [new AppEntry { Exe = exe }] }.Sanitize([]);
        Assert.Empty(c.Apps);
    }

    [Fact]
    public void JsonRoundTripAcceptsCommentsAndTrailingCommas()
    {
        string json = """
        {
          // user comment
          "apps": [ { "exe": "Slack.exe", "name": "Slack" }, ],
          "policy": { "stepSeconds": 45 },
          "governor": { "pressureOverride": "MEDIUM" }
        }
        """;
        var c = EngineConfig.FromJson(json).Sanitize([]);
        Assert.Single(c.Apps);
        Assert.Equal(45, c.Policy.StepSeconds);
        Assert.Equal("medium", c.Governor.PressureOverride);
        var back = EngineConfig.FromJson(c.ToJson());
        Assert.Equal(45, back.Policy.StepSeconds);
    }

    [Fact]
    public void DuplicateAppsAreMerged()
    {
        var c = new EngineConfig { Apps = [new AppEntry { Exe = "Slack.exe" }, new AppEntry { Exe = "slack.EXE" }] }.Sanitize([]);
        Assert.Single(c.Apps);
    }

    [Fact]
    public void GameInstallPathsAreDenied()
    {
        Assert.True(DenyList.IsDeniedPath(@"D:\SteamLibrary\steamapps\common\Game\game.exe"));
        Assert.False(DenyList.IsDeniedPath(@"C:\Program Files\WindowsApps\5319275A.WhatsAppDesktop\WhatsApp.Root.exe"));
    }

    [Theory]
    [InlineData("EasyAntiCheat*", "easyanticheat_eos.exe", true)]
    [InlineData("a?c.exe", "abc.exe", true)]
    [InlineData("a?c.exe", "ac.exe", false)]
    [InlineData("*.exe", "x.exe", true)]
    [InlineData("steam.exe", "steamwebhelper.exe", false)]
    public void Glob(string pattern, string text, bool match) => Assert.Equal(match, DenyList.Glob(pattern, text));
}

public class GovernorTests
{
    private const ulong G = 1UL << 30;
    private static readonly GovernorConfig Cfg = new();

    private static MemorySample M(double availPct, double commitPct = 50, bool low = false) =>
        new(16 * G, (ulong)(16 * G * availPct / 100), 32 * G, (ulong)(32 * G * (100 - commitPct) / 100), low);

    [Fact]
    public void ClassifiesByAvailableAndCommit()
    {
        Assert.Equal(Pressure.Low, Governor.Classify(M(50), Cfg));
        Assert.Equal(Pressure.Medium, Governor.Classify(M(20), Cfg));
        Assert.Equal(Pressure.High, Governor.Classify(M(5), Cfg));
        Assert.Equal(Pressure.Medium, Governor.Classify(M(50, commitPct: 85), Cfg));
        Assert.Equal(Pressure.High, Governor.Classify(M(50, commitPct: 95), Cfg));
        Assert.Equal(Pressure.High, Governor.Classify(M(50, low: true), Cfg));
    }

    [Fact]
    public void EscalatesImmediatelyAndDeescalatesWithHysteresis()
    {
        var g = new Governor();
        Assert.Equal(Pressure.Low, g.Update(M(50), Cfg, 0));
        Assert.Equal(Pressure.High, g.Update(M(5), Cfg, 1000));
        Assert.Equal(1.0, g.Aggressiveness);
        Assert.Equal(Pressure.High, g.Update(M(50), Cfg, 2000));
        Assert.Equal(Pressure.High, g.Update(M(50), Cfg, 2000 + 59_000));
        Assert.Equal(Pressure.Low, g.Update(M(50), Cfg, 2000 + 60_000));
        Assert.Equal(Cfg.LowAggressiveness, g.Aggressiveness);
    }

    [Fact]
    public void BlipResetsDeescalationTimer()
    {
        var g = new Governor();
        g.Update(M(5), Cfg, 0);
        g.Update(M(50), Cfg, 1000);
        g.Update(M(5), Cfg, 30_000);
        Assert.Equal(Pressure.High, g.Update(M(50), Cfg, 70_000));
    }

    [Fact]
    public void OverrideForcesLevel()
    {
        var cfg = new GovernorConfig { PressureOverride = "high" }.Sanitize([]);
        var g = new Governor();
        Assert.Equal(Pressure.High, g.Update(M(80), cfg, 0));
    }
}

public class SavingsTests
{
    private static ulong Mb(double v) => Units.FromMb(v);

    [Fact]
    public void ReclaimedIsCounterfactualMinusCurrent()
    {
        Assert.Equal(Mb(60), Savings.Reclaimed(Mb(100), Mb(200), Mb(40), Mb(200)));
    }

    [Fact]
    public void AppsOwnShrinkingIsNotCredited()
    {
        // The app freed half of its commit by itself; its private WS would have halved too.
        Assert.Equal(Mb(10), Savings.Reclaimed(Mb(100), Mb(200), Mb(40), Mb(100)));
        Assert.Equal(0UL, Savings.Reclaimed(Mb(100), Mb(200), Mb(60), Mb(100)));
    }

    [Fact]
    public void GrowthIsNotCredited()
    {
        Assert.Equal(Mb(60), Savings.Reclaimed(Mb(100), Mb(200), Mb(40), Mb(400)));
    }

    [Fact]
    public void NeverNegative()
    {
        Assert.Equal(0UL, Savings.Reclaimed(Mb(50), Mb(100), Mb(80), Mb(100)));
        var s = Savings.Summarize(0, 0.5);
        Assert.Equal(0, s.NetMb);
    }

    [Fact]
    public void NetSubtractsStoreCost()
    {
        var s = Savings.Summarize(Mb(640), 0.25);
        Assert.Equal(640, s.ReclaimedMb);
        Assert.Equal(160, s.StoreCostMb);
        Assert.Equal(480, s.NetMb);
    }

    [Fact]
    public void TrackerResetsOnEnd()
    {
        var t = new SavingsTracker();
        Assert.Equal(0UL, t.Reclaimed(Mb(10), Mb(10)));
        t.Begin(Mb(100), Mb(100));
        Assert.Equal(Mb(70), t.Reclaimed(Mb(30), Mb(100)));
        t.End();
        Assert.Equal(0UL, t.Reclaimed(Mb(30), Mb(100)));
    }

    [Fact]
    public void StoreRatioStartsAtPriorAndFollowsObservations()
    {
        var e = new StoreRatioEstimator();
        Assert.Equal(StoreRatioEstimator.Prior, e.Ratio, 6);
        Assert.False(e.Observe(Mb(1), (long)Mb(1)));   // too small to be meaningful
        for (int i = 0; i < 30; i++) e.Observe(Mb(50), (long)Mb(12.5));
        Assert.InRange(e.Ratio, 0.24, 0.27);
    }

    [Fact]
    public void StoreRatioIsClamped()
    {
        var e = new StoreRatioEstimator();
        for (int i = 0; i < 50; i++) e.Observe(Mb(50), -(long)Mb(100));
        Assert.Equal(StoreRatioEstimator.MinRatio, e.Ratio, 6);
        for (int i = 0; i < 50; i++) e.Observe(Mb(50), (long)Mb(500));
        Assert.Equal(StoreRatioEstimator.MaxRatio, e.Ratio, 6);
    }
}

public class LearnedProfilesTests
{
    [Fact]
    public void RoundTripAndSeed()
    {
        var p = new LearnedProfiles();
        var s = new ControllerState { LearnedCap = 123 * Units.MiB, ReferenceWs = 400 * Units.MiB, FloorBytes = 110 * Units.MiB, FloorSetUnix = 1000 };
        string key = LearnedProfiles.Key("WhatsApp.Root.exe", "msedgewebview2.exe", "renderer");
        Assert.True(p.Update(key, s, 2000));
        Assert.False(p.Update(key, s, 2001));
        var q = LearnedProfiles.FromJson(p.ToJson(), 3000);
        var t = new ControllerState();
        q.Seed(key, t);
        Assert.Equal(s.LearnedCap, t.LearnedCap);
        Assert.Equal(s.FloorBytes, t.FloorBytes);
        Assert.Equal(s.ReferenceWs, t.ReferenceWs);
    }

    [Fact]
    public void StaleAndGarbageAreDropped()
    {
        var p = new LearnedProfiles();
        p.Update("k", new ControllerState { LearnedCap = 1 }, 0);
        Assert.Equal(0, LearnedProfiles.FromJson(p.ToJson(), LearnedProfiles.MaxAgeSeconds + 10).Count);
        Assert.Equal(0, LearnedProfiles.FromJson("{not json", 0).Count);
        Assert.Equal(0, LearnedProfiles.FromJson(null, 0).Count);
    }
}
