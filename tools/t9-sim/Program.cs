// Offline check of T9's control rule (src/Transport/AdaptiveRate.cs, RateRule.Decide) against a simple line model.
// Loads the real built DLL and calls the real rule once per simulated second; compares with fixed rates.
// Line model (fluid, 1 s steps): the sender pushes min(rate, demand) into a line of fixed capacity with a router
// buffer; the excess queues (adding delay = queue / capacity), and anything past the buffer is dropped (loss).
using System.Reflection;

if (args.Length > 0 && args[0] == "--knee") { Knee.Run(args.Length > 1 ? args[1] : "../../bin/NoodlesMcDoodles.dll"); return; }

var dll = args.Length > 0 ? args[0] : "../../bin/NoodlesMcDoodles.dll";
var asm = Assembly.LoadFrom(Path.GetFullPath(dll));
var ruleT = asm.GetType("NoodlesMcDoodles.Transport.RateRule", true);
var stateT = asm.GetType("NoodlesMcDoodles.Transport.RateState", true);
var decide = ruleT.GetMethod("Decide", BindingFlags.Public | BindingFlags.Static);

const int Floor = 153600, Ceil = 1048576, JoinCeil = 4194304, JoinSeconds = 30, Total = 20971520;
var scenarios = new (string name, double capKBs, int baseMs, int bufKB, double joinDemandKBs, double playDemandKBs, bool qUnknown)[]
{
    ("fast line (80 Mbit, 20 ms)",        10000, 20, 256, 3000, 300, false),
    ("Arizona-like (20 Mbit, 70 ms)",      2500, 70, 128, 1500, 400, false),
    ("slow line (4 Mbit, 50 ms)",           500, 50,  64, 1000, 400, false),
    ("very slow (0.8 Mbit, 80 ms)",         100, 80,  32,  300, 200, false),
    ("idle player",                        10000, 20, 256,   5,   5, false),
    ("quality not reported",               10000, 20, 256, 3000, 300, true),
};

Console.WriteLine($"{"scenario",-32} {"policy",-18} {"rate end",9} {"rate max",9} {"ping avg",9} {"ping max",9} {"loss s",7} {"delivered MB",13}");
foreach (var sc in scenarios)
{
    foreach (var policy in new[] { "T9 adaptive", "M1 fixed (T1+T2)", "vanilla fixed" })
    {
        var st = Activator.CreateInstance(stateT);
        double cap = sc.capKBs * 1024, buf = sc.bufKB * 1024, q = 0, delivered = 0, pingSum = 0, pingMax = 0;
        int rate = Floor, rateMax = 0, lossSeconds = 0, ping = sc.baseMs; float qRemote = sc.qUnknown ? -1f : 1f;
        long queueMicros = 0; int pending = 0; float outBps = 0;
        const int Seconds = 180;
        for (int t = 0; t < Seconds; t++)
        {
            bool joining = t < JoinSeconds;
            double demand = (joining ? sc.joinDemandKBs : sc.playDemandKBs) * 1024;
            if (policy == "T9 adaptive")
                rate = (int)decide.Invoke(null, new object[] { st, ping, qRemote, queueMicros, pending, outBps, rate, (float)t, Floor,
                    joining ? JoinCeil : Ceil, (long)Total, joining, 30, 0.98f });
            else if (policy == "M1 fixed (T1+T2)") rate = joining ? JoinCeil : Ceil;
            else rate = Floor;
            rateMax = Math.Max(rateMax, rate);

            double sent = Math.Min(rate, demand);
            q += sent - cap; double dropped = 0;
            if (q < 0) q = 0;
            if (q > buf) { dropped = q - buf; q = buf; }
            delivered += sent - dropped;
            double loss = sent > 0 ? dropped / sent : 0;
            if (loss > 0.001) lossSeconds++;
            ping = (int)(sc.baseMs + q / cap * 1000);
            pingSum += ping; pingMax = Math.Max(pingMax, ping);
            if (!sc.qUnknown) qRemote = (float)(1 - loss);
            pending = (int)Math.Max(0, demand - rate);
            queueMicros = rate > 0 ? (long)(pending / (double)rate * 1e6) : 0;
            outBps = (float)sent;
        }
        Console.WriteLine($"{sc.name,-32} {policy,-18} {rate / 1024,6} KiB {rateMax / 1024,6} KiB {pingSum / Seconds,6:F0} ms {pingMax,6:F0} ms {lossSeconds,7} {delivered / 1048576,13:F1}");
    }
    Console.WriteLine();
}
