<!-- README schema v2 -->
# 🍜 NoodlesMcDoodles

![NOODLESMCDOODLES](https://img.shields.io/badge/NOODLESMCDOODLES-A_FAST_LINK_SHOULD_FEEL_FAST-c9a227?style=for-the-badge&labelColor=1b1b1b)
![VERSION](https://img.shields.io/badge/VERSION-1.1.0-8b1e1e?style=for-the-badge&labelColor=1b1b1b)
![SERVER SIDE](https://img.shields.io/badge/SERVER--SIDE-CLIENTS_OPTIONAL-1f4e79?style=for-the-badge&labelColor=1b1b1b)

> *The ropes between the longhouses were tangled, and every message waited on the slowest runner. Untangle them, and each runner keeps his own pace.*

### Your connection is fast. Now the game is too.

In vanilla Valheim, the area you stand in is run by whichever player loaded it first, and every connection is held
to the same fixed send rate. Your view of the world is only as good as the slowest link running it.

NoodlesMcDoodles hands that job to your dedicated server and lets every player's connection run as fast as it can
take. Install it on the server and you're done: players without the mod get the benefit too.

---

![NEW IN 1.1.0](https://img.shields.io/badge/NEW_IN_1.1.0-SIMPLE_SETTINGS_%C2%B7_QUIET_LOGS-8b1e1e?style=for-the-badge&labelColor=1b1b1b)

**Twenty plain-English settings, and a log you can actually read.**

| You | It |
|---|---|
| **open the config** | 20 settings in five sections: General, Network, Server World, Client, Safety. The expert tuning lives in its own file. |
| **update from an older version** | your old config is upgraded by itself: what you changed is kept, the old file is backed up, and one log line says what was kept. |
| **read the server log** | one startup line and a short `status:` line every 10 minutes, instead of thousands of lines a day. |

🔹 **`LogLevel`**: Quiet, Normal or Verbose · 🔹 **Works alongside LetItGrow** without switching off any of its own crash guards

---

![BEFORE YOU INSTALL](https://img.shields.io/badge/BEFORE_YOU_INSTALL-READ_THIS-b8860b?style=for-the-badge&labelColor=1b1b1b)

| | If… | Then… |
|---|---|---|
| 🖥️ | you run a dedicated server | install it there. That's where the real work happens. |
| 👥 | your players don't install it | that's fine. Everything server-side still applies to them. |
| 🔄 | you're updating from an older version | just swap the DLL. Your config is upgraded on first start and the old file is kept as a backup. |
| 🧯 | something ever looks wrong | type `noodles disable` in the console. Everything is back to vanilla at once, no restart. |

---

![I](https://img.shields.io/badge/I-THE_SERVER_RUNS_THE_WORLD-c9a227?style=for-the-badge&labelColor=1b1b1b)

**No more lag from the player who happened to get there first.**

| | |
|---|---|
| 🔹 **Server-run areas** | Creatures, builds, smelters, kilns, doors and terrain are run by the dedicated server. |
| 🔹 **Players keep what's theirs** | Your character, chests, carts, ships and items on the ground stay with the players handling them. |
| 🔹 **Best ping wins** | When players share an area, the objects players keep go to whoever has the best ping, not whoever arrived first. |
| 🔹 **Never mid-use** | A ship, cart, chest or smelter someone is using is never taken from them. |

<details>
<summary>📖 <b>Read the saga</b> — why the first player's connection matters</summary>

In vanilla, a zone belongs to the nearest connected player for as long as they're there. That player's game runs
everything in it and sends it to everyone else. If their connection is the slowest on the server, their upload
and their ping quietly become everyone else's lag. Raising send rates alone doesn't fix that.

With `ServerRunsWorld` on, the server runs those areas itself. `Mode` sets how much: `SharedFar` runs only areas
nobody is standing in, `FarOrShared` (the default) runs everything except what players handle directly, and `All`
also takes chests, carts, stands and ground items. Moving an object to the server only changes which machine sends
its updates; nothing a player sees happen to it changes.

</details>

---

![II](https://img.shields.io/badge/II-EVERY_PLAYER_AT_THEIR_OWN_SPEED-1f4e79?style=for-the-badge&labelColor=1b1b1b)

**A fast connection isn't held to a slow one's pace.**

| | |
|---|---|
| 🔹 **Adaptive rate** | Each connection starts at vanilla's rate and climbs while it stays clean, up to `MaxSendRatePerPlayerKiB`. |
| 🔹 **Backs off by itself** | Packet loss or a rising ping slows that one player down, and nobody else. |
| 🔹 **Deeper queues for far players** | A player far from the server can get the same throughput as a nearby one. |
| 🔹 **Smaller updates** | When both ends run the mod, world updates are compressed. |

<details>
<summary>📖 <b>Read the saga</b> — how the rate is chosen</summary>

Every second, the server reads Steam's own numbers for each connection: how much is delivered, the ping against
that player's recent best, and how much is waiting to be sent. A connection that's keeping up and has more to send
is raised; one that drops packets or whose ping swells is lowered. A server-wide cap (`ServerUploadLimitMiB`) stops
the total from swamping your server's upload. With `ClientUploadBoost`, a modded player's own upload gets the same
treatment, so everyone else sees them sooner.

</details>

---

![III](https://img.shields.io/badge/III-EMPTY_AREAS_DON%27T_ROT-2e5e3a?style=for-the-badge&labelColor=1b1b1b)

**What you leave behind is still there when you come back.**

| | |
|---|---|
| 🔹 **No decay with nobody near** | Weather and structural damage pause in areas the server runs with no player in them. |
| 🔹 **No vanishing drops** | Dropped items don't despawn there. |
| 🔹 **No surprise fights** | Wildlife doesn't attack your tames or builds there. |
| 🔹 **Doors close** | Player-built doors left open are closed (keyed and dungeon doors never). |

---

![IV](https://img.shields.io/badge/IV-THE_STATUS_PANEL-5b2c83?style=for-the-badge&labelColor=1b1b1b)

**Press F6 and see who runs the world around you.**

| | |
|---|---|
| 🔹 **Basic** | Who runs the objects near you (server, you, other players), your ping, upload, download and limits. |
| 🔹 **Detailed** | Your upload and download against their limits, who runs each of the nine zones around you, and this client's features. |
| 🔹 **Anywhere** | Works on any server, modded or vanilla. Drag to move, drag the corner to resize; it remembers where you left it. |

<details>
<summary>📖 <b>Read the saga</b> — the client extras</summary>

Installing the mod on your own game also shortens vanilla's 2-second guess when updates stall, so other players and
creatures stop sliding and snapping back sooner (`ShortExtrapolation`). A smoother buffered mode exists too
(`BufferedSmoothing`), but it's off by default: it's smoother most of the time and worse than vanilla in the worst
case.

</details>

---

![THE TOOLBOX](https://img.shields.io/badge/THE_TOOLBOX-QUICK_START_%C2%B7_KEYS_%C2%B7_HOW--TO-1b1b1b?style=for-the-badge&labelColor=8b1e1e)

**First ten minutes:** install BepInExPack on the server → drop `NoodlesMcDoodles.dll` in `BepInEx/plugins/` → start the server. The defaults are already tuned.

| Key | | Command | |
|---|---|---|---|
| **F6** | status panel: Basic → Detailed → hidden | `noodles status` | what's on, what's off, and why |
| | | `noodles disable` / `enable` | everything to vanilla and back, no restart |
| | | `noodles clear <feature>` | turn a feature that switched itself off back on |

Commands work in the F5 console for server admins, or in the server console.

<details>
<summary>🛠️ <b>Players still lag?</b></summary>

Look for the server's status line in `BepInEx/LogOutput.log`:

`status: 3 players (2 modded) · sending 52 KiB/s · rate 150-256 KiB/s per player, 1 at vanilla · frame 33 ms avg / 298 ms max · …`

- Players at the top of the rate range while lagging → raise `MaxSendRatePerPlayerKiB`.
- "sending" close to `ServerUploadLimitMiB` → raise that, if your line can take it.
- A high frame max → the server itself is struggling, not the network.
- On a modded client, **F6 → Detailed** shows your own limits against what's actually in use.

</details>

<details>
<summary>🛠️ <b>Reporting a bug</b></summary>

Set `LogLevel = Verbose`, make it happen again, then send `BepInEx/LogOutput.log` and
`BepInEx/NoodlesMcDoodles/detail.log` on the Discord below. The detail log always holds what the main log leaves
out, and is capped at about 20 MB.

</details>

<details>
<summary>🛠️ <b>Uninstalling</b></summary>

Delete `NoodlesMcDoodles.dll` and restart. The mod never changes the world save format.

</details>

---

![MULTIPLAYER](https://img.shields.io/badge/MULTIPLAYER-WHO_NEEDS_IT-2e5e3a?style=for-the-badge&labelColor=1b1b1b)

| Works with vanilla players | Needs it on the server | Needs it on both ends |
|---|---|---|
| Server-run areas, adaptive rates, empty-area care | Everything in chapters I to III | Compressed updates, the F6 download numbers |

Players with and without the mod can share a server. The F6 panel and the client extras also work on a vanilla
server. Players and servers on older versions of NoodlesMcDoodles still work together with this one.

---

![COMPATIBILITY](https://img.shields.io/badge/COMPATIBILITY-PLAYS_WELL-2e5e3a?style=for-the-badge&labelColor=1b1b1b)

| Mod | How they get on |
|---|---|
| Mods that keep refusing the server's claims | The server falls back to running only empty areas until you run `noodles clear ModeQuarantine`. |
| Mods that hold items or doors on the server on purpose | ✅ Their refusals are respected and never trip the safety net. |
| LetItGrow | ✅ Both stay fully on. |
| Mods that use ServerSync | ✅ Rates are managed on the Steam connection underneath. |
| Crossplay (PlayFab) players | ✅ Welcome; their connection stays at vanilla rates. |

---

![SETTINGS](https://img.shields.io/badge/SETTINGS-ONE_SHORT_FILE-b8860b?style=for-the-badge&labelColor=1b1b1b)

Settings live in `BepInEx/config/wubarrk.noodlesmcdoodles.cfg`. Edit it while the server runs: most changes apply
within a second. Expert tuning is in `wubarrk.noodlesmcdoodles.advanced.cfg`; you shouldn't need it.

<details>
<summary>🖥️ <b>Server</b> — set on the server</summary>

| Section | Setting | What it does |
|---|---|---|
| 1 - General | `Enabled` | Master switch. `false` = plain vanilla, at once. |
| 1 - General | `LogLevel` | `Quiet`, `Normal` (a status line every 10 min) or `Verbose`. |
| 2 - Network | `MaxSendRatePerPlayerKiB` | Top send rate per player, in KiB/s. Default 256 (vanilla is a fixed 150). |
| 2 - Network | `ServerUploadLimitMiB` | Total the server sends to everyone, in MiB/s. Default 10. Keep it under your real upload. |
| 2 - Network | `AdaptiveRate` | Climb while a link is clean, back off when it isn't. Off = everyone at the top rate. |
| 2 - Network | `CompressWorldSync` | Compress updates to modded players. Sent to them from the server. |
| 3 - Server World | `ServerRunsWorld` | The server runs the world instead of the first player there. |
| 3 - Server World | `Mode` | `SharedFar`, `FarOrShared` (default) or `All`. See chapter I. |
| 3 - Server World | `BestPingOwnsSharedObjects` | Shared objects go to the nearby player with the best ping. |
| 3 - Server World | `ServerHoldsForFarPlayers` | **Experimental.** When everyone near an object has a high ping, the server keeps it. |
| 3 - Server World | `PreserveEmptyAreas` | No decay, despawns or fights where the server runs an empty area. |
| 3 - Server World | `DoorAutoClose` | Close player-built doors left open. |
| 5 - Safety | `AutoStandDown` | If the mod's own errors pile up, it switches itself off and says why. It never switches itself back on. |

</details>

<details>
<summary>🎮 <b>Player</b> — your own game</summary>

| Section | Settings |
|---|---|
| 2 - Network | `ClientUploadBoost` (faster upload of your own updates) |
| 4 - Client | `ShortExtrapolation` · `BufferedSmoothing` (experimental) · `PanelKey` (F6) · `PanelShowOnStart` · `PanelOpacity` |

</details>

---

![CREDITS](https://img.shields.io/badge/CREDITS-THANK_YOU-1b1b1b?style=for-the-badge&labelColor=c9a227)

**Needs:** BepInExPack Valheim · *Cooked up by Wubarrk.* · Bug reports and ideas are genuinely wanted: [Discord](https://discord.gg/GTTCnGS6Ym) · **License:** MIT, use, change and share freely, keep the copyright notice (`LICENSE.md`).
