// Knee sweep for T9's join ceiling (T2): how long a joining player takes to receive a backlog of world data, and
// what it does to ping and loss on the line, for several T2 values. Uses the real RateRule.Decide from the built
// DLL. Run: dotnet run -c Release -- --knee [path/to/NoodlesMcDoodles.dll]
//
// Model: one connection, 1 s decisions (T9's tick) over 0.1 s line steps. The joiner has `backlog` bytes to
// receive plus 60 KiB/s of ongoing updates. The line has a fixed capacity and a router buffer of 200 ms at that
// capacity; excess queues (adds delay) and anything past the buffer is dropped and resent (Steam's reliable
// channel), so a drop costs time rather than data. Unlimited supply: the sender always has data ready, which is the
// best case for a high ceiling (terrain-heavy joins, where one large ZDO fills Steam's queue).
using System.Reflection;

static class Knee
{
    const int Floor = 153600, T1 = 1048576, JoinSeconds = 30;
    const double Steady = 60 * 1024, Dt = 0.1;

    public static void Run(string dll)
    {
        var asm = Assembly.LoadFrom(Path.GetFullPath(dll));
        var decide = asm.GetType("NoodlesMcDoodles.Transport.RateRule", true).GetMethod("Decide", BindingFlags.Public | BindingFlags.Static);
        var stateT = asm.GetType("NoodlesMcDoodles.Transport.RateState", true);

        var lines = new (string name, double mbit, int baseMs)[]
        {
            ("10 Mbit, 70 ms", 10, 70), ("20 Mbit, 70 ms", 20, 70), ("40 Mbit, 70 ms", 40, 70),
            ("100 Mbit, 70 ms", 100, 70), ("300 Mbit, 70 ms", 300, 70),
        };
        var backlogs = new[] { 3.0, 10.0, 25.0 }; // MB of world data to receive on joining
        var joinCeilings = new[] { 0, 1048576, 1572864, 2097152, 3145728, 4194304 }; // 0 = vanilla fixed rate

        Console.WriteLine("Join backlog sweep (T1 = 1 MiB/s steady ceiling; T2 = join ceiling for the first 30 s)");
        Console.WriteLine($"{"line",-17} {"backlog",8} {"policy",-14} {"sync s",7} {"ping avg",9} {"ping max",9} {"loss s",7} {"peak rate",10}");
        foreach (double mb in backlogs)
        {
            foreach (var line in lines)
            {
                foreach (int t2 in joinCeilings)
                {
                    var r = Simulate(decide, stateT, line.mbit * 1e6 / 8, line.baseMs, mb * 1e6, t2);
                    string policy = t2 == 0 ? "vanilla" : $"T2 {t2 / 1048576.0:0.#} MiB/s";
                    Console.WriteLine($"{line.name,-17} {mb,5:0} MB {policy,-14} {r.syncS,7:0.0} {r.pingAvg,6:0} ms {r.pingMax,6:0} ms {r.lossS,7} {r.peakRate / 1024,6:0} KiB");
                }
                Console.WriteLine();
            }
        }
    }

    static (double syncS, double pingAvg, double pingMax, int lossS, int peakRate) Simulate(
        MethodInfo decide, Type stateT, double cap, int baseMs, double backlog, int t2)
    {
        object st = Activator.CreateInstance(stateT);
        double buf = Math.Max(32 * 1024, cap * 0.2), q = 0, steadyPending = 0;
        int rate = Floor, peak = rate, lossS = 0, ping = baseMs;
        float qRemote = 1f, outBps = 0f; long queueMicros = 0; int pending = 0;
        double pingSum = 0, pingMax = 0; int pingN = 0; double syncS = -1;
        int joinCeil = Math.Max(T1, Math.Min(t2, T1 * 8));

        for (int t = 0; t < 600 && syncS < 0; t++)
        {
            bool joining = t < JoinSeconds;
            if (t2 != 0)
                rate = (int)decide.Invoke(null, new object[] { st, ping, qRemote, queueMicros, pending, outBps, rate, (float)t,
                    Floor, joining ? joinCeil : T1, long.MaxValue, joining, 30, 0.98f });
            peak = Math.Max(peak, rate);

            double sentS = 0, dropS = 0, qSum = 0;
            steadyPending = Math.Min(steadyPending + Steady, Steady); // unsent updates are superseded, not queued forever
            for (int k = 0; k < 10; k++)
            {
                double want = backlog + steadyPending;
                double send = Math.Min(rate * Dt, want);
                double fromSteady = Math.Min(steadyPending, send * steadyPending / Math.Max(1, want));
                steadyPending -= fromSteady;
                backlog -= send - fromSteady;
                q += send - cap * Dt;
                double drop = 0;
                if (q < 0) q = 0;
                if (q > buf) { drop = q - buf; q = buf; }
                backlog += drop; // resent later: costs time, never data
                sentS += send; dropS += drop; qSum += q;
                if (backlog <= 1 && syncS < 0) { syncS = t + (k + 1) * Dt; }
            }
            double loss = sentS > 0 ? dropS / sentS : 0;
            if (loss > 0.001) lossS++;
            ping = (int)(baseMs + qSum / 10 / cap * 1000);
            pingSum += ping; pingN++; pingMax = Math.Max(pingMax, ping);
            qRemote = (float)(1 - loss);
            pending = (int)Math.Min(backlog + steadyPending, 32768);
            queueMicros = rate > 0 ? (long)(pending / (double)rate * 1e6) : 0;
            outBps = (float)sentS;
        }
        return (syncS, pingSum / Math.Max(1, pingN), pingMax, lossS, peak);
    }
}
