# Beeline scenario tests

Run with Claude Test Kit (dev build of Beeline loaded, plus Lifestream and vnavmesh):

```
/ctk test C:\Users\lordd\Claude\work\Beeline\tests
/ctk test report
```

One file runs alone too: `/ctk test C:\Users\lordd\Claude\work\Beeline\tests\05-flying-trip.json`.
Scenario format and checks: `work\ClaudeTestKit\tests\README.md`.

## Test spot

Thavnair (territory 957), open ground just outside **Yedlihmad**, checked with `/ctk spotcheck` (open sky, no
aggressive enemies: the Gaja nearby is lv80, far below you). Test 03 starts at the Yedlihmad aetheryte and flies to the
start spot; 04-09 start and end there; 10 ends back at the aetheryte, so the folder runs again in one go.

| Spot | World X, Z | Used by |
|---|---|---|
| Start spot, ~110 y NW of the aetheryte, open | 150, 530 | 03-10 |
| Walk target, 42 y from the start | 120, 500 | (spare) |
| Fly target, ~180 y E on a hill, open | 330, 470 | 05-09 |

Don't put test spots among the town's buildings (awnings, walls, water: flights bump into them, landing is refused) or
within an aetheryte icon's reach (a click there is the game's paid teleport). Check new spots with `/ctk spotcheck x z`.
Before each click the tests close and reopen the map (`map close`, `map`) so it shows the current zone.

## How a test starts a trip: the real map press

Every trip starts the way a player starts it, with the Test Kit's real-looking input:

- `/ctk mapclick <x> <z> Left ctrl`: focuses the map, glides the mouse to where world spot (x, z) is drawn and
  Ctrl+clicks it (Beeline's default mouse trigger). Works with the game in the background.
- `/ctk mapcursor <x> <z> cross`: steers the map's controller crosshair onto the spot with the right stick, then
  presses Cross (09). Its result shows as the event `mapcursor reached` / `mapcursor FAILED`.

Each test first opens the map (`do: map`), waits 0.8 s (a press right after the map opens is ignored on purpose,
see 02), and parks the flag far away (`flag 600 -300`): a map icon covers ~38 y at the default zoom, so a leftover
flag next to a test spot would catch the click (Beeline goes to the icon under the cursor, as for a player).
The spots are open, flat ground (checked: the lab's 8/8 run). Zoomed out, a map icon covers ~38 y and a click near the
aetheryte would land on its icon (= the game's paid teleport), so the map is zoomed in before each click (pending an
absolute `/ctk mapzoom` level). Don't move the spots without checking the ground: 150,650 is walls and water. The clicked spot lands within a yalm or two of the target, so `dest=` checks allow +/-1 (the crosshair +/-6).

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
- `path`: `computing` · `held` (flying path ready, waiting for the mount) · `ready`. `walkafter=1`: flying to the open sky next to a covered spot
  (roof, arch, overhang), then on in, low. `nofly=1`: this move stays on the ground. `stuck`: Beeline's own stuck
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
| 03-short-trip | From the aetheryte, Ctrl+click the start spot ~40 y away: `route=direct`, reaches it; no WOBBLE/STUCK/SNAP. | Test spot, on foot. |
| 04-map-press-no-destination | Map open 1.2 s, Cross: Beeline sees the press (or ignores it if the map has no focus); a trip to where you stand ends by itself; no wobble/stuck/snap. | As 02, open world. |
| 05-flying-trip | ~110 y trip: mount summoned, take-off (`move=flying`, log "Took off"), lands at the clicked spot, flies back; no WOBBLE/STUCK/SNAP. | Test spot, on foot, flying unlocked. |
| 06-cancel-command | `/beeline stop` mid-trip stops at once (`last=cancelled`), then returns. | Test spot. |
| 07-cancel-by-moving | Pushing the left stick mid-trip cancels it (`last=cancelled-you-moved`, log line), then returns. WOBBLE isn't checked (you take over). | Test spot. |
| 08-new-trip-replaces | A new destination mid-trip replaces the trip (log "Trip replaced", probe `dest` changes) and arrives there. WOBBLE isn't checked (turning back is intended). | Test spot. |
| 09-controller-press | Crosshair steered onto a spot ~110 y away with the right stick, then Cross: "Controller on map", position from the crosshair, trip there; back by Ctrl+click. | Test spot, on foot, flying unlocked. |
| 10-aetheryte-icon | Ctrl+click on Palaka's Stand's aetheryte icon: Beeline logs "leaving it to the game's teleport", starts no trip, the game teleports you; back to Yedlihmad the same way. Costs two teleport fees. | Test spot, on foot. |

Costs: 04-09 should never pay for a teleport (10 pays two on purpose) (all trips are short, same zone, `route=direct` is asserted). If
Beeline ever picks a teleport there, the assert fails, but the teleport still happens and costs gil.

## Manual only (not automated)

- **Cross-zone trips, teleport / aethernet routes, shard or aetheryte under the cursor**: need Lifestream and
  cost gil (teleports), and the cursor on a map icon. Check by hand: point at a spot in another zone, press
  Cross, expect `route=teleport...`, `phase=teleporting`, then the usual move.
- **Quest search** (quest markers, NPC/object on arrival, targeting on arrival, quest areas): needs suitable
  quests in the journal; the probe shows `target=marker` and `approached=1` while you test by hand.
- **The keyboard hotkey trigger** (off by default): press it with the mouse over the map.
- **Enemy targeted stops the trip, Giddy Up dismount for a fight**: need enemies around (and the option on).
- **Trip timer in the server info bar**: off by default; turn it on in the settings and watch the info bar
  (the probe's `t` / `est` give the numbers).
- **Housing district aethernet**: needs a housing district.
