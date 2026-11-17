// Offline round-trip check for src/Compression (D1/D2), loading the REAL built DLL by reflection (like
// tools/t9-sim). Two parts:
//
//   1. Compression codec: NoodlesMcDoodles.Compression.CompressionCodec.Compress/Decompress has ZERO
//      dependency on UnityEngine or the vanilla game assemblies (plain System.IO.Compression.GZipStream), so
//      this loads and calls the REAL production methods directly, on realistic synthetic ZDO-batch byte
//      shapes at 10-128 KiB, proving byte-exact round-trips and measuring ratio/CPU at both CompressionLevel
//      options the mod exposes — this is the number ZdoCompression.CompressionLevel's shipped default rests on.
//
//   2. In-flight/ack/timeout state machine: NoodlesMcDoodles.Compression.InFlightTracker<TKey,TItem> is
//      ALSO Unity/vanilla-independent by design (every clock reading is a caller-supplied float — see that
//      class's own remarks) specifically so it could be driven here, by reflection, closed over simple
//      surrogate types (long/int) instead of the real ZDOID/InFlightItem. This is the REAL shipped algorithm,
//      not a reimplementation of it.
//
// NOT attempted: loading the real ZPackage/ZDO/ZDOID types from assembly_valheim.dll. CompressionState (the
// ZNetPeer/ZDOMan.ZDOPeer-specific glue around InFlightTracker) calls UnityEngine.Time.realtimeSinceStartup
// directly, and ZPackage's own Vector3 overloads need UnityEngine.Vector3 — both are backed by native Unity
// engine calls with no engine process behind them here, so touching either is expected to throw immediately
// (DllNotFoundException/TypeInitializationException) rather than run correctly. Per the task's own allowance
// ("if ZPackage/ZDO cannot be loaded outside the game, test your own framing and state machine classes and
// say so") this tool tests the two classes that genuinely can run standalone instead, and says so below.

using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Reflection;

string dllPath = args.Length > 0 ? args[0] : "../../bin/NoodlesMcDoodles.dll";
string fullPath = Path.GetFullPath(dllPath);
if (!File.Exists(fullPath))
{
    Console.WriteLine($"FAIL: {fullPath} does not exist. Build the mod first.");
    Environment.Exit(1);
}

var asm = Assembly.LoadFrom(fullPath);
Console.WriteLine($"Loaded {fullPath}");
Console.WriteLine();

bool allPassed = true;
allPassed &= RunCodecTests(asm);
Console.WriteLine();
allPassed &= RunInFlightTrackerTests(asm);
Console.WriteLine();
allPassed &= RunHzSimulation(asm);
Console.WriteLine();
allPassed &= RunSupersedeComparatorTests(asm);

Console.WriteLine();
Console.WriteLine(allPassed ? "ALL CHECKS PASSED" : "SOME CHECKS FAILED");
Environment.Exit(allPassed ? 0 : 1);


