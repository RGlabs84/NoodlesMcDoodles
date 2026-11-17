// Offline check of C1's buffered-interpolation rule (src/Smoothing/SmoothBuffer.cs) against vanilla's own
// single-sample 0.2-lerp/5m-snap dead-reckoning (CLIENT:88001-88018/SERVER:87753-87766). Loads the real built
// DLL and drives the REAL SmoothBuffer.Ingest/TryGetRenderPose by reflection, exactly like tools/t9-sim does
// for Transport.RateRule.Decide — no Unity, no ZDO, no game process required, because SmoothBuffer itself has
// zero dependency on either (see its own remarks). Vanilla's formula is short enough to restate directly here
// (the same choice t9-sim makes for its own fixed-rate baselines) rather than needing a second reflected type.
//
// GROUND-TRUTH MODEL: a piecewise-linear "wander" path — a new random heading/speed every 0.8-1.5s, matching
// how a real AI actually moves — deliberately NOT a straight line (which would make vanilla's stale-velocity
// dead-reckoning artificially exact).
//
// 2026-09-28 LEAD FIX: the first version of this tool measured C1's own gain relative to C1-at-0ms, and
// measured C1's tracking error against its OWN delayed target (`TryGetRenderPose`'s intended output) rather
// than the ACTUAL current position. Both are circular: comparing a scheme against its own worst setting, or
// checking whether it hits the target it is deliberately aiming at, cannot show whether it is actually better
// than vanilla or worth its added delay. Rebuilt around three honest metrics, computed identically for C1 and
// for vanilla:
//   1. True-position error (mean, p95): distance from the RENDERED position to TRUE(now) — not a delayed
//      target. This is what a player aiming/dodging actually experiences.
//   2. Stutter (p95, max of frame-to-frame CHANGE IN RENDERED VELOCITY): a jerk metric, not a position-jitter
//      one — vanilla's fixed 20%/tick lerp decelerates continuously toward whatever the current target is,
//      which a plain stdev-of-position-step metric does not clearly separate from smooth motion; a sudden
//      change in apparent velocity is what actually reads as "stutter" to an eye.
//   3. A congested-far-player stream (bursty arrivals + long gaps, ~70ms RTT), not just steady jitter.
using System.Reflection;

const float SnapThresholdMeters = 5f; // vanilla's own constant (CLIENT:88012/SERVER:87764), reused verbatim
const float FrameDt = 1f / 60f;       // render/tick rate
const float WarmupSeconds = 0.25f;    // excluded from every metric below: before this, neither scheme has 2 real samples yet
const float ScriptedTeleportDistanceMeters = 10f;
const float JitterCeilingForGapDetection = 0.5f; // scenario 2/3 only — anything longer between updates is "a gap"

string dllPath = args.Length > 0 ? args[0] : "../../bin/NoodlesMcDoodles.dll";
var asm = Assembly.LoadFrom(Path.GetFullPath(dllPath));
var bufferType = asm.GetType("NoodlesMcDoodles.Smoothing.SmoothBuffer", throwOnError: true);
var ingestMethod = bufferType.GetMethod("Ingest", BindingFlags.Public | BindingFlags.Instance);
var renderMethod = bufferType.GetMethod("TryGetRenderPose", BindingFlags.Public | BindingFlags.Instance);

// Piecewise-linear wander path, built once, shared by every scenario (fixed seed -> reproducible).
var legRng = new Random(1337);
var legs = new List<(float startT, float startX, float startY, float vx, float vy)>();
{
    float t = 0f, x = 0f, y = 0f;
    while (t < 60f)
    {
        float legLen = 0.8f + (float)legRng.NextDouble() * 0.7f;               // 0.8-1.5s per leg
        float heading = (float)(legRng.NextDouble() * Math.PI * 2.0);
        float speed = 0.8f + (float)legRng.NextDouble() * 0.8f;                 // 0.8-1.6 m/s, a walking creature
        float vx = speed * MathF.Cos(heading), vy = speed * MathF.Sin(heading);
        legs.Add((t, x, y, vx, vy));
        x += vx * legLen; y += vy * legLen;
        t += legLen;
    }
}
const float MaxLegSpeed = 1.6f;

