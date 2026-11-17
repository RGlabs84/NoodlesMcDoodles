# Changelog

## [1.1.0] - Simpler settings, quieter logs

- **Changed: a much shorter config (schema v2).** `wubarrk.noodlesmcdoodles.cfg` now holds 20 plain-English
  settings in five sections: General, Network, Server World, Client and Safety. The ~110 expert settings moved to
  `wubarrk.noodlesmcdoodles.advanced.cfg` under their old names. Rates are now in KiB/s and MiB/s instead of bytes.
  See the README for the full list.
- **New: automatic config upgrade.** On first start, a 1.0.x config is converted by itself. Settings you changed
  are kept in their new place, everything you left alone picks up 1.1.0's defaults, the old file is saved as
  `wubarrk.noodlesmcdoodles.cfg.v1-backup-<date>`, and one log line lists what was kept. Future versions upgrade
  the same way.
- **New: `LogLevel` (Quiet / Normal / Verbose).** At the default `Normal`, a day on a busy server went from about
  2,600 log lines to about 125: one startup line instead of ~30, and a short `status:` line every 10 minutes while
  players are on. Everything left out still goes to `BepInEx/NoodlesMcDoodles/detail.log`.
- **Changed: the `[NoodlesMcDoodles] telemetry` line** is no longer in `LogOutput.log` at `Normal`. It's in
  `detail.log` every interval, or back in the main log with `LogLevel = Verbose`. Its default interval is now
  600 s (was 60 s).
- **Fixed: crash guards switched off next to LetItGrow.** LetItGrow already makes the harvest code safe on a
  dedicated server. When it got there first, this mod logged an error on every boot and switched off its five
  other unrelated crash guards (cooking stations, Valkyrie, Leviathan, music volume, ship sails). It now sees the
  code is already safe and leaves those guards on.
- **Compatibility:** the handshake is unchanged, so 1.0.x and 1.1.0 players and servers still work together.

## [1.0.13] - Checked against Valheim 1.0.21

- **Compatibility**: rebuilt and checked against Valheim 1.0.21 (network version 41). Valheim 1.0.21 changes the
  network version, so the server and its players need the same game build. Nothing in this mod needed a code change.
  The handshake is unchanged, so 1.0.10 to 1.0.12 players and servers still work with this one.
- **Changed**: website link moved to https://www.rossgilsonlabs.dev.
- **Changed**: now depends on BepInExPack Valheim 5.4.2360.

## [1.0.12] - Harvested items no longer trip the safety net

- **Fixed**: harvested crops, berries, mushrooms and honey were mistaken for building pieces by 1.0.11's hand-back
  pass. When another mod deliberately holds such items on the server (an item-tracking mod, say), a player
  harvesting a patch, dozens at once, had every hand-back refused, and that burst tripped this mod's own safety net,
  which drops the server to the cautious `SharedFar` mode until a restart or `noodles clear`. Ground items are
  left to the item rules again, as in 1.0.10.
- **Changed**: a hand-back another mod refuses on purpose (a held door, a held item) no longer counts toward the
  safety net's veto limit. It is counted on its own as `ownership.o11.releaseRefusedByHolder`, with samples in the
  detail log.

## [1.0.11] - Logs, stumps and new builds stop vanishing

- **Fixed**: trees and falling logs near a player stay with that player's own game again, as in vanilla. The server
  used to run them, so a log it broke on impact, or a stump a falling log hit, could disappear from the player's
  view, and a falling log could change hands mid-fall.
- **Fixed**: a piece you placed this session stays with you while you're nearby. A brand-new portal was taken over
  by the server moments after its builder stepped away, and then destroyed.
- **Fixed**: a tombstone near a player stays with the players' games again, as in vanilla. Another server mod could
  hand new tombstones to the server, whose copy only lifts a sinking tombstone back up where its own ground has
  loaded.
- **Fixed**: the server no longer applies creature, fire or impact damage to builds and tames that are outside every
  player's own simulated area. Before, it kept doing so up to about 160 m from a player, further than any player's
  game runs.
- **Diagnostics**: a player-built object the server destroys with nobody near now logs a warning naming what destroyed
  it (`ownership.playerBuiltDestroyWhileServerHeld`), and a hit the server's copy of an object can't take names that
  object (`ownership.droppedRoutedRpc`).
- **Compatibility**: the handshake is unchanged, so 1.0.10 and 1.0.11/1.0.12 players and servers work together.

## [1.0.10] - Objects stop changing hands