// ============================================================================================================
// Part 1 — CompressionCodec: real production Compress/Decompress, realistic synthetic ZDO-batch byte shapes.
// ============================================================================================================
static bool RunCodecTests(Assembly asm)
{
    Console.WriteLine("=== CompressionCodec (real DLL, System.IO.Compression.GZipStream, no Unity needed) ===");

    var codecType = asm.GetType("NoodlesMcDoodles.Compression.CompressionCodec", true)!;
    var compressMethod = codecType.GetMethod("Compress", BindingFlags.Public | BindingFlags.Static)!;
    var decompressMethod = codecType.GetMethod("Decompress", BindingFlags.Public | BindingFlags.Static)!;

    var levelType = typeof(CompressionLevel); // System.IO.Compression.CompressionLevel — same enum, no marshaling risk
    int[] sizes = { 10 * 1024, 32 * 1024, 64 * 1024, 128 * 1024 };
    var levels = new[] { CompressionLevel.Fastest, CompressionLevel.Optimal };

    bool ok = true;
    Console.WriteLine($"{"target KiB",10} {"level",-9} {"raw B",8} {"gzip B",8} {"ratio",6} {"comp ms",8} {"decomp ms",9} {"roundtrip",9}");

    // Fixed seed: reproducible numbers across runs (needed since this run's output is what the shipped
    // CompressionLevel default is picked from — see docs/impl/COMPRESSION-IMPL.md's Defaults table).
    var rng = new Random(20260928);

    // One fixed batch per size (not regenerated per level) so Fastest vs Optimal are compared on IDENTICAL
    // bytes, not two different random draws.
    foreach (int targetBytes in sizes)
    {
        byte[] raw = BuildRealisticZdoBatch(targetBytes, rng);
        foreach (var level in levels)
        {
            var swC = Stopwatch.StartNew();
            var compressed = (byte[])compressMethod.Invoke(null, new object[] { raw, level })!;
            swC.Stop();

            var swD = Stopwatch.StartNew();
            var roundTripped = (byte[])decompressMethod.Invoke(null, new object[] { compressed })!;
            swD.Stop();

            bool exact = ByteExact(raw, roundTripped);
            ok &= exact;
            double ratio = raw.Length / (double)compressed.Length;
            Console.WriteLine($"{targetBytes / 1024,10} {level,-9} {raw.Length,8} {compressed.Length,8} {ratio,6:F2} {swC.Elapsed.TotalMilliseconds,8:F3} {swD.Elapsed.TotalMilliseconds,9:F3} {(exact ? "byte-exact" : "MISMATCH"),9}");
        }
    }

    // Bytes-saved-per-ms-CPU at the knee of the curve: average across sizes for each level, comparing
    // Fastest's own ratio/CPU against Optimal's — this is the number the "not overkill" default rests on.
    Console.WriteLine();
    Console.WriteLine("Per-level average across all four sizes (bytes saved per ms of SEND-side compress CPU):");
    foreach (var level in levels)
    {
        long totalRaw = 0, totalCompressed = 0; double totalMs = 0;
        var rng2 = new Random(20260928); // same seed -> same batches as above, isolated pass per level
        foreach (int targetBytes in sizes)
        {
            byte[] raw = BuildRealisticZdoBatch(targetBytes, rng2);
            var sw = Stopwatch.StartNew();
            var compressed = (byte[])compressMethod.Invoke(null, new object[] { raw, level })!;
            sw.Stop();
            totalRaw += raw.Length;
            totalCompressed += compressed.Length;
            totalMs += sw.Elapsed.TotalMilliseconds;
        }
        long saved = totalRaw - totalCompressed;
        double savedPerMs = totalMs > 0 ? saved / totalMs : 0;
        Console.WriteLine($"  {level,-9} bytesSaved={saved,7} totalCompressMs={totalMs,7:F3} bytesSavedPerMs={savedPerMs,10:F0} ratio={totalRaw / (double)totalCompressed,5:F2}");
    }

    // Level-agnostic decompression sanity: bytes compressed at Optimal must decompress correctly too (gzip's
    // decoder doesn't need to know the encoder's level — see CompressionCodec's own remarks).
    byte[] optimalRaw = BuildRealisticZdoBatch(32 * 1024, new Random(1));
    var optimalCompressed = (byte[])compressMethod.Invoke(null, new object[] { optimalRaw, CompressionLevel.Optimal })!;
    var optimalRoundTrip = (byte[])decompressMethod.Invoke(null, new object[] { optimalCompressed })!;
    bool levelAgnosticOk = ByteExact(optimalRaw, optimalRoundTrip);
    ok &= levelAgnosticOk;
    Console.WriteLine();
    Console.WriteLine($"Decompress is level-agnostic (Optimal-encoded bytes decode correctly): {(levelAgnosticOk ? "PASS" : "FAIL")}");

    return ok;
}

/// <summary>
/// Synthesizes a byte sequence matching the EXACT wire shape ZDOMan.SendZDOs/CompressionCodec would compress
/// (research/12 "Where the bytes go", byte-for-byte): per entry, ZDOID(12B: long UserID + uint ID) +
/// OwnerRevision(ushort,2B) + DataRevision(uint,4B) + owner(long,8B) + position(Vector3,12B) = 38B framing,
/// + a 4B nested-length-prefix + a serialized ZDO body (flags+prefab hash+optional rotation+ZDOVars, or a
/// larger byte-array inventory blob for the container-shaped minority) — not through the real ZPackage class
/// (see file header), but field-for-field identical in layout and size. Clustered, repeated values (a handful
/// of shared prefab hashes/owners, nearby jittered positions) rather than uniform random bytes, matching how
/// real ZDOs in one zone actually look (many of the same few creature/item prefabs, all near each other) —
/// uniform random bytes would be near-incompressible and would understate GZip's real-world ratio here.
/// </summary>
static byte[] BuildRealisticZdoBatch(int targetBytes, Random rng)
{
    long[] ownerPool = { 1001L, 1002L, 1003L, 0L }; // a couple of client owners + the server's own session id (0)
    uint[] prefabPool = { 0x1a2b3c4dU, 0x5e6f7080U, 0x11223344U, 0x0a0b0c0dU, 0x99887766U, 0x77665544U };
    float baseX = 1000f, baseZ = -500f;

    using var ms = new MemoryStream(targetBytes + 4096);
    using var bw = new BinaryWriter(ms);

    while (ms.Length < targetBytes)
    {
        // ZDOID
        bw.Write(ownerPool[rng.Next(ownerPool.Length)]);
        bw.Write((uint)rng.Next(1, 200_000));
        // OwnerRevision / DataRevision / owner / position — vanilla's own per-item framing (SERVER:77103-77107)
        bw.Write((ushort)rng.Next(0, 4));
        bw.Write((uint)rng.Next(1, 500));
        bw.Write(ownerPool[rng.Next(ownerPool.Length)]);
        bw.Write(baseX + (float)(rng.NextDouble() * 40 - 20));
        bw.Write((float)(rng.NextDouble() * 3));
        bw.Write(baseZ + (float)(rng.NextDouble() * 40 - 20));

        bool isContainer = rng.NextDouble() < 0.08; // ~8% of entries are container-shaped (research/12: "several hundred bytes to a few KB")
        byte[] body = isContainer ? BuildContainerBody(rng, prefabPool) : BuildSmallBody(rng, prefabPool);
        bw.Write(body.Length); // ZPackage.Write(ZPackage)'s own 4B nested length-prefix (SERVER:83133-83137)
        bw.Write(body);
    }
    return ms.ToArray();
}