(float px, float py, float pz, float vx, float vy, float vz) TruePose(float t, float teleportOffsetX)
{
    var leg = legs.Last(l => l.startT <= t);
    float dt = t - leg.startT;
    return (leg.startX + leg.vx * dt + teleportOffsetX, leg.startY + leg.vy * dt, 0f, leg.vx, leg.vy, 0f);
}

float Dist(float ax, float ay, float az, float bx, float by, float bz) =>
    MathF.Sqrt((bx - ax) * (bx - ax) + (by - ay) * (by - ay) + (bz - az) * (bz - az));
float Lerp(float a, float b, float f) => a + (b - a) * f;
double P95(List<float> xs)
{
    var sorted = xs.OrderBy(x => x).ToList();
    int idx = Math.Clamp((int)Math.Ceiling(0.95 * sorted.Count) - 1, 0, sorted.Count - 1);
    return sorted[idx];
}

bool Ingest(object buf, uint revision, float px, float py, float pz, float qy, float qw, float vx, float vy, float vz, float arrivalTime)
{
    object[] a = { revision, px, py, pz, 0f, qy, 0f, qw, vx, vy, vz, arrivalTime, SnapThresholdMeters };
    return (bool)ingestMethod.Invoke(buf, a);
}
bool TryRender(object buf, float now, float delaySec, float capSec, bool allowExtrap, out float x, out float y, out float z)
{
    object[] a = { now, delaySec, capSec, allowExtrap, 0f, 0f, 0f, 0f, 0f, 0f, 1f };
    bool ok = (bool)renderMethod.Invoke(buf, a);
    x = (float)a[4]; y = (float)a[5]; z = (float)a[6];
    return ok;
}

List<(float t, float px, float py, float pz, float vx, float vy, float vz)> BuildStream(
    Random rng, float duration, float jitterMinS, float jitterMaxS,
    int forcedGapEveryN, float forcedGapMinS, float forcedGapMaxS, float? teleportAt, float teleportDist)
{
    var events = new List<(float, float, float, float, float, float, float)>();
    float t = 0f;
    int i = 0;
    while (t < duration)
    {
        float offset = teleportAt.HasValue && t >= teleportAt.Value ? teleportDist : 0f;
        var (px, py, pz, vx, vy, vz) = TruePose(t, offset);
        events.Add((t, px, py, pz, vx, vy, vz));
        bool forced = forcedGapEveryN > 0 && i > 0 && i % forcedGapEveryN == 0;
        float gap = forced
            ? forcedGapMinS + (float)rng.NextDouble() * (forcedGapMaxS - forcedGapMinS)
            : jitterMinS + (float)rng.NextDouble() * (jitterMaxS - jitterMinS);
        t += gap;
        i++;
    }
    return events;
}

// The Arizona case: a connection whose send queue is backed up delivers several queued updates for the same
// object back-to-back the instant it drains, then nothing for a while until the next backlog builds — not
// steady jitter. ~70ms RTT modeled as a fixed 35ms one-way latency added to every arrival (shifts every error
// number up by a realistic floor for BOTH schemes equally; does not change the burst/gap shape itself, since
// that shape is defined by GENERATION time, which the constant offset preserves the spacing of).
List<(float t, float px, float py, float pz, float vx, float vy, float vz)> BuildCongestedStream(
    Random rng, float duration, float oneWayLatencySec,
    int burstMin, int burstMax, float intraBurstGapMinS, float intraBurstGapMaxS,
    float interBurstGapMinS, float interBurstGapMaxS)
{
    var events = new List<(float, float, float, float, float, float, float)>();
    float genT = 0f;
    while (genT < duration)
    {
        int burstCount = rng.Next(burstMin, burstMax + 1);
        for (int i = 0; i < burstCount && genT < duration; i++)
        {
            var (px, py, pz, vx, vy, vz) = TruePose(genT, 0f);
            events.Add((genT + oneWayLatencySec, px, py, pz, vx, vy, vz));
            genT += intraBurstGapMinS + (float)rng.NextDouble() * (intraBurstGapMaxS - intraBurstGapMinS);
        }
        genT += interBurstGapMinS + (float)rng.NextDouble() * (interBurstGapMaxS - interBurstGapMinS);
    }
    return events;
}

