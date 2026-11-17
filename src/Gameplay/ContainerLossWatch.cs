using System;
using System.Collections.Generic;
using HarmonyLib;
using NoodlesMcDoodles.Core;
using UnityEngine;

namespace NoodlesMcDoodles.Gameplay
{
    /// <summary>
    /// Real Loss Signal #2 (safety-net spec, WP4) — TELEMETRY ONLY in this build, never a kill-switch trip (see
    /// docs/impl/GAMEPLAY-IMPL.md "Container item-count loss watch" for the full account and the documented
    /// burn-in-before-promotion follow-up the spec itself requires before this may ever call
    /// <c>Core.LossSignals.Report</c>).
    /// <para>
    /// <b>Hook (confirmed against the decompile this pass, not assumed — the spec explicitly required this
    /// before wiring).</b> <c>Container.Save()</c> — <c>private void Save()</c>, SERVER:122171-122177:
    /// <c>m_nview.GetZDO().Set(ZDOVars.s_items, array); m_lastRevision = ...;</c>. Called only from
    /// <c>OnContainerChanged()</c> (SERVER:122163-122169, <c>if (!m_loading &amp;&amp; IsOwner()) Save();</c>),
    /// itself the <c>Inventory.m_onChanged</c> callback wired once in <c>Awake()</c> (SERVER:121771-121773).
    /// </para>
    /// <para>
    /// <b>Why this hook is naturally low-noise (two structural facts that make it safe to watch on every call,
    /// not just a documented hope):</b>
    /// </para>
    /// <para>
    /// 1. <c>Save()</c> only ever runs on whichever process currently OWNS the ZDO (the <c>IsOwner()</c> guard
    /// above). Gated below on <c>ZNet.instance.IsServer()</c>, this postfix therefore only ever samples a
    /// container the SERVER ITSELF currently owns — exactly, and only, the population this watch can meaningfully
    /// speak to.
    /// </para>
    /// <para>
    /// 2. Every vanilla RPC path that lets a player edit a container's contents transfers ownership to that
    /// player FIRST (<c>RPC_RequestOpen</c>/<c>RPC_RequestStack</c>/<c>RPC_RequestTakeAll</c>, SERVER:121977 and
    /// siblings — each only ever grant-and-<c>SetOwner(uid)</c> or refuse; none touches <c>m_inventory</c>
    /// itself). The actual edit/stack/take-all move (<c>RPC_OpenResponse</c>/<c>RPC_StackResponse</c>/
    /// <c>RPC_TakeAllResponse</c>) always runs on the REQUESTING PLAYER's own process, after
    /// <c>ClaimOwnership()</c>/<c>SetOwner</c> already ran — so THAT machine's own <c>Save()</c> fires there,
    /// never on a dedicated server's. A player quick-looting ("take all") or editing a container the server
    /// doesn't currently own therefore never reaches this watch at all on a dedicated server — not suppressed,
    /// structurally never invoked.
    /// </para>
    /// <para>
    /// 3. The one case point 2 does not cover — a listen host, where the server process IS also a connected
    /// player — is exactly why <c>ZDOVars.s_inUse</c> (SERVER:78720, the same field
    /// <c>Ownership/LatencyAwareOwnership.cs</c> already reads via <c>zdo.GetInt(ZDOVars.s_inUse, 0) == 1</c>) is
    /// still checked below, per the spec. <c>SetInUse(true)</c> (SERVER:121988-122006, wired from
    /// <c>InventoryGui.Show</c>/<c>Hide</c>) always runs, in-process, before any GUI-driven edit can reach
    /// <c>m_inventory</c> there — so at the moment any legitimate edit's <c>Save()</c> fires, the ZDO's own
    /// <c>s_inUse</c> field already reads true. This closes the one legitimate path point 2 leaves open, on top
    /// of it, not instead of it.
    /// </para>
    /// <para>
    /// 4. <b><c>OnDestroyed()</c> (SERVER:121818-121828) is a separate, deliberate exclusion.</b> Destroying a
    /// container legitimately drops its full contents as loose <c>ItemDrop</c>s (or into
    /// <c>m_destroyedLootPrefab</c>) — both <c>DropAllItems()</c> overloads end by moving every item out of
    /// <c>m_inventory</c> and calling this SAME <c>Save()</c>, so an ordinary raid/fire/collapse destroying a
    /// loaded, server-held chest would otherwise look identical to a real loss (count drops to 0, GUI never
    /// opened, <c>s_inUse</c> false). <see cref="Patch_Container_OnDestroyed_ForgetBeforeDrop"/> forgets this
    /// ZDOID's last-known count immediately before the drop runs, so the destroy-triggered <c>Save()</c> has
    /// nothing to compare against and is correctly never flagged — the items are redistributed, not lost,
    /// exactly per <c>OnDestroyed</c>'s own vanilla contract.
    /// </para>
    /// <para>
    /// <b>Runtime-authority gate, not just process-type.</b> Registered <see cref="PatchSide.Both"/> (never
    /// <see cref="PatchSide.ServerOnly"/>), gated internally on <c>ZNet.instance.IsServer()</c> — the same rule
    /// this module's <c>ServerCombatStats</c>/<c>ProductionHandoverGuard</c> patches already follow (see
    /// <see cref="GameplayInit"/> remarks): a listen host is a client-BUILD process
    /// (<see cref="Side.IsDedicatedServerProcess"/> false there) that still holds real server authority once
    /// <c>ZNet.instance</c> exists, so <c>ServerOnly</c> would silently lose this watch on every listen-hosted
    /// game. A default-ON flag elsewhere in this mod is, by the binding rule, also "on" inside a connecting
    /// player's own client config — the runtime check is not defence-in-depth here, it is the only thing that
    /// keeps this inert on a plain client at all.
    /// </para>
    /// <para>
    /// <b>No <see cref="FeatureFlag"/>/config toggle of its own</b> — matches <c>Ownership/OwnershipLossWatch.cs</c>'s
    /// own precedent for a pure background safety-net watcher (registered under a descriptive-only
    /// <see cref="PatchRegistry"/> name, never one bound to <see cref="Features.Register"/>): telemetry that
    /// should simply always run once the mod itself is on, not a tunable behaviour.
    /// </para>
    /// </summary>
    internal static class ContainerLossWatch
    {
        private struct Seen
        {
            public int Count;
            public float LastSampleRealtime;
        }