static byte[] BuildSmallBody(Random rng, uint[] prefabPool)
{
    // Flags(2B) + prefab hash(4B) + optional rotation(12B, ~40% of entries per research/12) + 1-4 float/int
    // ZDOVars (research/12: "~9B" per float entry: 4B key + 4B value + ~1B WriteNumItems count byte, amortized).
    using var ms = new MemoryStream();
    using var bw = new BinaryWriter(ms);
    bool hasRotation = rng.NextDouble() < 0.4;
    ushort flags = (ushort)(0x0001 | (hasRotation ? 0x0002 : 0) | 0x0010 /* one non-empty category */);
    bw.Write(flags);
    bw.Write(prefabPool[rng.Next(prefabPool.Length)]);
    if (hasRotation)
    {
        bw.Write((float)rng.NextDouble() * 360f);
        bw.Write((float)rng.NextDouble() * 360f);
        bw.Write((float)rng.NextDouble() * 360f);
    }
    int varCount = rng.Next(1, 5);
    bw.Write((byte)varCount);
    for (int i = 0; i < varCount; i++)
    {
        bw.Write(rng.Next(0, 40)); // small, repeated key space — real ZDOVars keys are a handful of well-known ints
        bw.Write((float)(rng.NextDouble() * 100));
    }
    return ms.ToArray();
}

static byte[] BuildContainerBody(Random rng, uint[] prefabPool)
{
    // Flags/prefab as above, plus a larger byte-array inventory blob with SOME repeated structure (a handful
    // of "item code" chunks repeated with small variation) rather than pure noise — matching how a real
    // inventory's own serialized item list actually looks (many stacks of the same few item types).
    using var ms = new MemoryStream();
    using var bw = new BinaryWriter(ms);
    bw.Write((ushort)0x0011);
    bw.Write(prefabPool[rng.Next(prefabPool.Length)]);

    int blobLen = rng.Next(500, 1500);
    byte[] blob = new byte[blobLen];
    byte[][] itemChunks =
    {
        new byte[] { 0x10, 0x20, 0x00, 0x01, 0x00, 0x00, 0x00, 0x40 },
        new byte[] { 0x11, 0x21, 0x00, 0x02, 0x00, 0x00, 0x00, 0x20 },
        new byte[] { 0x12, 0x22, 0x00, 0x01, 0x00, 0x00, 0x00, 0x64 },
    };
    int pos = 0;
    while (pos < blobLen)
    {
        var chunk = itemChunks[rng.Next(itemChunks.Length)];
        int n = Math.Min(chunk.Length, blobLen - pos);
        Array.Copy(chunk, 0, blob, pos, n);
        pos += n;
    }
    bw.Write(blob.Length);
    bw.Write(blob);
    return ms.ToArray();
}

static bool ByteExact(byte[] a, byte[] b)
{
    if (a.Length != b.Length) return false;
    for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
    return true;
}


