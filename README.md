# SimpleRegions — self-service land claims for TShock

A plugin for **TShock 6.1.0** (Terraria 1.4.5.6, TSAPI 2.1, .NET 9).

TShock's built-in flow to protect a house is five commands — `/region set 1`, walk,
`/region set 2`, `/region define`, `/region owner` — and every one of them needs
`tshock.admin.region`, which ordinary players don't have. SimpleRegions lets a player claim
their own house in one or two commands, see the borders, and stay within limits they can't
abuse.

## How it works

Claims are stored as **ordinary TShock regions**. The plugin creates and configures them,
so every claim remains visible to `/region list` and fully manageable with the built-in
admin tooling. Only the plugin's own bookkeeping (which regions it created, and the owner)
lives in a separate table.

Instead of capping the *number* of claims, players get a **land budget**: 40,000 tiles by
default, with each claim between 10x10 and 100x100.

## Land budget: union, not sum

The budget charges the **union** of a player's claims, never the sum of their areas. Land
covered by two of your own claims is paid for **once**.

This matters because overlapping your own claims is the supported way to build
non-rectangular footprints (L-shapes, courtyards, and so on). Charging per-region would bill
the same ground twice and penalise exactly the workflow the overlap rule exists to enable.

Every operation reports the remaining budget:

```
Приват 'База' создан: 2500 блоков. Осталось 37500 из 40000.
```

If a claim doesn't fit, the refusal says exactly what is missing, and `/rg list` shows each
claim's real contribution — for overlapping claims that is less than its raw area, because
it's the amount that deleting it would actually free.

## Overlap rules

| Overlap with | Allowed |
|---|---|
| Your own claims | **Yes** — this is how you build complex shapes |
| Another player's claim | No — the refusal names the region and its owner |
| An admin / non-plugin region | No |

**Build access inside an overlap:** a player may build if they are allowed in **at least
one** of the overlapping claims.

This needs the plugin's own hook, because TShock's `CanBuild` only consults the *top* region
by Z — so where two claims overlap, the higher one alone would decide and could silently
revoke access granted in the other. The override is deliberately narrow: if any non-plugin
(admin) region covers the tile, the plugin stays out of the decision entirely and lets
TShock handle it, so player claims can never be layered to punch a hole in an admin
protection.

## Commands

| Command | Permission | What it does |
|---|---|---|
| `/rg pos1` / `/rg pos2` | `simpleregions.claim` | Mark the two corners |
| `/rg claim <name>` | `simpleregions.claim` | Claim the marked rectangle |
| `/rg claim <name> <radius>` | `simpleregions.claim` | Claim a square around yourself, no marking needed |
| `/rg list` | — | Your claims: size, budget contribution, members, and the total used |
| `/rg info` | — | What is claimed where you stand (handles overlaps) |
| `/rg add <player> <name>` | `simpleregions.claim` | Let someone build in your claim |
| `/rg remove <player> <name>` | `simpleregions.claim` | Revoke that access |
| `/rg delete <name>` | `simpleregions.claim` | Delete your own claim |
| `/rg show` | `simpleregions.show` | Toggle border highlighting |

Command alias: `/regions`. Players can only ever manage **their own** claims.

Holders of `simpleregions.admin` additionally:

* bypass the size, budget and overlap limits;
* may `/rg delete`, `/rg add` and `/rg remove` on **anyone's** claim;
* may **build and break blocks inside any claim** without being added to it — needed for
  clearing griefed builds or fixing someone's claim. This applies only to claims created by
  this plugin; ordinary admin regions are left entirely to TShock's own permission handling.

**Corner anchoring:** marking a corner snaps to the block you are **standing on**, not to
your head. A player's position is measured from the top of their sprite, so anchoring
naively would place corners ~3 tiles too high and leave the floor you marked from outside
the claim.

`/rg info` lists **all** regions at your position when they overlap, with owners, and says
outright that they are layered:

```
Здесь 2 наслаивающихся привата(ов):
  'База' (владелец: Ник)
  'Склад' (владелец: Ник)
```

## Border highlighting

The server can send **one specific client** tile data that differs from the real world. The
player sees the highlight, the world is not modified, and nobody else sees anything.

Highlighting works by flipping the **paint colour** of existing tiles rather than swapping
blocks, so tiles keep their shape and only change colour. Three things are drawn:

1. `/rg pos1` — a marker on the first corner.
2. `/rg pos2` — a **preview** of the exact rectangle before you commit, WorldEdit-style,
   together with its area and whether your budget covers it.
