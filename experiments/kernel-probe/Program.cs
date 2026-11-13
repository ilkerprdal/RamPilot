using System.Diagnostics;
using System.Globalization;
using KernelProbe;

// kernel-probe: synthetic experiments on the Windows working-set manager (see ../README.md).
//
//   kernel-probe victim  [--runs 3] [--mb 400] [--hot-mb 40] [--layout stride|block] [--interval-ms 100]
//                        [--kind private|file] [--warmup-s 30] [--caps 150] [--step-s 60] [--tag name]
//   kernel-probe faults  [--runs 3] [--mb 128] [--wait-s 30]
//   kernel-probe trim    [--runs 3] [--mb 300]
//   kernel-probe worker  ...   (child process, driven by the commands above)
//   kernel-probe pressure --mb N [--hold-ms 3000]

var a = new Args(args);
CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
return a.Mode switch
{
    "worker" => Worker.Run(a),
    "pressure" => Experiments.PressureChild(a),
    "victim" => Experiments.Victim(a),
    "faults" => Experiments.Faults(a),
    "trim" => Experiments.Trim(a),
    "watch" => Experiments.Watch(a),
    _ => Usage(),
};

static int Usage()
{
    Console.Error.WriteLine("usage: kernel-probe victim|faults|trim [--options]   (see Program.cs header)");
    return 2;
}

internal sealed class Args
{
    private readonly Dictionary<string, string> _d = new();
    public readonly string Mode;

    public Args(string[] a)
    {
        Mode = a.Length > 0 ? a[0] : "";
        for (int i = 1; i < a.Length; i++)
            if (a[i].StartsWith("--")) _d[a[i][2..]] = i + 1 < a.Length && !a[i + 1].StartsWith("--") ? a[++i] : "true";
    }

    public string Str(string k, string def) => _d.TryGetValue(k, out var v) ? v : def;
    public int Int(string k, int def) => _d.TryGetValue(k, out var v) ? int.Parse(v, CultureInfo.InvariantCulture) : def;
}

internal static class Experiments
{
    private const uint MinDis = Native.QUOTA_LIMITS_HARDWS_MIN_DISABLE;
    private const uint MaxEn = Native.QUOTA_LIMITS_HARDWS_MAX_ENABLE;
    private const uint MaxDis = Native.QUOTA_LIMITS_HARDWS_MAX_DISABLE;
    private const long MB = Sys.MB;

