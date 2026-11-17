using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using NoodlesMcDoodles.Core;
using UnityEngine;

namespace NoodlesMcDoodles.ZoneSim
{
    /// <summary>
    /// Cached reflection access to <c>ZoneSystem</c>'s private members this module needs but that have no
    /// public accessor: the <c>m_zones</c> dictionary (private, <c>SERVER:113154</c> / <c>CLIENT:113965</c> —
    /// <c>private readonly Dictionary&lt;Vector2s, ZoneData&gt; m_zones</c>, confirmed identical both builds
    /// this session), the private nested <c>ZoneData</c> class (<c>SERVER:112639</c> / <c>CLIENT:113450</c> —
    /// its two fields, <c>m_root</c>/<c>m_ttl</c>, are themselves public; only the enclosing class is private),
    /// and <c>PokeLocalZone</c>/<c>IsZoneGenerated</c> (private instance methods, <c>SERVER:113896</c>/
    /// <c>115727</c>, <c>CLIENT:114707</c>/<c>116538</c>).
    ///
    /// Resolved once via <see cref="AccessTools"/> rather than direct field/type syntax against the build-time
    /// publicized reference assembly — matching every other module's own established style (see e.g.
    /// <c>Transport/Timeouts.cs</c>'s remarks and <c>Core/ZNetHelpers.cs</c>'s doc comment): the publicizer
    /// only changes what the COMPILER accepts; the actual DLL loaded at runtime (the real game build) still has
    /// these members private, so <see cref="AccessTools"/> — not `instance.m_zones` — is what a modded server
    /// process actually resolves successfully against.
    ///
    /// Deliberately exposes <see cref="ContainsZone"/> (<c>m_zones.ContainsKey</c>), never wraps the PUBLIC
    /// <c>IsZoneLoaded</c> — docs/design/04's own explicit guard, citing research/04's "OOM-lesson
    /// <c>IsZoneLoaded</c> vs <c>m_zones.ContainsKey</c> distinction": <c>IsZoneLoaded</c> additionally checks
    /// <c>!m_loadingObjectsInZones.ContainsKey</c> (confirmed <c>SERVER:113921-113927</c>/
    /// <c>CLIENT:114732-114738</c>), an unrelated, stricter condition that a prior mod's design hit as a stall
    /// trap. "Does a real terrain root exist for this zone" — what Hook 2's object-feed readiness and Hook 7's
    /// snap readiness both actually need — is exactly <c>m_zones.ContainsKey</c>, nothing more.
    /// </summary>
    internal static class ZoneSystemAccess
    {
        private static readonly FieldInfo s_zonesField = AccessTools.Field(typeof(ZoneSystem), "m_zones");
        private static readonly System.Type s_zoneDataType = AccessTools.Inner(typeof(ZoneSystem), "ZoneData");
        private static readonly FieldInfo s_zoneDataRoot = s_zoneDataType != null ? AccessTools.Field(s_zoneDataType, "m_root") : null;
        private static readonly FieldInfo s_zoneDataTtl = s_zoneDataType != null ? AccessTools.Field(s_zoneDataType, "m_ttl") : null;
        private static readonly MethodInfo s_pokeLocalZone = AccessTools.Method(typeof(ZoneSystem), "PokeLocalZone");
        private static readonly MethodInfo s_isZoneGenerated = AccessTools.Method(typeof(ZoneSystem), "IsZoneGenerated", new[] { typeof(Vector2s) });

        /// <summary>
        /// True only if every reflection target above resolved — checked once at module Init (mirroring
        /// <c>ZNetHelpers.ValidateOrDisableDependents</c>'s "fail loud at startup, not silently per call"
        /// convention) so a future game build that renames one of these fails this module OFF cleanly at boot
        /// rather than throwing deep inside a hot per-tick loop the first time a peer connects.
        /// </summary>
        public static bool IsReady =>
            s_zonesField != null && s_zoneDataType != null && s_zoneDataRoot != null && s_zoneDataTtl != null &&
            s_pokeLocalZone != null && s_isZoneGenerated != null;

        private static IDictionary Zones(ZoneSystem instance) => (IDictionary)s_zonesField.GetValue(instance);

        public static bool ContainsZone(ZoneSystem instance, Vector2s zone) => Zones(instance).Contains(zone);

        public static int ZoneCount(ZoneSystem instance) => Zones(instance).Count;

        /// <summary>(zone key, boxed ZoneData instance) pairs, snapshotted so Hook 4's backlog-drain pass can
        /// collect-then-mutate without enumerating the live dictionary while removing from it (vanilla's own
        /// <c>UpdateTTL</c> avoids this the same way, by <c>break</c>ing after its first removal — see
        /// <see cref="ZoneTtl"/> for why this module needs to remove more than one entry per call).</summary>
        public static List<(Vector2s zone, object data)> SnapshotZoneEntries(ZoneSystem instance)
        {
            var list = new List<(Vector2s, object)>();
            foreach (DictionaryEntry entry in Zones(instance)) list.Add(((Vector2s)entry.Key, entry.Value));
            return list;
        }

        public static void RemoveZone(ZoneSystem instance, Vector2s zone) => Zones(instance).Remove(zone);

        public static GameObject ZoneRoot(object zoneData) => (GameObject)s_zoneDataRoot.GetValue(zoneData);
        public static float ZoneTtl(object zoneData) => (float)s_zoneDataTtl.GetValue(zoneData);
        public static void AddZoneTtl(object zoneData, float dt) => s_zoneDataTtl.SetValue(zoneData, ZoneTtl(zoneData) + dt);

        /// <summary>
        /// <c>ZoneSystem.PokeLocalZone(Vector2s)</c> — resets an already-real zone's TTL for free, or (server,
        /// <c>IsZoneGenerated</c> true) builds a real <c>Heightmap</c>+<c>MeshCollider</c> via
        /// <c>SpawnMode.Full</c>. Never call this on a zone that fails <see cref="IsZoneGenerated"/> first —
        /// that would silently trigger first-time world generation outside vanilla's own capped ghost-zone path
        /// (Hook 1's own guard; see <c>PokeRingDriver</c>).
        /// </summary>
        public static bool PokeLocalZone(ZoneSystem instance, Vector2s zone) => (bool)s_pokeLocalZone.Invoke(instance, new object[] { zone });

        public static bool IsZoneGenerated(ZoneSystem instance, Vector2s zone) => (bool)s_isZoneGenerated.Invoke(instance, new object[] { zone });
    }
}