// ============================================================================================================
// Part 2 — InFlightTracker<TKey,TItem>: the REAL ack/nack/timeout state machine, closed over surrogate types.
// ============================================================================================================
static bool RunInFlightTrackerTests(Assembly asm)
{
    Console.WriteLine("=== InFlightTracker<long,int> (real DLL, closed over surrogate types — no ZDOID/Unity needed) ===");
    Console.WriteLine("1.0.8: IsInFlightUnacked now also returns the newest in-flight item for the key (5 args, not");
    Console.WriteLine("4 — see below); the surrogate TItem here is a plain int 'revision' the test compares directly,");
    Console.WriteLine("standing in for production's real DataRevision/OwnerRevision comparison in CompressionState.");

    var openType = asm.GetType("NoodlesMcDoodles.Compression.InFlightTracker`2", true)!;
    var closedType = openType.MakeGenericType(typeof(long), typeof(int)); // TKey surrogate for ZDOID, TItem surrogate for a revision payload

    var track = closedType.GetMethod("Track")!;
    var isInFlight = closedType.GetMethod("IsInFlightUnacked")!;
    var sweep = closedType.GetMethod("SweepExpired")!;
    var commit = closedType.GetMethod("Commit")!;
    var drop = closedType.GetMethod("Drop")!;
    var clear = closedType.GetMethod("Clear")!;
    var countProp = closedType.GetProperty("Count")!;

    bool ok = true;
    int caseNum = 0;

    bool Check(string name, bool cond)
    {
        caseNum++;
        Console.WriteLine($"  [{caseNum}] {name}: {(cond ? "PASS" : "FAIL")}");
        return cond;
    }

    int Count(object t) => (int)countProp.GetValue(t)!;

    // ---- Scenario: lost ack (timeout) -------------------------------------------------------------------
    {
        var t = Activator.CreateInstance(closedType)!;
        track.Invoke(t, new object[] { 1, new long[] { 100L, 101L, 102L }, new int[] { 5, 6, 7 }, 0f });
        var argsFresh = new object[] { 100L, 1f, 3f, null, null };
        ok &= Check("lost-ack: freshly tracked batch is in-flight (well within timeout)",
            (bool)isInFlight.Invoke(t, argsFresh)!);
        ok &= Check("lost-ack: in-flight query hands back the batch's own item (5)", (int)argsFresh[3]! == 5);

        // Advance "now" past the timeout without any ack — IsInFlightUnacked must lazily expire it.
        var args = new object[] { 101L, 5f /* now */, 3f /* timeout */, null, null };
        bool stillInFlight = (bool)isInFlight.Invoke(t, args)!;
        bool justExpired = (bool)args[4]!;
        ok &= Check("lost-ack: expired batch reads as not-in-flight", !stillInFlight);
        ok &= Check("lost-ack: expiry is reported (justExpired=true) exactly once", justExpired);
        ok &= Check("lost-ack: expiry fully purges the batch (Count==0 afterward)", Count(t) == 0);

        // The ZDOs are NEVER dropped — only never marked "known" — so the caller's own ShouldSend naturally
        // reselects them; this class's only obligation is "don't say in-flight forever", which the above proves.
        var args2 = new object[] { 102L, 6f, 3f, null, null };
        bool secondCheck = (bool)isInFlight.Invoke(t, args2)!;
        ok &= Check("lost-ack: re-querying an already-expired key is a clean 'not in flight', not an error", !secondCheck);
    }

    // ---- Scenario: duplicate ack --------------------------------------------------------------------------
    {
        var t = Activator.CreateInstance(closedType)!;
        track.Invoke(t, new object[] { 2, new long[] { 200L, 201L }, new int[] { 9, 10 }, 0f });

        var commitArgs1 = new object[] { 2, null };
        var items1 = (int[])commit.Invoke(t, commitArgs1)!;
        ok &= Check("duplicate-ack: first commit returns the batch's items", items1 != null && items1.Length == 2 && items1[0] == 9 && items1[1] == 10);
        ok &= Check("duplicate-ack: first commit empties the tracker", Count(t) == 0);

        var commitArgs2 = new object[] { 2, null };
        var items2 = (int[])commit.Invoke(t, commitArgs2)!;
        ok &= Check("duplicate-ack: second commit of the SAME seq is a no-op (null), never double-applies", items2 == null);
    }

    // ---- Scenario: out-of-order ack -----------------------------------------------------------------------
    {
        var t = Activator.CreateInstance(closedType)!;
        track.Invoke(t, new object[] { 3, new long[] { 300L }, new int[] { 30 }, 0f });
        track.Invoke(t, new object[] { 4, new long[] { 301L }, new int[] { 31 }, 0f });
        ok &= Check("out-of-order: two independent batches both tracked", Count(t) == 2);

        // Commit the LATER sequence number first.
        var items4 = (int[])commit.Invoke(t, new object[] { 4, null })!;
        ok &= Check("out-of-order: committing seq 4 before seq 3 still resolves seq 4 correctly", items4 != null && items4[0] == 31);
        ok &= Check("out-of-order: only seq 4's key is released, seq 3 is untouched", Count(t) == 1);

        var stillInFlight3 = (bool)isInFlight.Invoke(t, new object[] { 300L, 1f, 100f, null, null })!;
        ok &= Check("out-of-order: seq 3's own key is still correctly tracked after seq 4 resolved", stillInFlight3);

        var items3 = (int[])commit.Invoke(t, new object[] { 3, null })!;
        ok &= Check("out-of-order: seq 3 resolves correctly afterward, in whatever order acks actually arrive", items3 != null && items3[0] == 30);
        ok &= Check("out-of-order: both resolved, tracker empty", Count(t) == 0);
    }

    // ---- Scenario: nack (explicit drop, never marks known) -------------------------------------------------
    {
        var t = Activator.CreateInstance(closedType)!;
        track.Invoke(t, new object[] { 5, new long[] { 500L }, new int[] { 50 }, 0f });
        drop.Invoke(t, new object[] { 5 });
        ok &= Check("nack: Drop releases the batch without returning/applying its items", Count(t) == 0);
        drop.Invoke(t, new object[] { 5 }); // duplicate nack / already-resolved seq
        ok &= Check("nack: dropping an already-dropped seq is a harmless no-op", Count(t) == 0);
    }

    // ---- Scenario: reconnect ---------------------------------------------------------------------------
    {
        // (a) AddPeer's own real behaviour: a reconnect allocates a brand-new ZDOPeer, and therefore (per
        // CompressionState) a brand-new PeerCompressionState/tracker — simulate directly with a fresh instance.
        var fresh = Activator.CreateInstance(closedType)!;
        ok &= Check("reconnect: a brand-new tracker instance starts empty", Count(fresh) == 0);

        // (b) The kill-switch / local-flag-off flush path: Clear() on a tracker with real in-flight state.
        var t = Activator.CreateInstance(closedType)!;
        track.Invoke(t, new object[] { 6, new long[] { 600L, 601L }, new int[] { 60, 61 }, 0f });
        track.Invoke(t, new object[] { 7, new long[] { 602L }, new int[] { 62 }, 0f });
        ok &= Check("reconnect/flush: two batches tracked before Clear()", Count(t) == 3);
        clear.Invoke(t, null);
        ok &= Check("reconnect/flush: Clear() empties everything immediately", Count(t) == 0);
        var postClearCommit = (int[])commit.Invoke(t, new object[] { 6, null })!;
        ok &= Check("reconnect/flush: a stale seq from before Clear() is a safe no-op afterward", postClearCommit == null);
    }

    // ---- Scenario: SweepExpired (the opportunistic per-peer-round sweep, not just per-key lazy expiry) ------
    {
        var t = Activator.CreateInstance(closedType)!;
        track.Invoke(t, new object[] { 8, new long[] { 800L }, new int[] { 80 }, 0f });
        track.Invoke(t, new object[] { 9, new long[] { 801L }, new int[] { 81 }, 10f }); // sent later, not yet expired
        var expired = (System.Collections.IList)sweep.Invoke(t, new object[] { 12f /* now */, 3f /* timeout */ })!;
        ok &= Check("sweep: only the batch older than the timeout is swept", expired.Count == 1 && (int)expired[0]! == 8);
        ok &= Check("sweep: the not-yet-expired batch survives the sweep", Count(t) == 1);
    }

    // ============================================================================================================
    // 1.0.8 superseding-send scenarios: a key can now be recorded by MORE THAN ONE simultaneously in-flight
    // batch (an older, not-yet-acked send and a newer one that superseded it — see InFlightTracker's own
    // class remarks and CompressionState.IsInFlightUnacked). The risk this set of scenarios exists to catch:
    // Commit/Drop used to erase a key's index entry unconditionally, which — once a key can be in two
    // batches — could wrongly clobber a NEWER batch's own claim on that key when the OLDER one resolves.
    // ============================================================================================================

    // ---- Scenario: key re-tracked by a newer batch; the older batch's own commit must not clear it -----------
    {
        var t = Activator.CreateInstance(closedType)!;
        track.Invoke(t, new object[] { 10, new long[] { 1000L }, new int[] { 100 }, 0f });  // older batch, revision 100
        track.Invoke(t, new object[] { 11, new long[] { 1000L }, new int[] { 200 }, 1f });  // newer batch supersedes it, revision 200
        ok &= Check("supersede/retrack: one key tracked by two batches at once still counts once (Count==1)", Count(t) == 1);

        var qNewest = new object[] { 1000L, 2f, 100f, null, null };
        bool inFlight1 = (bool)isInFlight.Invoke(t, qNewest)!;
        ok &= Check("supersede/retrack: IsInFlightUnacked reports the NEWEST batch's item (200), not the older (100)",
            inFlight1 && (int)qNewest[3]! == 200);

        // Ack the OLDER batch (seq 10) first — out-of-order-by-key, in-order-by-seq (the ordinary case: the
        // older batch was sent first, so its ack would normally also land first).
        var items10 = (int[])commit.Invoke(t, new object[] { 10, null })!;
        ok &= Check("supersede/retrack: committing the OLDER batch still returns its OWN (old) item", items10 != null && items10.Length == 1 && items10[0] == 100);

        var qAfterOld = new object[] { 1000L, 2f, 100f, null, null };
        bool inFlight2 = (bool)isInFlight.Invoke(t, qAfterOld)!;
        ok &= Check("supersede/retrack: after the OLDER batch commits, the key is STILL in flight — the NEWER batch still owns it (guard 7 not clobbered)",
            inFlight2 && (int)qAfterOld[3]! == 200);

        // Now ack the NEWER batch (seq 11) too — should fully release the key.
        var items11 = (int[])commit.Invoke(t, new object[] { 11, null })!;
        ok &= Check("supersede/retrack: committing the NEWER batch afterward returns ITS OWN item", items11 != null && items11[0] == 200);
        ok &= Check("supersede/retrack: after both batches resolve (ack older then newer), the key is fully released (Count==0)", Count(t) == 0);
    }

    // ---- Scenario: nack of the NEWER batch after already acking the OLDER one — must end up re-selectable ----
    {
        var t = Activator.CreateInstance(closedType)!;
        track.Invoke(t, new object[] { 20, new long[] { 2000L }, new int[] { 5 }, 0f });
        track.Invoke(t, new object[] { 21, new long[] { 2000L }, new int[] { 9 }, 1f });

        var itemsOld = (int[])commit.Invoke(t, new object[] { 20, null })!; // ack the older batch first
        ok &= Check("supersede/nack: acking the OLDER batch first succeeds and returns its own item", itemsOld != null && itemsOld[0] == 5);

        drop.Invoke(t, new object[] { 21 }); // nack the newer (and, by now, only remaining) batch
        ok &= Check("supersede/nack: nacking the NEWER batch afterward fully releases the key (Count==0)", Count(t) == 0);

        var qAfter = new object[] { 2000L, 5f, 100f, null, null };
        bool inFlightAfter = (bool)isInFlight.Invoke(t, qAfter)!;
        ok &= Check("supersede/nack: IsInFlightUnacked now reports NOT in flight — the caller's own ShouldSend naturally reselects it, nothing stuck", !inFlightAfter);
    }

    // ---- Scenario: the OLDER batch times out while the NEWER one (same key) survives ---------------------------
    {
        var t = Activator.CreateInstance(closedType)!;
        track.Invoke(t, new object[] { 30, new long[] { 3000L, 3001L }, new int[] { 1, 2 }, 0f }); // older batch, TWO keys
        track.Invoke(t, new object[] { 31, new long[] { 3000L }, new int[] { 3 }, 5f });           // newer batch supersedes ONLY key 3000

        // "now"=8, timeout=3: the older batch (sent at t=0, age 8) is past timeout; the newer one (sent at
        // t=5, age 3) is not (age must be STRICTLY greater than timeoutSeconds — see IsInFlightUnacked/SweepExpired).
        var expired = (System.Collections.IList)sweep.Invoke(t, new object[] { 8f, 3f })!;
        ok &= Check("supersede/timeout-older: sweeping expires the OLDER batch (seq 30) only", expired.Count == 1 && (int)expired[0]! == 30);

        var q3000 = new object[] { 3000L, 8f, 3f, null, null };
        bool key3000InFlight = (bool)isInFlight.Invoke(t, q3000)!;
        ok &= Check("supersede/timeout-older: key 3000's mapping survives the older batch's expiry — still owned by the newer, unexpired batch (item==3)",
            key3000InFlight && (int)q3000[3]! == 3);

        // Key 3001 belonged ONLY to the now-expired older batch — must read as reselectable, not stuck.
        var q3001 = new object[] { 3001L, 8f, 3f, null, null };
        bool key3001InFlight = (bool)isInFlight.Invoke(t, q3001)!;
        ok &= Check("supersede/timeout-older: key 3001 (only in the expired batch) is now reselectable", !key3001InFlight);
    }

    // ---- Scenario: the OLDER batch acks, then the NEWER one (now the sole remaining) times out -----------------
    {
        var t = Activator.CreateInstance(closedType)!;
        track.Invoke(t, new object[] { 40, new long[] { 4000L }, new int[] { 1 }, 0f });
        track.Invoke(t, new object[] { 41, new long[] { 4000L }, new int[] { 2 }, 1f });

        var itemsOld = (int[])commit.Invoke(t, new object[] { 40, null })!;
        ok &= Check("supersede/timeout-newer: acking the OLDER batch first succeeds", itemsOld != null && itemsOld[0] == 1);

        // "now"=105, timeout=3: the newer batch (sent at t=1, age 104) is now well past timeout too.
        var expired = (System.Collections.IList)sweep.Invoke(t, new object[] { 105f, 3f })!;
        ok &= Check("supersede/timeout-newer: sweeping later expires the NEWER batch (seq 41) too, once it is the only one left", expired.Count == 1 && (int)expired[0]! == 41);
        ok &= Check("supersede/timeout-newer: key fully released after both resolve (ack older, then timeout newer) — Count==0", Count(t) == 0);
    }

    return ok;
}