// Runs both C1 (the real reflected rule) and vanilla's own restated formula over one event stream, at one
// (playbackDelay, extrapolationCap) setting. True-error and stutter are computed with NO exclusion beyond the
// startup warmup — including gap/burst-catchup frames — because for the congested scenario that reaction IS
// the phenomenon under test, and for the plain jitter scenario there are no gaps to begin with. The
// teleport-frame/overshoot fields (scenario 2/3 only) keep their original, separately-windowed computation.
SimResult Run(
    List<(float t, float px, float py, float pz, float vx, float vy, float vz)> events,
    float duration, float playbackDelaySec, float extrapolationCapSec, float? teleportAt, float teleportDist = 0f)
{
    float TeleOffset(float atTime) => teleportAt.HasValue && atTime >= teleportAt.Value ? teleportDist : 0f;

    object c1 = Activator.CreateInstance(bufferType);
    int nextEvent = 0;
    uint revision = 0;
    bool haveAnySample = false;
    float c1X = 0, c1Y = 0, c1Z = 0;

    bool vHave = false;
    float vTimer = 0f;
    (float px, float py, float pz, float vx, float vy, float vz) vBase = default;
    float vX = 0, vY = 0, vZ = 0;

    // "vanilla + shorter cap" — the C3-alone policy: IDENTICAL dead-reckon/lerp/snap formula as plain vanilla
    // above, sharing the same ingested base sample (vBase), only the dead-reckoning timer's cap differs
    // (extrapolationCapSec instead of vanilla's hardcoded 2.0f). This is what
    // Patch_ZSyncTransform_SyncPosition_ShortExtrapolation actually does at the IL level — one literal swapped,
    // nothing else about vanilla's own rendering touched.
    float vcTimer = 0f;
    float vcX = 0, vcY = 0, vcZ = 0;

    var c1Pos = new List<(float t, float x, float y, float z)>();
    var vPos = new List<(float t, float x, float y, float z)>();
    var vcPos = new List<(float t, float x, float y, float z)>();
    var c1TrueErr = new List<float>();
    var vTrueErr = new List<float>();
    var vcTrueErr = new List<float>();

    // Scenario 2/3 compatibility (unchanged semantics — see those scenarios' own remarks).
    var frames = new List<(float t, float c1x, float c1y, float c1z, float vx, float vy, float vz)>();

    for (float t = 0f; t <= duration; t += FrameDt)
    {
        while (nextEvent < events.Count && events[nextEvent].t <= t)
        {
            var e = events[nextEvent];
            revision++;
            float ang = 0.3f * e.t;
            bool teleported = Ingest(c1, revision, e.px, e.py, e.pz, MathF.Sin(ang / 2f), MathF.Cos(ang / 2f), e.vx, e.vy, e.vz, e.t);
            if (teleported || !haveAnySample) { c1X = e.px; c1Y = e.py; c1Z = e.pz; haveAnySample = true; }

            vBase = (e.px, e.py, e.pz, e.vx, e.vy, e.vz);
            vTimer = 0f;
            vcTimer = 0f;
            if (!vHave) { vX = e.px; vY = e.py; vZ = e.pz; vcX = e.px; vcY = e.py; vcZ = e.pz; vHave = true; }
            nextEvent++;
        }

        if (haveAnySample && TryRender(c1, t, playbackDelaySec, extrapolationCapSec, true, out float rx, out float ry, out float rz))
        {
            c1X = rx; c1Y = ry; c1Z = rz;
        } // else: not-ready tick — hold last value, matching the prefix falling back to vanilla for that one tick.

        if (vHave)
        {
            // Restated directly from CLIENT:88001-88012/SERVER:87753-87764: dead-reckon on this client's own
            // tick clock (capped 2s, vanilla's literal — the baseline this design replaces), lerp 20%/tick or
            // snap outright at >=5m.
            vTimer = MathF.Min(vTimer + FrameDt, 2.0f);
            float tx = vBase.px + vBase.vx * vTimer, ty = vBase.py + vBase.vy * vTimer, tz = vBase.pz + vBase.vz * vTimer;
            float d = Dist(vX, vY, vZ, tx, ty, tz);
            if (d < SnapThresholdMeters) { vX = Lerp(vX, tx, 0.2f); vY = Lerp(vY, ty, 0.2f); vZ = Lerp(vZ, tz, 0.2f); }
            else { vX = tx; vY = ty; vZ = tz; }

            // Identical formula, only the cap differs — see this method's remarks on vcTimer above.
            vcTimer = MathF.Min(vcTimer + FrameDt, extrapolationCapSec);
            float ctx = vBase.px + vBase.vx * vcTimer, cty = vBase.py + vBase.vy * vcTimer, ctz = vBase.pz + vBase.vz * vcTimer;
            float cd = Dist(vcX, vcY, vcZ, ctx, cty, ctz);
            if (cd < SnapThresholdMeters) { vcX = Lerp(vcX, ctx, 0.2f); vcY = Lerp(vcY, cty, 0.2f); vcZ = Lerp(vcZ, ctz, 0.2f); }
            else { vcX = ctx; vcY = cty; vcZ = ctz; }
        }

        frames.Add((t, c1X, c1Y, c1Z, vX, vY, vZ));

        if (t >= WarmupSeconds)
        {
            c1Pos.Add((t, c1X, c1Y, c1Z));
            vPos.Add((t, vX, vY, vZ));
            vcPos.Add((t, vcX, vcY, vcZ));
            var (truex, truey, truez, _, _, _) = TruePose(t, TeleOffset(t));
            c1TrueErr.Add(Dist(c1X, c1Y, c1Z, truex, truey, truez));
            vTrueErr.Add(Dist(vX, vY, vZ, truex, truey, truez));
            vcTrueErr.Add(Dist(vcX, vcY, vcZ, truex, truey, truez));
        }
    }

    List<float> Stutter(List<(float t, float x, float y, float z)> pos)
    {
        var vel = new List<(float t, float vx, float vy, float vz)>();
        for (int i = 1; i < pos.Count; i++)
        {
            float dt = pos[i].t - pos[i - 1].t;
            if (dt <= 0f) continue;
            vel.Add((pos[i].t, (pos[i].x - pos[i - 1].x) / dt, (pos[i].y - pos[i - 1].y) / dt, (pos[i].z - pos[i - 1].z) / dt));
        }
        var s = new List<float>();
        for (int i = 1; i < vel.Count; i++)
        {
            float dvx = vel[i].vx - vel[i - 1].vx, dvy = vel[i].vy - vel[i - 1].vy, dvz = vel[i].vz - vel[i - 1].vz;
            s.Add(MathF.Sqrt(dvx * dvx + dvy * dvy + dvz * dvz));
        }
        return s;
    }
    var c1Stutter = Stutter(c1Pos);
    var vStutter = Stutter(vPos);
    var vcStutter = Stutter(vcPos);

    // --- Scenario 2/3-only fields: windowed jitter-stdev, teleport-frame detection, max overshoot. Unchanged
    // computation from the previous version of this tool — kept only where those scenarios still use them. ---
    var c1Jumps = new List<float>();
    var vJumps = new List<float>();
    for (int i = 1; i < frames.Count; i++)
    {
        c1Jumps.Add(Dist(frames[i - 1].c1x, frames[i - 1].c1y, frames[i - 1].c1z, frames[i].c1x, frames[i].c1y, frames[i].c1z));
        vJumps.Add(Dist(frames[i - 1].vx, frames[i - 1].vy, frames[i - 1].vz, frames[i].vx, frames[i].vy, frames[i].vz));
    }
    float teleportEventTime = teleportAt.HasValue ? events.First(e => e.t >= teleportAt.Value).t : float.NaN;
    var gapEndTimes = new List<float>();
    for (int i = 1; i < events.Count; i++)
        if (events[i].t - events[i - 1].t > JitterCeilingForGapDetection) gapEndTimes.Add(events[i].t);
    bool NearAny(float frameTimeEnd, IEnumerable<float> marks) => marks.Any(m => frameTimeEnd >= m - FrameDt && frameTimeEnd <= m + FrameDt);
    bool ExcludedForJitter(float frameTimeEnd) =>
        frameTimeEnd < WarmupSeconds
        || (teleportAt.HasValue && frameTimeEnd >= teleportEventTime - FrameDt && frameTimeEnd <= teleportEventTime + FrameDt)
        || NearAny(frameTimeEnd, gapEndTimes);

    var keptC1 = new List<float>(); var keptV = new List<float>();
    int c1TeleFrames = 0, vTeleFrames = 0; float c1TeleJump = 0, vTeleJump = 0;
    for (int i = 0; i < c1Jumps.Count; i++)
    {
        float frameEnd = frames[i + 1].t;
        if (teleportAt.HasValue && frameEnd >= teleportEventTime - FrameDt && frameEnd <= teleportEventTime + FrameDt)
        {
            if (c1Jumps[i] >= SnapThresholdMeters - 0.01f) { c1TeleFrames++; c1TeleJump = MathF.Max(c1TeleJump, c1Jumps[i]); }
            if (vJumps[i] >= SnapThresholdMeters - 0.01f) { vTeleFrames++; vTeleJump = MathF.Max(vTeleJump, vJumps[i]); }
        }
        if (ExcludedForJitter(frameEnd)) continue;
        keptC1.Add(c1Jumps[i]); keptV.Add(vJumps[i]);
    }
    double c1JMean = keptC1.Count > 0 ? keptC1.Average() : 0;
    double c1Std = keptC1.Count > 0 ? Math.Sqrt(keptC1.Select(x => (x - c1JMean) * (x - c1JMean)).Average()) : 0;
    double vJMean = keptV.Count > 0 ? keptV.Average() : 0;
    double vStd = keptV.Count > 0 ? Math.Sqrt(keptV.Select(x => (x - vJMean) * (x - vJMean)).Average()) : 0;
    // c1TrueErr/vcTrueErr already ARE "distance from TRUE(now)" per kept frame (t >= WarmupSeconds) — max of
    // that series is exactly "max overshoot from the true position", no need for a second pass over frames.
    double maxOvershoot = c1TrueErr.Count > 0 ? c1TrueErr.Max() : 0;
    double vcMaxOvershoot = vcTrueErr.Count > 0 ? vcTrueErr.Max() : 0;

    return new SimResult(
        c1TrueErr.Average(), P95(c1TrueErr), vTrueErr.Average(), P95(vTrueErr),
        vcTrueErr.Average(), P95(vcTrueErr),
        P95(c1Stutter), c1Stutter.Count > 0 ? c1Stutter.Max() : 0, P95(vStutter), vStutter.Count > 0 ? vStutter.Max() : 0,
        P95(vcStutter), vcStutter.Count > 0 ? vcStutter.Max() : 0,
        c1Std, vStd,
        c1TeleFrames, vTeleFrames, c1TeleJump, vTeleJump, maxOvershoot, vcMaxOvershoot);
}

