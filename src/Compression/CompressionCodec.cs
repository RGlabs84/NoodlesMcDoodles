using System.IO;
using System.IO.Compression;
using NoodlesMcDoodles.Transport;

namespace NoodlesMcDoodles.Compression
{
    /// <summary>
    /// GZip compress/decompress, structurally identical to <c>Utils.Compress</c>/<c>Utils.Decompress</c>
    /// (UTILS:12483-12496, confirmed: <c>GZipStream</c> over a <c>MemoryStream</c>) but with a CALLER-CHOSEN
    /// <see cref="CompressionLevel"/> instead of vanilla's hardcoded <c>CompressionLevel.Fastest</c>.
    ///
    /// Why not just call <c>ZPackage.GetCompressed()</c>/<c>Utils.Compress</c> (the blueprint's original
    /// sketch): those hardcode Fastest with no way to tune it, and the coordinator's "not overkill" rule asks
    /// for the level to be a measured choice (see <c>tools/zip-sim</c>), not an inherited default. This is
    /// still the SAME BCL class the game process already loads (<c>System.IO.Compression.GZipStream</c>) —
    /// zero new dependency, and decompression is level-agnostic (gzip's decoder doesn't need to know what
    /// level encoded the stream), so the receive side keeps calling vanilla's own <c>Utils.Decompress</c>
    /// completely unchanged; only the SEND side differs from the blueprint's original sketch.
    /// </summary>
    internal static class CompressionCodec
    {
        /// <summary>
        /// RED-TEAM FIX (DATA-LOSS REVIEW, blocker) — hard ceiling on a single call's decompressed OUTPUT size.
        /// <see cref="Decompress"/> previously had no bound at all: the RPC that reaches it
        /// (<c>CompressedRpc.RPC_ZDODataCompressed</c>) is registered the instant ANY non-PlayFab peer joins
        /// (<c>Patch_ZDOMan_AddPeer</c>), and a single-member GZip/deflate payload well under Steam's own
        /// ~512 KiB reliable-message ceiling (<see cref="SharedBudget.AbsoluteSteamCeilingBytes"/>) can be
        /// crafted — LZ77 back-references into its own already-produced output, a standard "deflate bomb" — to
        /// expand to hundreds of MB in one read loop, on Valheim's single main thread, freezing world sim and
        /// every other mod's RPCs for every connected peer, or OOM-crashing the whole process. Vanilla never
        /// decompresses anything on the code path this mod's new channel replaces, so it has zero exposure to
        /// this; this channel defaults ON for the first live version, so it does.
        ///
        /// A compliant sender's own pre-compression package is already bounded near
        /// <see cref="SharedBudget.AbsoluteSteamCeilingBytes"/> (Patch_ZDOMan_SendZDOs.cs's own remarks: the
        /// ordinary multi-item accumulation path is bounded far tighter, by Q3's 131072-byte hard clamp, but
        /// Q12Guard's single-oversized-item path — one legitimately huge item such as a heavily-edited
        /// TerrainComp — is bounded only by the 512 KiB Steam ceiling, independently of Q3's clamp), so a small
        /// fixed multiple of that same constant is a generous cap for any real batch while staying nowhere near
        /// enough to freeze or OOM anything. Derived from SharedBudget's own constant (not a second, driftable
        /// literal) so it automatically tracks that value if it ever changes.
        /// </summary>
        private const long MaxDecompressedBytes = SharedBudget.AbsoluteSteamCeilingBytes * 4L; // 2 MiB

        public static byte[] Compress(byte[] input, CompressionLevel level)
        {
            using var output = new MemoryStream();
            using (var gzip = new GZipStream(output, level))
            {
                gzip.Write(input, 0, input.Length);
            }
            return output.ToArray();
        }

        /// <summary>
        /// Matches <c>Utils.Decompress</c> (UTILS:12489-12496) exactly — kept as our own copy rather than a
        /// call into <c>Utils</c> so this class has one self-contained round-trip pair usable identically from
        /// production code and from <c>tools/zip-sim</c> (which cannot load the game's Unity-dependent
        /// assemblies — see that tool's own remarks).
        /// </summary>
        public static byte[] Decompress(byte[] input)
        {
            using var input_ = new MemoryStream(input);
            using var gzip = new GZipStream(input_, CompressionMode.Decompress);
            using var output = new MemoryStream();

            // Bounded read loop instead of the unbounded gzip.CopyTo(output) this used to be (see
            // MaxDecompressedBytes remarks) — throws the moment total decompressed output crosses the ceiling,
            // rather than letting a crafted stream keep inflating. The caller (CompressedRpc.RPC_ZDODataCompressed)
            // already wraps its whole decompress+apply call in a catch(Exception) that treats any decompress/
            // parse failure as a transient nack, so no other change is needed to handle this exception safely.
            byte[] buffer = new byte[8192];
            long total = 0;
            int read;
            while ((read = gzip.Read(buffer, 0, buffer.Length)) > 0)
            {
                total += read;
                if (total > MaxDecompressedBytes)
                {
                    throw new InvalidDataException(
                        $"Decompressed payload exceeded {MaxDecompressedBytes} bytes — refusing (decompression-bomb guard).");
                }
                output.Write(buffer, 0, read);
            }
            return output.ToArray();
        }
    }
}