// ============================================================================================================
// Part 3 — 1.0.8 stutter fix: discrete-time Hz simulation, OLD guard-7 calling discipline vs NEW, driving the
// SAME real InFlightTracker (not a reimplementation — see file header). One ZDO whose revision changes on
// EVERY send round (the worst case the bug report describes — a moving creature/player), sent every 66ms
// (ZDOMan.SendZDOToPeers2's own per-peer round-robin cadence on a 30fps server), acked after a fixed RTT.
//
//   OLD discipline: reproduces 1.0.7 exactly by construction — it calls the real IsInFlightUnacked and then
//   simply never looks at the item it now also returns, suppressing on ANY in-flight batch regardless of
//   revision (which is ALL 1.0.7's call site ever did, since that extra information didn't exist yet).
//   NEW discipline: CompressionState.IsInFlightUnacked's own real comparison — suppress only if the newest
//   in-flight item's revision equals the live one; otherwise send again (a superseding send).
// ============================================================================================================
static bool RunHzSimulation(Assembly asm)
{
    Console.WriteLine("=== 1.0.8 stutter fix: one continuously-changing ZDO, OLD guard-7 discipline vs NEW (real InFlightTracker, real DLL) ===");
    Console.WriteLine("Send round every 66ms (ZDOMan.SendZDOToPeers2's ~15Hz per-peer round-robin on a 30fps server, SERVER:76884).");
    Console.WriteLine();

    var openType = asm.GetType("NoodlesMcDoodles.Compression.InFlightTracker`2", true)!;
    var closedType = openType.MakeGenericType(typeof(long), typeof(int));
    var trackM = closedType.GetMethod("Track")!;
    var isInFlightM = closedType.GetMethod("IsInFlightUnacked")!;
    var commitM = closedType.GetMethod("Commit")!;

    const float roundMs = 66f;
    const float simMs = 10_000f;             // 10s of simulated traffic per (rule, RTT) combination
    const float noTimeout = 1_000_000f;      // effectively disabled — the timeout path has its own scenarios in Part 2
    const long key = 42L;
    float roundRateHz = 1000f / roundMs;

    (int sends, double hz, double maxGapMs, double minGapMs) Simulate(bool newRule, float rtt)
    {
        var tracker = Activator.CreateInstance(closedType)!;
        var sendTimes = new List<float>();
        var pendingAcks = new List<(float dueAt, int seq)>();
        int nextSeq = 1, liveRevision = 0;

        for (float t = 0; t <= simMs; t += roundMs)
        {
            // Acks are asynchronous RPC arrivals, applied as soon as they land — process any due before this
            // round's own send decision (matches CompressionState.CommitInFlight's own release-on-ack timing).
            for (int i = pendingAcks.Count - 1; i >= 0; i--)
            {
                if (pendingAcks[i].dueAt <= t)
                {
                    commitM.Invoke(tracker, new object[] { pendingAcks[i].seq, null });
                    pendingAcks.RemoveAt(i);
                }
            }

            liveRevision++; // changes every single round — the worst case the bug report describes

            var args = new object[] { key, t, noTimeout, null, null };
            bool inFlight = (bool)isInFlightM.Invoke(tracker, args)!;
            bool send;
            if (!inFlight) send = true;                              // nothing withheld — vanilla's own "not known -> true"
            else if (!newRule) send = false;                          // OLD: any in-flight batch suppresses, full stop
            else send = liveRevision > (int)args[3]!;                 // NEW: only an UNCHANGED revision suppresses

            if (send)
            {
                int seq = nextSeq++;
                trackM.Invoke(tracker, new object[] { seq, new long[] { key }, new int[] { liveRevision }, t });
                pendingAcks.Add((t + rtt, seq));
                sendTimes.Add(t);
            }
        }

        double hz = sendTimes.Count / (simMs / 1000.0);
        double maxGap = 0, minGap = sendTimes.Count > 1 ? double.MaxValue : 0;
        for (int i = 1; i < sendTimes.Count; i++)
        {
            double gap = sendTimes[i] - sendTimes[i - 1];
            if (gap > maxGap) maxGap = gap;
            if (gap < minGap) minGap = gap;
        }
        return (sendTimes.Count, hz, maxGap, minGap == double.MaxValue ? 0 : minGap);
    }

    bool ok = true;
    Console.WriteLine($"{"RTT ms",7} {"rule",5} {"sends",6} {"Hz",7} {"maxGapMs",9} {"minGapMs",9}");
    float[] rtts = { 50f, 100f, 200f };
    foreach (float rtt in rtts)
    {
        var oldR = Simulate(newRule: false, rtt);
        var newR = Simulate(newRule: true, rtt);
        Console.WriteLine($"{rtt,7:F0} {"OLD",5} {oldR.sends,6} {oldR.hz,7:F2} {oldR.maxGapMs,9:F1} {oldR.minGapMs,9:F1}");
        Console.WriteLine($"{rtt,7:F0} {"NEW",5} {newR.sends,6} {newR.hz,7:F2} {newR.maxGapMs,9:F1} {newR.minGapMs,9:F1}");

        bool neverWorse = newR.hz >= oldR.hz - 0.01;
        bool recoversRoundRate = newR.hz >= roundRateHz - 0.5;
        Console.WriteLine($"  [RTT={rtt:F0}ms] NEW never worse than OLD: {(neverWorse ? "PASS" : "FAIL")} ({newR.hz:F2} >= {oldR.hz:F2}); " +
            $"NEW recovers ~full round rate (>={roundRateHz - 0.5:F2}Hz): {(recoversRoundRate ? "PASS" : "FAIL")}");
        ok &= neverWorse & recoversRoundRate;

        if (rtt > roundMs)
        {
            bool oldDegrades = oldR.hz < roundRateHz - 0.5;
            Console.WriteLine($"  [RTT={rtt:F0}ms > round interval] OLD visibly degrades (<{roundRateHz - 0.5:F2}Hz), reproducing the reported stutter: {(oldDegrades ? "PASS" : "FAIL")}");
            ok &= oldDegrades;
        }
    }

    Console.WriteLine();
    Console.WriteLine("(This models ONLY guard 7's own suppression window, i.e. IsInFlightUnacked's contribution to send");
    Console.WriteLine("cadence for a single always-changing ZDO — not budget/Q3 throttling, packet loss, or other traffic");
    Console.WriteLine("competing for the same round's byte budget, none of which this class knows about or controls.)");

    return ok;
}


