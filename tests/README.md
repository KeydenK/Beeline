# Beeline scenario tests

Run with Claude Test Kit (dev build of Beeline loaded, plus Lifestream and vnavmesh):

```
/ctk test C:\Users\lordd\Claude\work\Beeline\tests
/ctk test report
```

One file runs alone too: `/ctk test C:\Users\lordd\Claude\work\Beeline\tests\05-flying-trip.json`.
Scenario format and checks: `work\ClaudeTestKit\tests\README.md`.

## Test spot

Tests 04-08 start and end at the **Yedlihmad aetheryte in Thavnair** (territory 957, aetheryte at world
X 196, Z 628; from the game data). Stand next to it, on foot, nothing targeted, flying unlocked. Each test
flies/walks out and comes back, so the whole folder runs in one go. The destinations are fixed world spots:

| Spot | World X, Z | Used by |
|---|---|---|
| Walk target, ~40 y NW | 175, 595 | 04 |
| Fly target, ~110 y NW | 150, 530 | 05-08 |
| Return spot, by the aetheryte | 190, 615 | 04-08 |

If a spot turns out to be bad ground (water, a roof, a wall), pick a new one with DalamudMCP `get_position`
and change the numbers in the JSON files (the `flag`, `near:` and `dest=` values).

## How a test starts a trip: `/beeline testflag`

A player points the map cursor at a spot and presses the map button. A scenario can't steer the map cursor to
a world spot, so dev builds have a hidden subcommand: `/beeline testflag` starts the same trip as pressing the
map button with the cursor on your flag (same `Travel.Start` call: route choice, mount, take-off, vnavmesh,
arrival). It does nothing in installed (non-dev) builds and isn't in the help text. Tests set the flag with
`/ctk flag <x> <z>` first. The map press itself is covered by 02 and 03.

## The probe

`Beeline.TestState` (dev builds only, see `Beeline/TestProbe.cs`) answers one line, e.g. during a flight:

```
trip=on; phase=moving; move=flying; route=direct; dest=957@(150,530); left=62; path=ready; fly=1; wantmount=1; tookoff=1; jumps=1; target=flag; approached=0; t=9; est=12; stuck=0; trips=4; last=none; zone=957; mounted=1; inflight=1
```

and when idle:

```
trip=off; phase=idle; trips=4; last=arrived; lastleft=2; zone=957; mounted=0; inflight=0
```

- `phase`: `starting` (going directly, first step) · `teleporting` (teleport/aethernet under way) · `waitmesh`
  (waiting for vnavmesh) · `moving` · `idle`.
- `move` (while moving): `mounting` (mount on its way) · `takeoff` (mounted, flying leg, not in the air yet) ·
  `flying` · `riding` · `walking`.
- `route`: `direct` · `teleport` · `aethernet` · `teleport+aethernet`. `dest`: territory@(world X,Z) of the
  destination; `left`: yalms (flat) still to go; `t` / `est`: seconds since the trip started / the route's estimate.
- `path`: `computing` · `held` (flying path ready, waiting for the mount) · `ready`. `stuck`: Beeline's own stuck
  watchdog stage (0 = fine).
- `target`: what the press picked: `flag` · `marker` (quest/map marker) · `none` (a plain spot). `approached`: it
  went for the marker's NPC/object.
- `trips`: trips started since load. `last`: how the last trip ended: `arrived` · `no-path` · `cancelled` (/beeline
  stop) · `cancelled-you-moved` · `replaced` · `stopped-stuck-couldn-t-move-for-a-while` · `timed-out-...` ·
  `failed-...` (the log reason, lower case, dashes). `lastleft`: yalms from the target when it arrived.

## Tests

| File | Checks | Preconditions |
|---|---|---|
| 01-probe | The probe answers in the documented format; `/beeline stop` with no trip does nothing. | Anywhere, no trip running. |
| 02-map-just-opened | The press right after the map opens (within 0.5 s) is ignored, no trip starts. | Nothing targeted, not right next to an NPC/aetheryte (the virtual Cross may also reach the game). |
| 03-map-press-no-destination | Map open 1.2 s, Cross: Beeline sees the press (or ignores it if the map has no focus); a trip to where you stand ends by itself; no wobble/stuck/snap. | As 02, open world. |
| 04-short-trip | Short trip to the flag (~40 y): `route=direct`, reaches the flag, comes back; no WOBBLE/STUCK/SNAP. | Test spot, on foot. |
| 05-flying-trip | ~110 y trip: mount summoned, take-off (`move=flying`, log "Took off"), lands at the flag, flies back; no WOBBLE/STUCK/SNAP. | Test spot, on foot, flying unlocked. |
| 06-cancel-command | `/beeline stop` mid-trip stops at once (`last=cancelled`), then returns. | Test spot. |
| 07-cancel-by-moving | Pushing the left stick mid-trip cancels it (`last=cancelled-you-moved`, log line), then returns. WOBBLE isn't checked (you take over). | Test spot. |
| 08-new-trip-replaces | A new destination mid-trip replaces the trip (log "Trip replaced", probe `dest` changes) and arrives there. WOBBLE isn't checked (turning back is intended). | Test spot. |

Costs: 04-08 should never pay for a teleport (all trips are short, same zone, `route=direct` is asserted). If
Beeline ever picks a teleport there, the assert fails, but the teleport still happens and costs gil.

## Manual only (not automated)

- **Cross-zone trips, teleport / aethernet routes, shard or aetheryte under the cursor**: need Lifestream and
  cost gil (teleports), and the cursor on a map icon. Check by hand: point at a spot in another zone, press
  Cross, expect `route=teleport...`, `phase=teleporting`, then the usual move.
- **Quest search** (quest markers, NPC/object on arrival, targeting on arrival, quest areas): needs suitable
  quests in the journal; the probe shows `target=marker` and `approached=1` while you test by hand.
- **Mouse and keyboard triggers** (Ctrl + left click, the hotkey): need the map's position on screen to click on.
- **Pad press with the cursor moved to a spot** (crosshair maths): needs the map cursor steered on the map.
- **Enemy targeted stops the trip, Giddy Up dismount for a fight**: need enemies around (and the option on).
- **Trip timer in the server info bar**: off by default; turn it on in the settings and watch the info bar
  (the probe's `t` / `est` give the numbers).
- **Housing district aethernet**: needs a housing district.