3. `/rg show` — borders of nearby claims: your own in one colour, other players' in another.

Practical details this accounts for:

* **Desync.** Clients silently drop fake tiles whenever they re-request a chunk (walking
  away and back, section reload), so an active highlight is re-sent on a timer
  (`HighlightRefreshSeconds`, default 4s).
* **Packet volume.** Only tiles within `HighlightRadius` (default 100) are drawn, and only
  the four edge strips of each rectangle — not the filled box — so a 100x100 claim costs a
  handful of tiny packets instead of one 10,000-tile blob.
* **Interaction safety.** Touching a highlighted tile drops the highlight and resends real
  data, so a player can't end up desynced against paint that was never really there.
* **Cleanup.** Real tiles are restored when the mode is switched off, when the player
  disconnects, and when the plugin unloads.

Existing tiles are only re-**painted**, so they keep their shape. Where a border crosses
**open air** there is nothing to paint, so a temporary marker block (`HighlightAirBlock`,
glass by default) is sent for those tiles instead — again to that one client only. Without
this, a surface claim would be practically invisible, since its top and side borders run
through open sky.

## Data safety across plugin updates

Claims must survive upgrades, so the storage layer:

1. **never** drops or recreates tables — `CREATE TABLE IF NOT EXISTS` only;
2. keeps a schema version in its own table, so future layout changes migrate via `ALTER
   TABLE` with the data intact;
3. checks consistency at startup and only **warns** — if a claim exists in the plugin's
   table but not in TShock (or the reverse), it is logged loudly and nothing is deleted,
   because silent cleanup would destroy player claims after a botched restore;
4. keys rows by **region name + world**, never by a region id that changes when a region is
   recreated.

The plugin's table lives in `tshock.sqlite`. That's fine here: claim operations are a
handful per day, so the write-lock contention that matters for high-volume logging is a
non-issue at this rate.

## Installation

1. Download `SimpleRegions.dll` from
   [Releases](https://github.com/Solevaral/SimpleRegions/releases) or from the repository:
   `dist/SimpleRegions.dll`.
2. Drop it into your TShock server's `ServerPlugins/` folder.
3. Restart the server — `tshock/SimpleRegions.json` is created automatically.
4. Grant claiming to ordinary players (this is the point of the plugin):

```bash
/group addperm default simpleregions.claim
```

```bash
/group addperm default simpleregions.show
```

```bash
/group addperm admin simpleregions.admin
```

### Building from source

```bash
dotnet build SimpleRegions/SimpleRegions.csproj -c Release
```

No third-party NuGet packages beyond TShock and its own dependencies are used.

## Config — `tshock/SimpleRegions.json`

| Field | Default | What it does |
|---|---|---|
| `Enabled` | `true` | Turns the plugin's commands and hooks on/off |
| `AreaBudgetPerPlayer` | `40000` | Total land per player, counted as a union of their claims |
| `MinRegionSize` | `10` | Minimum side of a single claim |
| `MaxRegionSize` | `100` | Maximum side of a single claim |
| `SpawnProtectionRadius` | `0` | Reject claims closer than this to world spawn. `0` disables |
| `HighlightRadius` | `100` | How far from the player borders are drawn |
| `HighlightRefreshSeconds` | `4` | How often an active highlight is re-sent |
| `HighlightChunkSize` | `50` | Longest side of a single highlight packet |
| `MaxRegionNameLength` | `32` | Maximum claim name length |
| `HighlightAirBlock` | glass | Marker block shown where a border crosses open air |
| `PaintOwnRegion` | green | Paint id for your own claims |
| `PaintForeignRegion` | red | Paint id for other players' claims |
| `PaintSelection` | yellow | Paint id for the pending selection preview |
| `PaintCorner` | cyan | Paint id for the first-corner marker |
| `Messages` | Russian | All player-facing text |

## Notes

* All in-game messages are Russian, matching the target server's audience; refusals always
  explain the specifics (`Слишком маленький участок: минимум 10x10, у вас 8x12`) rather than
  an error code. Code comments and this README are English.
* Region sizes are stored the way TShock expects them: its `Region.InArea` treats the upper
  bound as inclusive, so a region stored with `Width=10` actually protects 11 tiles. The
  plugin converts explicitly (verified against the real `Region` class in tests), so the
  area a player is charged is exactly the area that ends up protected.

## License

MIT, see [LICENSE](LICENSE).