// ============================================================================================================
// Part 4 — 1.0.9 compression follow-up: CompressionState.IsSupersedingRevision, the two-field supersede
// comparison IsInFlightUnacked uses, extracted into a pure function of four plain numbers (no ZDO/Unity
// dependency) specifically so it can be driven here directly, closing the gap Part 2/3's own ZDO-dependent
// scenarios couldn't reach on their own.
// ============================================================================================================
static bool RunSupersedeComparatorTests(Assembly asm)
{
    Console.WriteLine("=== CompressionState.IsSupersedingRevision (real DLL, pure function of 4 numbers, no ZDO/Unity needed) ===");

    var stateType = asm.GetType("NoodlesMcDoodles.Compression.CompressionState", true)!;
    var method = stateType.GetMethod("IsSupersedingRevision", BindingFlags.NonPublic | BindingFlags.Static)!;

    bool ok = true;
    int caseNum = 0;

    bool Check(string name, bool cond)
    {
        caseNum++;
        Console.WriteLine($"  [{caseNum}] {name}: {(cond ? "PASS" : "FAIL")}");
        return cond;
    }

    bool Call(ushort liveOwnerRevision, ushort snapshotOwnerRevision, uint liveDataRevision, uint snapshotDataRevision) =>
        (bool)method.Invoke(null, new object[] { liveOwnerRevision, snapshotOwnerRevision, liveDataRevision, snapshotDataRevision })!;

    ok &= Check("unchanged: identical owner+data revisions -> not superseding",
        !Call(5, 5, 100u, 100u));
    ok &= Check("data-only advance: owner unchanged, data revision ahead -> superseding",
        Call(5, 5, 101u, 100u));
    ok &= Check("owner-only advance: owner revision ahead, data unchanged -> superseding " +
        "(mirrors vanilla ShouldSend: owner ahead always supersedes, regardless of data)",
        Call(6, 5, 100u, 100u));
    ok &= Check("owner ahead, data BEHIND the snapshot: owner alone is still enough -> superseding",
        Call(6, 5, 50u, 100u));
    ok &= Check("owner behind the snapshot, data ahead: data alone is still enough -> superseding",
        Call(4, 5, 101u, 100u));
    ok &= Check("both fields behind the snapshot (a live ZDO's own revisions can never actually regress, " +
        "but the pure function is still a plain comparison for any input) -> not superseding",
        !Call(4, 5, 99u, 100u));
    ok &= Check("both fields simultaneously ahead -> superseding",
        Call(9, 5, 200u, 100u));
    ok &= Check("ushort owner-revision boundary: max value vs one below, data unchanged -> superseding",
        Call(ushort.MaxValue, (ushort)(ushort.MaxValue - 1), 0u, 0u));
    ok &= Check("uint data-revision boundary: max value vs one below, owner unchanged -> superseding",
        Call(0, 0, uint.MaxValue, uint.MaxValue - 1));
    ok &= Check("zero/zero baseline (fresh ZDO, fresh snapshot) -> not superseding",
        !Call(0, 0, 0u, 0u));

    return ok;
}