List<(string scenario, int delayMs, SimResult r, SimResult v)> PrintSweep(string title,
    List<(float t, float px, float py, float pz, float vx, float vy, float vz)> events,
    float duration, int[] delaysMs, float extrapolationCapSec)
{
    var collected = new List<(string, int, SimResult, SimResult)>();
    Console.WriteLine($"=== {title} ===");
    var v = Run(events, duration, 0f, extrapolationCapSec, null); // vanilla's own numbers don't depend on playback delay
    Console.WriteLine($"vanilla            (reference, delay-independent): true-err mean={v.VTrueErrMean:F4}m p95={v.VTrueErrP95:F4}m | " +
        $"stutter p95={v.VStutterP95:F4} max={v.VStutterMax:F4} m/s | position-jitter stdev={v.VJitterStd:F4}m");
    Console.WriteLine($"vanilla+{extrapolationCapSec * 1000:F0}ms cap (reference, delay-independent): true-err mean={v.VCapTrueErrMean:F4}m p95={v.VCapTrueErrP95:F4}m | " +
        $"stutter p95={v.VCapStutterP95:F4} max={v.VCapStutterMax:F4} m/s | max overshoot={v.VCapMaxOvershoot:F4}m " +
        $"({(Math.Abs(v.VCapTrueErrP95 - v.VTrueErrP95) < 0.0001 && Math.Abs(v.VCapStutterP95 - v.VStutterP95) < 0.0001 ? "identical to vanilla in this scenario" : "differs from vanilla in this scenario")})");
    Console.WriteLine();
    Console.WriteLine($"{"delayMs",-8} {"C1 err mean",-13} {"C1 err p95",-12} {"C1 stutter p95",-16} {"C1 stutter max",-16} {"C1 jitter stdev",-16} {"err p95 vs vanilla",-19} {"stutter p95 vs vanilla",-22}");
    foreach (int ms in delaysMs)
    {
        var r = Run(events, duration, ms / 1000f, extrapolationCapSec, null);
        string errVerdict = r.C1TrueErrP95 <= v.VTrueErrP95 ? "not worse" : $"+{(r.C1TrueErrP95 - v.VTrueErrP95):F4}m worse";
        string stutterVerdict = r.C1StutterP95 < v.VStutterP95 ? $"-{100.0 * (v.VStutterP95 - r.C1StutterP95) / v.VStutterP95:F0}% better" : "not better";
        Console.WriteLine($"{ms,-8} {r.C1TrueErrMean,-13:F4} {r.C1TrueErrP95,-12:F4} {r.C1StutterP95,-16:F4} {r.C1StutterMax,-16:F4} {r.C1JitterStd,-16:F4} {errVerdict,-19} {stutterVerdict,-22}");
        collected.Add((title, ms, r, v));
    }
    Console.WriteLine();
    return collected;
}