        private static readonly Dictionary<ZDOID, Seen> s_lastSeen = new Dictionary<ZDOID, Seen>();

        private static readonly Counter s_dropWhileClosed =
            Telemetry.GetCounter("gameplay.containerItemCountDropWhileClosed");

        // Memory-growth bound only (same rationale/shape as QuiescenceTracker.MaybeSweep) -- never a correctness
        // concern: an entry this watch never gets to see removed (e.g. a process crash mid-session) is a
        // handful of bytes, and every entry is forgotten on the next kill-switch flush regardless (see Clear()).
        private const float SweepIntervalSeconds = 300f;
        private const float StaleAfterSeconds = 1800f;
        private static float s_lastSweep;

        [HarmonyPatch(typeof(Container), "Save")]
        internal static class Patch_Container_Save_ItemCountWatch
        {
            [HarmonyPostfix]
            internal static void Postfix(Container __instance)
            {
                if (ZNet.instance == null || !ZNet.instance.IsServer()) return; // runtime-authority gate -- see class remarks
                try
                {
                    Sample(__instance);
                }
                catch (Exception e)
                {
                    NoodlesLog.PatchException(nameof(Patch_Container_Save_ItemCountWatch), e);
                }
            }
        }

        /// <summary>See class remarks, point 4 — forgets a container's last-known count immediately before its
        /// own contents are legitimately dropped to the ground, so the destroy-triggered <c>Save()</c> that
        /// follows has nothing to compare against. Runs on the same owner-only path <c>Save()</c>'s own postfix
        /// above does; harmless even for a ZDOID this watch never had a sample for (<c>Dictionary.Remove</c> on a
        /// missing key is a no-op).</summary>
        [HarmonyPatch(typeof(Container), "OnDestroyed")]
        internal static class Patch_Container_OnDestroyed_ForgetBeforeDrop
        {
            [HarmonyPrefix]
            internal static void Prefix(Container __instance)
            {
                if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
                try
                {
                    ZDO zdo = ZdoOf(__instance);
                    if (zdo != null) s_lastSeen.Remove(zdo.m_uid);
                }
                catch (Exception e)
                {
                    NoodlesLog.PatchException(nameof(Patch_Container_OnDestroyed_ForgetBeforeDrop), e);
                }
            }
        }

        private static ZDO ZdoOf(Container container)
        {
            ZNetView nview = container.GetComponent<ZNetView>();
            return nview != null ? nview.GetZDO() : null;
        }

        private static void Sample(Container container)
        {
            ZDO zdo = ZdoOf(container);
            if (zdo == null) return;

            ZDOID id = zdo.m_uid;
            int count = container.GetInventory()?.NrOfItems() ?? 0;
            float now = Time.realtimeSinceStartup;
            MaybeSweep(now);

            if (s_lastSeen.TryGetValue(id, out Seen last) && count < last.Count &&
                zdo.GetInt(ZDOVars.s_inUse, 0) != 1) // same field Ownership/LatencyAwareOwnership.cs reads
            {
                s_dropWhileClosed.Increment();
                NoodlesLog.WarningRateLimited("gameplay.containerItemCountDropWhileClosed",
                    $"container item count dropped ({last.Count} -> {count}) on a server-owned container that " +
                    $"was never opened (zdo={id}) -- telemetry only, not a kill-switch trip; see " +
                    "docs/impl/GAMEPLAY-IMPL.md \"Container item-count loss watch\".",
                    firstN: 5, windowSeconds: 60f);
            }

            s_lastSeen[id] = new Seen { Count = count, LastSampleRealtime = now };
        }

        private static void MaybeSweep(float now)
        {
            if (now - s_lastSweep < SweepIntervalSeconds) return;
            s_lastSweep = now;

            List<ZDOID> stale = null;
            foreach (var kv in s_lastSeen)
            {
                if (now - kv.Value.LastSampleRealtime > StaleAfterSeconds) (stale ??= new List<ZDOID>()).Add(kv.Key);
            }
            if (stale == null) return;
            foreach (ZDOID id in stale) s_lastSeen.Remove(id);
        }

        /// <summary>Kill-switch flush (see <see cref="GameplayInit"/>) — pure bookkeeping, no held state to
        /// release; starts fresh ("no prior sample") the moment the mod comes back on, same rationale as
        /// <see cref="QuiescenceTracker.Clear"/>.</summary>
        internal static void Clear() => s_lastSeen.Clear();
    }
}