- **Changed**: the server's claim pass now does a little work every frame (about half a millisecond) instead of
  one big pass a second, and it only changes who runs an object when something actually needs to change. The old
  pass made thousands of ownership calls a second that were all refused, in one frame. In a 25-minute test those
  are gone, and objects the server claimed back within a minute dropped from 42,484 to 291: creatures, doors and
  building pieces stop bouncing between the server and players.
- **Fixed**: the server now lets go of an object the moment it unloads it, instead of a second or two later. In
  that gap a creature could stand frozen and a hit on it could be lost.
- **Fixed**: the server no longer tries claims its own rules refuse (mostly pickables near players).
- **Fixed**: the F6 network panel no longer shows on the main menu or loading screen; it appears once you're in
  the world.
- **Settings**: new `[OwnershipAuthority] ClaimScanBudgetMs` (default 0.5). `MaxZonesClaimedPerCycle` is gone;
  the old line in an existing config is ignored.
- **Telemetry**: `ownership.o6.claimSliceMsMax` and `ownership.o6.scanStepMsMax` (the claim pass's slowest frame
  and single step), `ownership.o6.scanObjectsPerSec`, `ownership.o6.setOwnerCallsPerSec`,
  `ownership.o6.strayOrCandidacyLostReleased` and `ownership.unloadReleased`.

## [1.0.9] - Less stutter when players reach new areas

- **Fixed**: the server stuttered when a player walked into an area it hadn't loaded yet. It loaded that area's
  objects 100 or more at a time and could build several new areas' terrain in the same frame. It now spends at
  most 8 ms at a time loading objects and builds one new area at a time. In a five-minute test walk around a dense
  Ashlands base, frames over 100 ms dropped from 10 to 2.
- **Changed**: objects closest to a player now load first. Before, the server loaded the ones nearest the middle
  of the world first.
- **Fixed**: the server could take charge of an object before it had finished loading it. Until the load finished,
  a creature there stood still and a hit on a tree, rock or building piece could be lost. The server now takes an
  object only once it has loaded it. In a freshly entered area, the player's own game runs things a little longer
  first, as in vanilla.
- **Fixed**: when compression turned off for a player partway through a session, the updates still tracked for
  that player are now cleared within seconds instead of at logout. Nothing was lost before; this is bookkeeping.
- **New settings** (`[ZoneSim]`): `CreateBudgetMs` (default 8), `MinCreatedPerTick` (default 2) and
  `ZoneBuildBudgetMs` (default 6). The default for `MaxZoneBuildsPerTick` is now 1 (was 4). **An existing config
  keeps its saved value**, so on a server that already ran NoodlesMcDoodles, set `MaxZoneBuildsPerTick = 1` in
  `wubarrk.noodlesmcdoodles.cfg`.
- **Telemetry**: `zonesim.createBacklog` (objects still waiting to load), `zonesim.createBudgetLimitedTicksTotal`,
  `zonesim.zoneBuildsDeferredTotal`, `zonesim.lastZoneBuildMs` and `ownership.o6.claimDeferredNotCreated` (claims
  put off until the object has loaded).

## [1.0.8] - Smoother creatures, held zones stay held

- **Fixed**: creatures and other players could look choppy to a modded player. With compression on, a newer update
  for an object was held back until the player's game had confirmed the previous one, so a moving creature updated
  about 5 to 7 times a second instead of about 15. Newer updates now go out right away; an update the player already
  has is still never sent twice.
- **Fixed**: when the server claimed a zone, it also let go of everything it held two zones away. The next claims
  took those objects back, so creatures, doors, trees and building pieces near a player kept changing hands between
  the server and that player. Objects in a zone the server holds now stay with the server until it releases the zone.
- **Fixed**: the server claims only the zone it is working on, not free objects at the edges of the zones around it,
  so it no longer owns objects in zones it doesn't hold.
- **Fixed**: a building piece a player owns no longer stops the server from holding the rest of that zone while the
  player is nearby. The piece stays with the player, as before.
- **Telemetry**: `ownership.o6.reclaimedWithinMinute` counts objects the server had to claim again within a minute;
  `ownership.o6.selfReleaseKept` counts releases it now skips; `compression.supersedingSends` and
  `compression.heldBackSameRevision` show newer updates sent early and repeats held back.

## [1.0.6] - Player counts after a leave

- **Fixed**: the player counts in the telemetry line no longer include players who have already left. The
  mod's per-player records are now removed when a player disconnects, and any record left over for a player who
  is no longer connected is cleared within a frame.

## [1.0.5] - Server stability fixes

- **Fixed**: a player changing sail near the server's copy of their ship made the server throw an error on every
  frame until the sail stopped moving. The server now skips the sail sound it can't play anyway.
