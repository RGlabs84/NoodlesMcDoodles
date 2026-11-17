using UnityEngine;
using UnityEngine.Rendering;

namespace NoodlesMcDoodles.Core
{
    /// <summary>
    /// "Which process am I in" — dedicated server vs. client.
    ///
    /// WHY THIS EXISTS AND ISN'T JUST "ZNet.instance.IsDedicated()": <c>ZNet.IsDedicated()</c> is the real,
    /// ground-truth answer (hardcoded <c>return true;</c> on the SERVER assembly, <c>return false;</c> on the
    /// CLIENT assembly — SERVER:81194-81197 / CLIENT:81380-81383, confirmed against both decompiles), but
    /// <c>ZNet.instance</c> is null until a world/menu scene constructs a <c>ZNet</c> component, which happens
    /// long after BepInEx calls this plugin's own <c>Awake()</c>. <see cref="PatchRegistry"/> needs a side
    /// answer AT patch-application time (plugin Awake, so a client-only patch class is never even offered to
    /// <c>CreateClassProcessor</c> on the dedicated server and vice versa) — before any <c>ZNet</c> exists.
    ///
    /// <see cref="IsDedicatedServerProcess"/> answers that with <c>SystemInfo.graphicsDeviceType ==
    /// GraphicsDeviceType.Null</c>, which is available the moment Unity's engine has booted (well before any
    /// BepInEx plugin loads) and needs no game type at all. This is not a guess for this project: it's the
    /// documented, live-build-verified technique for exactly this game
    /// (`libs-Tools/VALHEIM-DEDICATED-SERVER-FACTS.md`, "Headless detection" — "SystemInfo.graphicsDeviceType
    /// == GraphicsDeviceType.Null — compile-independent ... verified working on the real server binary"), and
    /// it matches how Valheim's own dedicated server is actually launched (`-batchmode -nographics`, see
    /// `libs-Tools/DEDICATED-SERVER-TESTBED/run-linux-1.0-server.sh`), which forces a null graphics device.
    ///
    /// Once <c>ZNet.instance</c> does exist, <see cref="ConfirmAgainstZNet"/> cross-checks this heuristic
    /// against the real <c>IsDedicated()</c> answer and loudly warns (never silently trusts the heuristic
    /// over ground truth) if they ever disagree, so a future engine/launch-flag change that breaks the
    /// heuristic is visible in the log rather than silently mis-routing every side-restricted patch.
    /// </summary>
    internal static class Side
    {
        private static bool? s_isDedicatedServerProcess;

        /// <summary>True once, computed on first access and cached — Application.platform/SystemInfo don't change at runtime.</summary>
        public static bool IsDedicatedServerProcess
        {
            get
            {
                if (!s_isDedicatedServerProcess.HasValue)
                {
                    s_isDedicatedServerProcess = SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null;
                }
                return s_isDedicatedServerProcess.Value;
            }
        }

        /// <summary>
        /// Called once from a postfix on <c>ZNet.Awake</c> (the earliest point <c>ZNet.instance</c> is set,
        /// SERVER:79204/CLIENT:79346, <c>m_instance = this;</c> is the very first statement). Not a gate on
        /// anything by itself — patch application already happened at plugin Awake using the heuristic above —
        /// this only proves the heuristic was right, loudly, if it wasn't.
        /// </summary>
        public static void ConfirmAgainstZNet(bool znetIsDedicated)
        {
            if (znetIsDedicated != IsDedicatedServerProcess)
            {
                NoodlesLog.Error(
                    $"side-detection heuristic disagreed with ZNet.IsDedicated() (heuristic={IsDedicatedServerProcess}, " +
                    $"ZNet={znetIsDedicated}) — side-restricted patch classes may have been applied to the wrong " +
                    "process. This should never happen on a normal Steam client or `-batchmode -nographics` " +
                    "dedicated server; if this fires, treat every side-gated feature's behaviour as suspect.");
            }
        }
    }
}