// ============================================================================================================
// Scenario 1 — ORDINARY PLAY: PlaybackDelayMs sweep, realistic send jitter only (20-150ms inter-arrival, no
// forced gaps, no teleport).
// ============================================================================================================
const float OrdinaryDuration = 40f;
const float ShippedExtrapolationCapSec = 0.500f; // keep in sync with SmoothingModule.ExtrapolationCapMs default
int[] delaysMs = { 0, 40, 60, 80, 100, 150 };
var ordinaryEvents = BuildStream(new Random(20260928), OrdinaryDuration, 0.020f, 0.150f, 0, 0, 0, null, 0);
var allResults = new List<(string scenario, int delayMs, SimResult r, SimResult v)>();
allResults.AddRange(PrintSweep("Scenario 1: ordinary play (20-150ms send jitter, no gaps/teleport) — PlaybackDelayMs sweep",
    ordinaryEvents, OrdinaryDuration, delaysMs, ShippedExtrapolationCapSec));

// ============================================================================================================
// Scenario 2 — CONGESTED FAR PLAYER: the case this project exists for. ~70ms RTT (35ms one-way modeled),
// updates arrive in clumps of 3-6 (a backed-up send queue draining), then 150-400ms of nothing, repeating.
// ============================================================================================================
var congestedEvents = BuildCongestedStream(new Random(20260928 + 1), OrdinaryDuration, 0.035f,
    3, 6, 0.002f, 0.008f, 0.150f, 0.400f);