- **New safety net**: if any object throws an error while the server updates it, every other object keeps
  updating. Before this, a single error froze every creature's AI and movement on the server for that frame.
  The first error of each kind is still logged in full; repeats are counted and summarised once a minute.
  Setting: `Features.UpdateIsolation.Active` (on by default).
- **Quieter log**: the routine "wear skipped in an empty held zone" lines moved from the main log to the detail log.

## [1.0.4] - F6 panel layout fix

- **Fixed**: values in the F6 panel's Detailed tab were cut off at the right edge whenever the tab needed its
  scrollbar. Rows now lay out left of the scrollbar and long values wrap instead of being clipped.

## [1.0.3] - ServerSync fixes, download numbers, a better F6 panel

- **Fixed**: on servers running ServerSync-based mods, the adaptive send rate, the send-queue unlock and ZDO
  compression now reach every player. ServerSync can leave its own wrapper around a player's connection after the
  join, and that wrapper even looks like a crossplay connection; the mod now looks through it to the Steam
  connection underneath. Before this, those players got vanilla rates and uncompressed data from the server.
- **F6 panel**: a Download section with the server's own numbers for you (its limit, what it actually sends, its
  queue for you against the allowance), since your download is the server's upload and only the server sets it.
  The panel is resizable, remembers where you put it and how big, and scrolls when its content is taller.
- **Diagnostics**: the log names the object and its owner when another mod refuses an ownership change, and says
  why compression isn't used for a negotiated player.

## [1.0.2] - Handshake fix and a detailed F6 panel

- **Fixed**: a modded client on a server running ServerSync-based mods now negotiates with the server. The server's
  hello could arrive before the rest of the join data and was dropped, so both sides fell back to vanilla mode.
- **F6 panel**: two tabs, Basic and Detailed. Detailed shows your set upload limit against the actual one, your send
  queue against its allowance, and who controls each zone around you, by name.

## [1.0.1] - Simulation distance fix

- **Fixed**: the server no longer lowers every player's simulation distance. 1.0.0 turned vanilla's default
  loaded area (a square around each player) into the smaller "Low" preset (a circle), so objects toward the
  corners of the loaded area appeared later. Vanilla's default is kept now; the ceiling still caps anything larger.

## [1.0.0] - First public release

- **Server-run zones**: the dedicated server runs creatures, tames, buildings, production, trees and rocks, doors
  and terrain, in empty areas and around players alike (`OwnershipAuthority.Mode = FarOrShared`). Players keep
  their own character and what they handle directly: chests, carts, item and armor stands, turrets, signs,
  ground items, ships and nearby harvestables. Objects players keep go to whichever nearby player has the best
  ping to the server instead of whoever arrived first.
- **Empty-area preservation**: decay, despawn, and wild-creature-vs-tame/piece combat are frozen inside the
  server's own empty held zones, so holding territory centrally doesn't quietly cost players their stuff.
  Where players are, decay and combat work exactly as in vanilla.
- **Steadier networking**: adaptive per-connection send rates and a round-trip-aware send queue, so a distant
  player's effective throughput approaches a nearby player's instead of being structurally capped lower by
  vanilla's fixed queue.
- **Optional client-side extras**: installing the mod on a player's own client applies the same upload
  treatment to their own connection (so others see them with fresher updates) and shortens vanilla's two-second
  stale-movement window so a server-simulated object stops sliding and snapping back sooner after a hiccup.
- **F6 status panel** (client): a black-and-gold panel with two tabs. Network shows who runs the world around
  you (Server owns / You own / Other players own / Unowned) and your ping, upload, download, send queue and upload
  limit; Modes shows whether the server runs the mod, the detected setup, and this client's own features. Works
  on modded and vanilla servers alike; read-only, sends nothing.
- **ZDO compression** between two connected peers both running this mod, negotiated automatically per
  connection; a vanilla peer or an incompatible build sees no difference.
- **Gameplay-correctness guards** so server-run zones spawn creatures, run weather, and credit combat correctly,
  and so an in-use ship, cart, container, or production building is never reclaimed out from under the player
  using it.
- **Zero-data-loss safety rails**: automatic stand-down if the mod's own error rate spikes; a mode quarantine
  that drops ownership to the conservative `SharedFar` mode if another mod keeps refusing the server's claims;
  and an admin kill switch (`noodles status | enable | disable`) that reverts every hook to exact vanilla
  behaviour instantly, with no restart and no persistent data at risk.
- **Experimental features, independently switchable**: server-owned shared areas for the case where every
  nearby player is far from the server at once (`O10`, on by default), and an alternate buffered-interpolation
  smoothing mode kept in for further testing (`ClientSmoothing.Active`, off by default).