    private static string ProbeDir
    {
        get
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d != null && !File.Exists(Path.Combine(d.FullName, "KernelProbe.csproj"))) d = d.Parent;
            return d?.FullName ?? throw new InvalidOperationException("KernelProbe.csproj not found above exe");
        }
    }

    private static string OutDir => Path.GetFullPath(Path.Combine(ProbeDir, "..", "results", "kernel-probe"));
    private static string Stamp => DateTime.Now.ToString("yyyyMMdd_HHmmss");
    private static string TmpArg => $"--tmp \"{Path.Combine(ProbeDir, "tmp")}\"";

    /// <summary>Resident % of each tenth of the region, lowest address first, e.g. "0|0|...|100".</summary>
    private static string Deciles(ulong[] at)
    {
        var parts = new string[10];
        for (int d = 0; d < 10; d++)
        {
            long from = at.Length * d / 10, to = at.Length * (d + 1) / 10, v = 0;
            for (long i = from; i < to; i++) if (Sys.Valid(at[i])) v++;
            parts[d] = (100.0 * v / (to - from)).ToString("F0", CultureInfo.InvariantCulture);
        }
        return string.Join('|', parts);
    }

    private static void Log(string s)
    {
        Console.WriteLine(s);
        File.AppendAllText(Path.Combine(OutDir, "console_" + Stamp[..8] + ".txt"), s + Environment.NewLine);
    }

    // =====================================================================================
    // A. Victim selection under a hard working-set maximum
    // =====================================================================================
    public static int Victim(Args a)
    {
        int runs = a.Int("runs", 3);
        int mb = a.Int("mb", 400), hotMb = a.Int("hot-mb", 40), interval = a.Int("interval-ms", 100);
        int warmup = a.Int("warmup-s", 30), step = a.Int("step-s", 60);
        string layout = a.Str("layout", "stride"), kind = a.Str("kind", "private"), fillOrder = a.Str("fill", "forward");
        long[] caps = a.Str("caps", "150").Split(',').Select(long.Parse).ToArray();
        string tag = a.Str("tag", $"{kind}_{layout}_{interval}ms_caps{string.Join('-', caps)}");
        Directory.CreateDirectory(OutDir);
        using var csv = new Csv(Path.Combine(OutDir, $"A_{tag}_{Stamp}.csv"),
            "run", "label", "t_s", "cap_mb", "ws_mb", "privws_mb", "region_res_mb", "hot_res_pct", "cold_res_pct",
            "other_ws_mb", "pf", "hf", "mc_ws_mb", "call_ms", "guard", "res_by_address_decile_pct");
        Log($"== A victim {tag}: region {mb} MB ({kind}, fill {fillOrder}), hot {hotMb} MB {layout} every {interval} ms, warmup {warmup}s, caps {string.Join(",", caps)} MB x {step}s, runs {runs}");

        for (int run = 1; run <= runs; run++)
        {
            using var w = new WorkerProc($"--mb {mb} --hot-mb {hotMb} --layout {layout} --interval-ms {interval} --kind {kind} --fill {fillOrder} {TmpArg}");
            var (b, pages) = w.Regions["main"];
            var isHot = new bool[pages];
            foreach (long p in Worker.HotPages(pages, hotMb * MB / Sys.Page, layout)) isHot[p] = true;
            long hotN = isHot.Count(x => x), coldN = pages - hotN;
            var sw = Stopwatch.StartNew();

            // External-trim guard: before any cap the region must stay fully resident; under a cap the WS must
            // stay near the cap and region residency must not fall by more than 5% of the cap between samples
            // of the same step (the hot thread keeps the WS at the cap); after uncapping nothing may drop.
            bool runOk = true;
            double prevRegion = double.NaN;
            (double hotPct, double coldPct, uint pf) Sample(string label, long cap, double callMs = 0, bool newStep = false)
            {
                ulong[] at = Sys.WsEx(w.H, b, pages);
                long hr = 0, cr = 0;
                for (long i = 0; i < pages; i++) if (Sys.Valid(at[i])) { if (isHot[i]) hr++; else cr++; }
                var (t, s) = Sys.ProcAndStore(w.Pid);
                double regionMb = (hr + cr) * Sys.Page / (double)MB;
                double wsMb = t.WorkingSet / (double)MB;
                double hp = 100.0 * hr / hotN, cp = 100.0 * cr / coldN;
                string guard = "ok";
                if (cap == 0 && label != "uncapped" && regionMb < mb * 0.995) guard = "EXTERNAL_TRIM?";
                if (cap > 0 && !label.EndsWith("_immediate") && wsMb < cap * 0.85) guard = "EXTERNAL_TRIM?";
                if (!newStep && !double.IsNaN(prevRegion) && regionMb < prevRegion - Math.Max(cap, 100) * 0.05) guard = "EXTERNAL_TRIM?";
                if (guard != "ok") { runOk = false; Log($"run {run}: guard tripped at {label} (WS {wsMb:F1} MB, region {regionMb:F1} MB, prev {prevRegion:F1} MB)"); }
                prevRegion = regionMb;
                csv.Row(run, label, Math.Round(sw.Elapsed.TotalSeconds, 2), cap, wsMb,
                    t.PrivateWs / (double)MB, regionMb, hp, cp, wsMb - regionMb,
                    t.PageFaults, t.HardFaults, s.WorkingSet / (double)MB, callMs, guard, Deciles(at));
                return (hp, cp, t.PageFaults);
            }

            Sample("start", 0);
            Thread.Sleep(warmup * 1000);
            var pre = Sample("pre", 0);
            Log($"run {run}: pre-cap hot {pre.hotPct:F1}% cold {pre.coldPct:F1}% resident");
            foreach (long cap in caps)
            {
                w.Cmd("pause");   // freeze hot touching so the immediate sample shows only the kernel's choice
                var t0 = Stopwatch.StartNew();
                Sys.SetWs(w.H, 1 * MB, cap * MB, MinDis | MaxEn);
                double callMs = t0.Elapsed.TotalMilliseconds;
                var imm = Sample($"cap{cap}_immediate", cap, callMs, newStep: true);
                w.Cmd("resume");
                double stepStart = sw.Elapsed.TotalSeconds;
                (double hotPct, double coldPct, uint pf) last = imm;
                foreach (int ts in new[] { 1, 2, 5, 10, 20, 30, 45, 60, 90, 120 }.Where(x => x <= step))
                {
                    double wait = stepStart + ts - sw.Elapsed.TotalSeconds;
                    if (wait > 0) Thread.Sleep(TimeSpan.FromSeconds(wait));
                    last = Sample($"cap{cap}", cap);
                }
                Log($"run {run}: cap {cap} MB (call {callMs:F1} ms) immediate hot {imm.hotPct:F1}% cold {imm.coldPct:F1}% | after {step}s hot {last.hotPct:F1}% cold {last.coldPct:F1}% | faults during step {last.pf - imm.pf} ({(last.pf - imm.pf) / (double)step:F1}/s)");
            }
            Sys.SetWs(w.H, 1 * MB, 2048 * MB, MinDis | MaxDis);
            Thread.Sleep(2000);
            Sample("uncapped", 0);
            Log($"run {run}: {(runOk ? "VALID (no external trim detected)" : "INVALID: possible external trim")}");
        }
        Log("CSV: " + csv.Path);
        return 0;
    }

    // =====================================================================================
    // B. Fault accounting: which counters move when trimmed pages are touched again
    // =====================================================================================
    public static int PressureChild(Args a)
    {
        long mb = a.Int("mb", 1024);
        unsafe
        {
            byte* p = (byte*)Native.VirtualAlloc(null, (nuint)(mb * MB), Native.MEM_COMMIT | Native.MEM_RESERVE, Native.PAGE_READWRITE);
            if (p == null) { Console.WriteLine("ERR alloc"); return 1; }
            for (long i = 0; i < mb * MB; i += Sys.Page) p[i] = 1;
        }
        Thread.Sleep(a.Int("hold-ms", 3000));
        return 0;
    }

    /// <summary>Consumes free + low-priority standby (+ margin) so that priority-1 standby pages are repurposed.</summary>
    private static long Pressure(Pdh pdh, int marginMb)
    {
        var v = pdh.Read();
        long need = (Pdh.Get(v, "freeZeroB") + Pdh.Get(v, "standbyReserveB") + Pdh.Get(v, "modifiedB")) / MB + marginMb;
        long avail = Pdh.Get(v, "availableB") / MB;
        need = Math.Min(need, avail - 1536);   // never push available below ~1.5 GB
        var p = Process.Start(new ProcessStartInfo(Sys.SelfExe, $"pressure --mb {need} --hold-ms 3000") { UseShellExecute = false, CreateNoWindow = true })!;
        Sys.AddToKillJob(p);
        p.WaitForExit();
        Thread.Sleep(1000);
        return need;
    }

    /// <summary>Diagnostic: what happens to trimmed private pages over time (modified list -> store -> standby).</summary>
    public static int Watch(Args a)
    {
        int mb = a.Int("mb", 256), secs = a.Int("secs", 60), memprio = a.Int("memprio", 1);
        Directory.CreateDirectory(OutDir);
        var pdh = new Pdh();
        using var csv = new Csv(Path.Combine(OutDir, $"B_watch_{Stamp}.csv"),
            "t_s", "event", "modified_mb", "standby_reserve_mb", "standby_normal_mb", "free_zero_mb", "available_mb", "mc_ws_mb", "sysPagesOutput", "sysPageWrites");
        using var w = new WorkerProc($"--memprio {memprio} {TmpArg}");
        w.Cmd($"alloc r private {mb}");
        var sw = Stopwatch.StartNew();
        void S(string ev)
        {
            var v = pdh.Read();
            var st = Sys.ProcAndStore(w.Pid).store;
            double M(string k) => Pdh.Get(v, k) / (double)MB;
            csv.Row(Math.Round(sw.Elapsed.TotalSeconds, 1), ev, M("modifiedB"), M("standbyReserveB"), M("standbyNormalB"),
                M("freeZeroB"), M("availableB"), st.WorkingSet / (double)MB, Pdh.Get(v, "sysPagesOutput"), Pdh.Get(v, "sysPageWrites"));
            Log($"t={sw.Elapsed.TotalSeconds,5:F1} {ev,-8} modified {M("modifiedB"),7:F0} reserve {M("standbyReserveB"),6:F0} normal {M("standbyNormalB"),6:F0} free {M("freeZeroB"),6:F0} store {st.WorkingSet / (double)MB,6:F0} MB");
        }
        S("before");
        w.Cmd("trim r");
        S("trimmed");
        for (int i = 1; i <= secs; i++) { Thread.Sleep(1000); if (i <= 5 || i % 5 == 0) S("idle"); }
        Log("CSV: " + csv.Path);
        return 0;
    }

    public static int Faults(Args a)
    {
        int runs = a.Int("runs", 3), mb = a.Int("mb", 128), bigMb = a.Int("big-mb", 512), waitS = a.Int("wait-s", 30);
        int margin = a.Int("margin-mb", 512);
        Directory.CreateDirectory(OutDir);
        var pdh = new Pdh();
        string stamp = Stamp;
        using var csv = new Csv(Path.Combine(OutDir, $"B_faults_{stamp}.csv"),
            "run", "state", "pages", "res_before_pct", "touch_us", "us_per_page", "dPF", "dHF", "pf_per_page", "hf_per_page",
            "dSysPF", "dSysTransition", "dSysDemandZero", "dSysCache", "dSysPageReads", "dSysPagesInput",
            "est_nontransition_faults", "mc_ws_mb", "dMcWs_since_trim_mb", "dMcWs_touch_mb", "dMcPF", "dMcHF", "pressure_mb",
            "worker_Mcycles", "store_Mcycles", "worker_kernel_ms",
            "h_lt0.3us", "h_0.3_1us", "h_1_2us", "h_2_4us", "h_4_8us", "h_8_32us", "h_ge32us", "guard");
        using var lists = new Csv(Path.Combine(OutDir, $"B_lists_{stamp}.csv"),
            "run", "label", "modified_mb", "standby_reserve_mb", "standby_normal_mb", "standby_core_mb", "free_zero_mb", "available_mb", "mc_ws_mb");
        Log($"== B faults: small region {mb} MB, big region {bigMb} MB, idle wait {waitS}s, pressure margin {margin} MB, runs {runs}; worker memory priority 1");

        for (int run = 1; run <= runs; run++)
        {
            using var w = new WorkerProc($"--memprio 1 {TmpArg}");
            w.Cmd("alloc warm private 1");
            long mcAtTrim = 0;
            long pressureMb = 0;
            string batchGuard = "ok";
            double Res(string region) { var (rb, rp) = w.Regions[region]; return 100.0 * Sys.WsEx(w.H, rb, rp).Count(Sys.Valid) / rp; }

            void Snap(string label)
            {
                var v = pdh.Read();
                var st = Sys.ProcAndStore(w.Pid).store;
                double M(string k) => Pdh.Get(v, k) / (double)MB;
                lists.Row(run, label, M("modifiedB"), M("standbyReserveB"), M("standbyNormalB"), M("standbyCoreB"), M("freeZeroB"), M("availableB"), st.WorkingSet / (double)MB);
                Log($"run {run} [{label}] modified {M("modifiedB"):F0} | standby reserve(p0-3) {M("standbyReserveB"):F0} normal(p4-5) {M("standbyNormalB"):F0} core(p6-7) {M("standbyCoreB"):F0} | free {M("freeZeroB"):F0} | store WS {st.WorkingSet / (double)MB:F0} MB");
            }

            void Measure(string state, string region, bool write = false)
            {
                var (b, pages) = w.Regions[region];
                w.Cmd("touch warm");
                w.Cmd("touch warm");
                ulong[] at = Sys.WsEx(w.H, b, pages);
                double resBefore = 100.0 * at.Count(Sys.Valid) / pages;
                // External-trim guard: a never-trimmed region must be fully resident; batch regions were checked before trimming.
                string guard = state.StartsWith("S0") && resBefore < 99.5 ? "EXTERNAL_TRIM?" : region is "s3b" or "s4" or "s5" or "s6" or "s7" ? batchGuard : "ok";
                if (guard != "ok") Log($"run {run}: guard tripped for {state}");
                var (t0, s0) = Sys.ProcAndStore(w.Pid);
                var v0 = pdh.Read();
                string reply = w.Cmd((write ? "touchw " : "touch ") + region);
                var (t1, s1) = Sys.ProcAndStore(w.Pid);
                var v1 = pdh.Read();
                var rf = reply.Split(' ');
                long us = long.Parse(rf[3]);
                long[] hist = rf[4].Split(',').Select(long.Parse).ToArray();
                long d(string k) => Pdh.Get(v1, k) - Pdh.Get(v0, k);
                uint dpf = t1.PageFaults - t0.PageFaults, dhf = t1.HardFaults - t0.HardFaults;
                long nonTrans = Math.Max(0, dpf - d("sysTransition") - d("sysDemandZero"));
                double sinceTrim = mcAtTrim != 0 ? (s0.WorkingSet - mcAtTrim) / (double)MB : double.NaN;
                csv.Row(run, state, pages, resBefore, us, us / (double)pages, dpf, dhf, dpf / (double)pages, dhf / (double)pages,
                    d("sysPF"), d("sysTransition"), d("sysDemandZero"), d("sysCache"), d("sysPageReads"), d("sysPagesInput"), nonTrans,
                    s0.WorkingSet / (double)MB, sinceTrim, (s1.WorkingSet - s0.WorkingSet) / (double)MB,
                    s1.PageFaults - s0.PageFaults, s1.HardFaults - s0.HardFaults, pressureMb,
                    (t1.Cycles - t0.Cycles) / 1e6, (s1.Cycles - s0.Cycles) / 1e6, (t1.KernelTime100ns - t0.KernelTime100ns) / 1e4,
                    hist[0], hist[1], hist[2], hist[3], hist[4], hist[5], hist[6], guard);
                double ge1 = 100.0 * (pages - hist[0] - hist[1]) / pages;
                Log($"run {run} {state,-44} res {resBefore,5:F1}% | {us / (double)pages,6:F2} us/page, >=1us {ge1,5:F1}% | PF {dpf,6} HF {dhf,5} | sys trans {d("sysTransition"),6} dz {d("sysDemandZero"),6} reads {d("sysPageReads"),5} pagesIn {d("sysPagesInput"),6} nonTrans~{nonTrans,6} | store dWS {(s1.WorkingSet - s0.WorkingSet) / (double)MB:F1} MB PF/HF {s1.PageFaults - s0.PageFaults}/{s1.HardFaults - s0.HardFaults} Mcyc {(s1.Cycles - s0.Cycles) / 1e6:F0} | worker Mcyc {(t1.Cycles - t0.Cycles) / 1e6:F0}");
            }

            // S0: resident pages (no fault expected)
            w.Cmd($"alloc s0 private {mb}");
            Measure("S0 resident (never trimmed)", "s0");
            w.Cmd("free s0");

            // S1: demand-zero first touch (soft fault baseline)
            w.Cmd($"allocz s1 {mb}");
            Measure("S1 demand-zero first write", "s1", write: true);
            w.Cmd("free s1");

            // S2: trimmed, touched immediately (modified list -> soft transition fault)
            w.Cmd($"alloc s2 private {mb}");
            w.Cmd("trim s2");
            Measure("S2 private trimmed then touched at once", "s2");
            w.Cmd("free s2");

            // S3: small trim (stays below the modified-page-writer threshold), idle wait, no pressure
            w.Cmd($"alloc s3 private {mb}");
            Snap("S3 before trim");
            w.Cmd("trim s3");
            Thread.Sleep(waitS * 1000);
            Snap($"S3 after trim + {waitS}s");
            Measure($"S3 private trimmed + {waitS}s idle", "s3");
            w.Cmd("free s3");

            // Batch: enough trimmed memory to wake the modified page writer (compression), then pressure.
            w.Cmd($"alloc s3b private {mb}");
            w.Cmd($"alloc s4 private {bigMb}");
            w.Cmd($"alloc s5 file {mb}");
            w.Cmd($"alloc s6 section {mb}");
            w.Cmd($"alloc s7 private {bigMb}");
            Snap("batch before trim");
            foreach (var r in new[] { "s3b", "s4", "s5", "s6", "s7" }) if (Res(r) < 99.5) batchGuard = "EXTERNAL_TRIM? (" + r + " not resident before trim)";
            mcAtTrim = Sys.ProcAndStore(w.Pid).store.WorkingSet;
            foreach (var r in new[] { "s3b", "s4", "s5", "s6" }) w.Cmd("trim " + r);
            // s7 is trimmed the RamPilot way: a hard max far below the WS, then released.
            Sys.SetWs(w.H, 1 * MB, 8 * MB, MinDis | MaxEn);
            Sys.SetWs(w.H, 1 * MB, 2048 * MB, MinDis | MaxDis);
            Snap("batch just trimmed");
            Thread.Sleep(15000);
            Snap("batch trimmed + 15s");
            Measure("S3b private batch-trimmed + 15s, no pressure", "s3b");
            pressureMb = Pressure(pdh, margin);
            Snap($"after pressure {pressureMb} MB");
            Measure("S4 private trimmed + pressure", "s4");
            Measure("S5 file-backed (clean) trimmed + pressure", "s5");
            Measure("S6 pagefile section trimmed + pressure", "s6");
            Measure("S7 private hard-cap-trimmed + pressure", "s7");
            foreach (var r in new[] { "s3b", "s4", "s5", "s6", "s7" }) w.Cmd("free " + r);
            mcAtTrim = 0; pressureMb = 0;
        }
        Log("CSV: " + csv.Path);
        return 0;
    }


    // =====================================================================================
    // C. Which SetProcessWorkingSetSizeEx calls trim immediately
    // =====================================================================================
    public static int Trim(Args a)
    {
        int runs = a.Int("runs", 3), mb = a.Int("mb", 300);
        Directory.CreateDirectory(OutDir);
        using var csv = new Csv(Path.Combine(OutDir, $"C_trim_{Stamp}.csv"),
            "run", "step", "ws_before_mb", "ws_immediate_mb", "ws_after2s_mb", "limits_after", "guard");
        Log($"== C trim-on-call: region {mb} MB, runs {runs}");
        for (int run = 1; run <= runs; run++)
        {
            using var w = new WorkerProc($"--mb {mb} {TmpArg}");
            double Ws() => Sys.Proc(w.Pid).WorkingSet / (double)MB;

            double prevAfter = double.NaN;
            bool runOk = true;
            void Step(string name, Action act)
            {
                double before = Ws();
                act();
                double imm = Ws();
                Thread.Sleep(2000);
                double after = Ws();
                string lim = Sys.GetWs(w.H);
                // External-trim guard: WS must not fall between steps, nor in the 2 s after a call settled.
                string guard = (!double.IsNaN(prevAfter) && before < prevAfter - 5) || after < imm - 5 ? "EXTERNAL_TRIM?" : "ok";
                if (guard != "ok") runOk = false;
                prevAfter = after;
                csv.Row(run, name, before, imm, after, lim, guard);
                Log($"run {run} {name,-48} WS {before,6:F1} -> {imm,6:F1} (2s: {after,6:F1}) MB | {lim} | {guard}");
            }

            Step("touch region (baseline)", () => w.Cmd("touch main"));
            Step("hard max 150 MB (below WS)", () => Sys.SetWs(w.H, 1 * MB, 150 * MB, MinDis | MaxEn));
            Step("raise hard max to 1024 MB", () => Sys.SetWs(w.H, 1 * MB, 1024 * MB, MinDis | MaxEn));
            Step("touch region under 1024 MB hard max", () => w.Cmd("touch main"));
            Step("disable hard max (soft max 1024 MB)", () => Sys.SetWs(w.H, 1 * MB, 1024 * MB, MinDis | MaxDis));
            Step("soft max 10 MB (MAX_DISABLE)", () => Sys.SetWs(w.H, 1 * MB, 10 * MB, MinDis | MaxDis));
            Step("touch region under 10 MB soft max", () => w.Cmd("touch main"));
            Step("soft max = 0.5 x WS", () => Sys.SetWs(w.H, 1 * MB, (long)(Ws() * 0.5) * MB, MinDis | MaxDis));
            Step("touch region again", () => w.Cmd("touch main"));
            Step("soft max = 2 x WS", () => Sys.SetWs(w.H, 1 * MB, (long)(Ws() * 2) * MB, MinDis | MaxDis));
            Step("hard max = WS + 64 MB (above WS)", () => Sys.SetWs(w.H, 1 * MB, ((long)Ws() + 64) * MB, MinDis | MaxEn));
            Step("touch region under hard max above WS", () => w.Cmd("touch main"));
            Log($"run {run}: {(runOk ? "VALID (no unexplained WS drop)" : "INVALID: unexplained WS drop, possible external trim")}");
        }
        Log("CSV: " + csv.Path);
        return 0;
    }
}