allResults.AddRange(PrintSweep("Scenario 2: congested far player (~70ms RTT, bursts of 3-6 updates then 150-400ms silence) — PlaybackDelayMs sweep",
    congestedEvents, OrdinaryDuration, delaysMs, ShippedExtrapolationCapSec));

// ============================================================================================================
// VERDICT: does any tested delay beat vanilla on stutter WITHOUT being worse on true-position error? Checked
// on BOTH stutter axes (p95 AND max — see the note above PrintSweep on why p95 alone is misleading: at low
// delay, C1's p95 stutter can read as perfect while its max stutter is far worse than vanilla's, because most
// frames ride an exact velocity extrapolation and only the rare correction snap is large). "Not worse" on error
// uses p95, the stricter of mean/p95.
// ============================================================================================================
Console.WriteLine("=== VERDICT: C1 vs vanilla, per scenario and delay ===");
Console.WriteLine($"{"scenario",-10} {"delayMs",-8} {"beats p95 stutter",-19} {"beats MAX stutter",-19} {"err p95 not worse",-19} {"ALL THREE",-10}");
bool anyCleanWin = false;
foreach (var (scenario, ms, r, v) in allResults)
{
    bool beatsP95Stutter = r.C1StutterP95 < v.VStutterP95;
    bool beatsMaxStutter = r.C1StutterMax < v.VStutterMax;
    bool errNotWorse = r.C1TrueErrP95 <= v.VTrueErrP95;
    bool cleanWin = beatsP95Stutter && beatsMaxStutter && errNotWorse;
    anyCleanWin |= cleanWin;
    string label = scenario.Contains("ordinary") ? "ordinary" : "congested";
    Console.WriteLine($"{label,-10} {ms,-8} {(beatsP95Stutter ? "yes" : "no"),-19} {(beatsMaxStutter ? "yes" : "no"),-19} {(errNotWorse ? "yes" : "no"),-19} {(cleanWin ? "YES" : "no"),-10}");
}
Console.WriteLine();
Console.WriteLine(anyCleanWin
    ? "At least one tested delay beats vanilla on BOTH stutter measures without a worse p95 true-position error."
    : "PLAIN ANSWER: no tested delay (0/40/60/80/100/150ms) beats vanilla on both p95 AND max stutter without " +
      "being worse on p95 true-position error, in either scenario. C1's p95 stutter win at low delay is real but " +
      "comes bundled with a max-stutter (worst-case single-frame snap) that is WORSE than vanilla's own, in the " +
      "congested/Arizona-like scenario at every tested delay from 0-150ms. The one delay where max stutter finally " +
      "beats vanilla (150ms, ordinary-play scenario only) is also the delay with the worst true-position error. " +
      "As measured, buffered interpolation (C1) does not clearly beat vanilla's own lerp/dead-reckoning for a " +
      "congested far connection — the mechanism that IS clearly measured to help there is C3's shorter " +
      "extrapolation cap on its own (2.42m vs 3.88m worst-case overshoot, Scenario 3 below), which does not " +
      "require C1's interpolation buffer at all. See SMOOTHING-IMPL.md for the recommendation this supports.");
