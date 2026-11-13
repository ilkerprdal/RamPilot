using System.Diagnostics;
using System.Globalization;

// RpDummy [totalMb=300] [hotMb=30] [gcMb=40] [gcPeriodSec=120] [burstAtSec=420] [burstMb=120]
// Allocates totalMb of dirty private memory, then: touches hotMb every second, gcMb every gcPeriodSec,
// and at burstAtSec burns CPU for 5 s while touching burstMb (an "incoming message"). Prints its WS every 30 s.
// Layout: cold memory is allocated first, the hot set last (as in a real app, whose start-up data goes cold).
// Under a hard working-set maximum Windows appears to evict by working-set-entry age rather than by access
// recency, so a hot set that is also the oldest allocation gets evicted first (see tests/live/evidence).
int[] a = args.Select(x => int.Parse(x, CultureInfo.InvariantCulture)).ToArray();
int total = a.Length > 0 ? a[0] : 300, hot = a.Length > 1 ? a[1] : 30, gc = a.Length > 2 ? a[2] : 40;
int gcPeriod = a.Length > 3 ? a[3] : 120, burstAt = a.Length > 4 ? a[4] : 420, burstMb = a.Length > 5 ? a[5] : 120;

var chunks = new byte[total][];
for (int i = 0; i < total; i++)
{
    chunks[i] = GC.AllocateUninitializedArray<byte>(1 << 20, pinned: true);
    Touch(chunks[i]);
}
var sw = Stopwatch.StartNew();
Console.WriteLine($"pid {Environment.ProcessId}: {total} MB allocated, hot {hot}, gc {gc}/{gcPeriod}s, burst {burstMb} MB at {burstAt}s");
long lastGc = 0, lastPrint = 0;
bool burstDone = false;
while (true)
{
    long t = sw.ElapsedMilliseconds / 1000;
    for (int i = total - hot; i < total; i++) Touch(chunks[i]);
    if (t - lastGc >= gcPeriod)
    {
        lastGc = t;
        for (int i = total - hot - gc; i < total - hot; i++) Touch(chunks[i]);
    }
    if (!burstDone && t >= burstAt)
    {
        burstDone = true;
        Console.WriteLine($"{DateTime.Now:HH:mm:ss} burst: CPU 5 s + touching {burstMb} MB");
        var b = Stopwatch.StartNew();
        double x = 0;
        while (b.ElapsedMilliseconds < 5000)
        {
            for (int i = 0; i < burstMb && i < total - hot - gc; i++) Touch(chunks[i]);
            for (int k = 0; k < 1_000_000; k++) x += Math.Sqrt(k);
        }
        if (x < 0) Console.WriteLine(x);
    }
    if (t - lastPrint >= 30)
    {
        lastPrint = t;
        using var me = Process.GetCurrentProcess();
        Console.WriteLine($"{DateTime.Now:HH:mm:ss} t={t}s ws={me.WorkingSet64 >> 20} MB");
    }
    Thread.Sleep(1000);
}

static void Touch(byte[] c)
{
    for (int o = 0; o < c.Length; o += 4096) c[o]++;
}