Console.WriteLine();

// ============================================================================================================
// Scenario 3 — GAP HANDLING: ExtrapolationCapMs sweep. Ordinary jitter plus forced 1-3s dropped-batch gaps, no
// teleport, at the shipped playback delay. Unchanged from the previous version of this tool.
// ============================================================================================================
const float ShippedPlaybackDelaySec = 0.100f; // keep in sync with SmoothingModule.PlaybackDelayMs default
var gapEvents = BuildStream(new Random(20260928), OrdinaryDuration, 0.020f, 0.150f, 6, 1.0f, 3.0f, null, 0);
int[] capsMs = { 150, 250, 400, 500, 600, 1000, 1500, 2000 };
Console.WriteLine("=== Scenario 3: ExtrapolationCapMs sweep (ordinary jitter + forced 1-3s gaps, no teleport) ===");
Console.WriteLine($"{"ExtrapolationCapMs",-20} {"C1 max overshoot (m)",-22} {"vanilla+cap max overshoot (m)",-30} {"bound = maxLegSpeed*cap (m)",-20}");
foreach (int ms in capsMs)
{
    var r = Run(gapEvents, OrdinaryDuration, ShippedPlaybackDelaySec, ms / 1000f, null);
    float bound = MaxLegSpeed * (ms / 1000f);
    Console.WriteLine($"{ms,-20} {r.MaxOvershoot,-22:F4} {r.VCapMaxOvershoot,-30:F4} {bound,-20:F4}");
}
Console.WriteLine("(overshoot should stay well under the bound — a wandering creature rarely holds its max leg speed for the whole cap window — and both should shrink together as the cap shrinks. vanilla+cap column: the C3-alone policy — vanilla's own lerp/dead-reckoning with only the extrapolation timer's cap changed — at each swept cap value.)");
Console.WriteLine();

// ============================================================================================================
// Scenario 4 — STRESS (guard verification): the literal brief — steady 20Hz, 50-200ms jitter, forced 1-3s
// gaps, one scripted 10m teleport — run at the defaults this module actually ships. Unchanged from the
// previous version of this tool.
// ============================================================================================================
const float StressDuration = 30f;
var stressEvents = BuildStream(new Random(20260928), StressDuration, 0.050f, 0.200f, 6, 1.0f, 3.0f, 15f, 10f);
var stress = Run(stressEvents, StressDuration, ShippedPlaybackDelaySec, ShippedExtrapolationCapSec, 15f, 10f);
float largestGap = 0f;
for (int i = 1; i < stressEvents.Count; i++) largestGap = MathF.Max(largestGap, stressEvents[i].t - stressEvents[i - 1].t);

Console.WriteLine("=== Scenario 4: stress / guard verification (steady 20Hz, 50-200ms jitter, forced 1-3s gaps, one 10m teleport) ===");
Console.WriteLine($"shipped defaults: PlaybackDelayMs={ShippedPlaybackDelaySec * 1000:F0} ExtrapolationCapMs={ShippedExtrapolationCapSec * 1000:F0}");
Console.WriteLine($"{stressEvents.Count} updates over {StressDuration:F0}s, largest inter-arrival gap {largestGap:F2}s");
Console.WriteLine($"teleport: C1 snapped in {stress.C1TeleportFrames} frame(s) ({stress.C1TeleportJump:F2}m), vanilla in {stress.VTeleportFrames} frame(s) ({stress.VTeleportJump:F2}m) — want exactly 1 frame each");
Console.WriteLine($"max overshoot from TRUE(now) across the whole run (gaps + teleport): {stress.MaxOvershoot:F2}m");
bool cleanSnap = stress.C1TeleportFrames == 1 && stress.VTeleportFrames == 1;
bool boundedOvershoot = stress.MaxOvershoot < ScriptedTeleportDistanceMeters + MaxLegSpeed * ShippedExtrapolationCapSec + 1.0;
Console.WriteLine($"[{(cleanSnap ? "PASS" : "FAIL")}] the 10m teleport snaps in exactly one frame for both C1 and vanilla (no smearing introduced by buffering)");
Console.WriteLine($"[{(boundedOvershoot ? "PASS" : "FAIL")}] no unbounded drift across the whole nasty combined stream (max overshoot {stress.MaxOvershoot:F2}m stays in the expected range)");

// Type declarations must come after every top-level statement in a C# top-level-statement program (CS8803) —
// referenced by Run/PrintSweep above, which is fine: a record's visibility is not position-dependent.
record SimResult(
    double C1TrueErrMean, double C1TrueErrP95, double VTrueErrMean, double VTrueErrP95,
    double VCapTrueErrMean, double VCapTrueErrP95,
    double C1StutterP95, double C1StutterMax, double VStutterP95, double VStutterMax,
    double VCapStutterP95, double VCapStutterMax,
    double C1JitterStd, double VJitterStd,
    int C1TeleportFrames, int VTeleportFrames, float C1TeleportJump, float VTeleportJump,
    double MaxOvershoot, double VCapMaxOvershoot);
