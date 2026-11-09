<p align="center">
  <img src="Beeline/images/icon.png" width="128" alt="Beeline icon">
</p>

<h1 align="center">Beeline</h1>

<p align="center">
  A Dalamud plugin for FINAL FANTASY XIV: <b>one button on the map</b> (controller, mouse or keyboard)<br>
  takes you to any aetheryte, quest marker, NPC or spot, <b>the quickest way</b>.
</p>

---

## Why?

Getting somewhere usually means opening the teleport list, finding the right aetheryte, taking the aethernet, mounting up and steering the rest of the way.
On a controller, every one of those steps is a few more button presses.

With Beeline you open the map, point at where you want to go, and press one button. Beeline picks the quickest way and takes you there.
It's made for controllers, and works just as well with a mouse or a keyboard key.

## Features

- **One button, everything on the map:**
  - **On an aetheryte or an aethernet shard:** you go there. If you're on that city's aethernet, the aethernet is used (it's free); otherwise you teleport, and take the aethernet from there if the shard needs it.
  - **Anywhere else, in any zone:** Beeline compares going there directly, taking the aethernet, and teleporting to the aetheryte (or shard) closest to the spot, and picks the quickest. Then it:
    - summons a mount on the way if the spot is far (Mount Roulette, while you keep moving),
    - flies if you have the zone's aether currents,
    - and moves you the rest of the way with vnavmesh.
  - **No map flag is placed**, so your own flag is never touched.
- **Made for questing.** Quest markers are used at their **exact** position, height included (the map cursor alone is only precise to about 5 yalms):
  - **Quest NPCs and objects:** you walk right up to them, and they get **targeted once you're close enough to talk to them or use them**: just press confirm. (Can be turned off.)
  - **Quest areas** (the circles on the map): you're taken into the area. Areas that make the enemies appear when you step in work too.
  - **Quest enemies:** you stop at a **safe distance** (20 yalms by default) so you don't pull them by accident, unless they're far below your level and can't aggro you anyway. Enemies are never targeted.
  - **Layered places** (cities with several floors): the right floor is picked from the marker's height. For plain spots, the floor at your own height is preferred.
  - Works with active quests, new quests (the quest-giver icons), levequests, guildleves, Triple Triad and special NPCs.
  - **Quest journal:** open the map from a quest in the journal and press the button right away: the quest's marker is found by its name.
- **Controller, mouse or keyboard, any button or combo:**
  - Controller: **Cross / A** by default. Any button works, and you can add a second button to hold for a combo (for example L2 + Cross).
  - Mouse: **Ctrl + left click** by default. Left, right or middle click or a side button, with any mix of Ctrl, Shift and Alt.
  - Keyboard: any key you choose (with any mix of Ctrl, Shift and Alt), pressed while the mouse is over the map.
- **You stay in control.** A trip stops as soon as you move your character, and in a few other cases (see [When does a trip stop?](#when-does-a-trip-stop)).
- **Optional: dismount when you target an enemy** (off by default).
- **Lightweight.** It does nothing while the map is closed and no trip is running. See [Performance](#performance).
- **Private.** Nothing is sent anywhere, and its log lines never contain your character's name or world.

## Requirements

- [Dalamud](https://goatcorp.github.io/) (XIVLauncher)
- [Lifestream](https://github.com/NightmareXIV/Lifestream), for teleports and the aethernet
- [vnavmesh](https://github.com/awgil/ffxiv_navmesh), for moving you to the spot

Both are free plugins you can find in the plugin installer. The settings window shows whether they're found.
Without Lifestream, Beeline still works inside your current zone (it just can't teleport).

## Installation

1. In game, type `/xlsettings` and open the **Experimental** tab.
2. Under **Custom Plugin Repositories**, paste this URL in an empty line and click the **+** button:
   ```
   https://raw.githubusercontent.com/KeydenK/Beeline/main/repo.json
   ```
3. Click **Save and Close**.
4. Type `/xlplugins`, search for **Beeline** and click **Install**.

It works right away with a controller (Cross / A) and a mouse (Ctrl + left click). No setup needed.

## Usage

1. Open the map.
2. Put the cursor on where you want to go: an aetheryte, a shard, a quest marker, an NPC icon, a quest area or any spot. Any zone works, not just the one you're in.
3. Press **Cross / A** on a controller, **Ctrl + left click** with a mouse, or your keyboard key.

That's it. You can close the map; the trip goes on. To stop it, just move your character.

Type `/beeline` to open the settings window. Its top shows whether Lifestream and vnavmesh are found and, during a trip, what Beeline is doing (teleporting, moving...) with a **Stop** button.

### Settings

| Setting | What it does | Default |
| --- | --- | --- |
| Go to what is under the map cursor | Turns Beeline on or off | On |
| Controller button | Any button: face buttons, D-pad, L1 / R1, L2 / R2, L3 / R3, Create / View, Options / Menu | Cross / A |
| Controller combo | An optional second button to hold at the same time (for example L2 + Cross) | No combo |
| Mouse button | Left, right or middle click, back or forward side button, or off. Plus any mix of Ctrl, Shift and Alt. Dragging the map doesn't count | Ctrl + left click |
| Keyboard key | Any key, plus any mix of Ctrl, Shift and Alt, pressed while the mouse is over the map. Pick one the game doesn't use, or both will happen | None |
| Use aethernet shards to get closer | Routes may go through the aethernet shard closest to the spot | On |
| Mount up when the point is far | Uses Mount Roulette on the way (you keep moving while it's summoned), if the spot is further than this | On, 40 yalms |
| Fly when the zone allows it | Flies when you have all the zone's aether currents | On |
| Stop this far from quest mobs | Safe distance from quest enemies | 20 yalms |
| Walk right up to mobs far below your level | No safe distance from enemies this many levels below you | On, 10 levels |
| Target the quest NPC or object when you arrive | Only friendly NPCs and objects, only within talking range | On |
| Dismount when you target an enemy | Gets you off your mount as soon as you target something you can attack | Off |
| **Advanced:** Route timing | Shows what Beeline learned from your trips (how long your teleports and aethernet hops take, loading screens included, and how fast you go on foot, riding and flying), with a button to reset it | Learned automatically |
| **Advanced:** Write each button press and trip step to the Dalamud log | For troubleshooting | Off |

### Commands

| Command | What it does |
| --- | --- |
| `/beeline` | Open the settings |
| `/beeline stop` | Stop the current trip |

## When does a trip stop?

- **You arrive.** (Next to the quest NPC or object, at the safe distance from quest enemies, or at the spot.)
- **You move your character** with the left stick or the movement keys (W, A, S, D, Q, E and the arrow keys). Typing in chat or in a plugin window doesn't count.
- **You press the button again** on something else: the new trip replaces the old one.
- **You type `/beeline stop`**, or click **Stop** in the settings window.
- **You change zone** after arriving (a duty pops, you teleport yourself, you log out...).
- **You target an enemy while mounted**, if "Dismount when you target an enemy" is on.
- **Something goes wrong**, with a chat message: the teleport is refused (in combat, not enough gil...), there's no walkable ground near the spot, the teleport takes longer than 60 seconds, or vnavmesh isn't ready after 2 minutes.

## Safeguards

So that Beeline never goes somewhere you didn't ask for:

- The button is **ignored for half a second after the map opens**, so the press that opened the map (from the quest journal, for example) doesn't count.
- **Holding the button** only counts once.
- With a controller, the press only counts **when the map has the focus**, not when another window (like the journal) is in front of it.
- With a mouse or keyboard, the mouse must be **over the map**, and not over a plugin window.
- If the cursor **hasn't moved since the map opened**, its position is still the old one (the game only updates it when the cursor moves). Instead of guessing, Beeline asks you to move the cursor a little. Quest markers named under the cursor work anyway.

## FAQ

**Which buttons should I avoid?**
The map already uses some of them. With a controller, **Square / X and Circle / B close the map** and **Triangle / Y switches between the city and region maps**. With a mouse, **middle click is auto-run**, a plain **left click** on an aetheryte opens the game's teleport window, and **right click** opens the map's menu. The defaults (Cross / A, and Ctrl + left click) avoid all of that. If you'd like another button the map uses, add a combo (a held controller button, or Ctrl / Shift / Alt) and the settings window warns you when a choice would clash.

**Why Ctrl + left click?**
A plain left click is already used by the map (an aetheryte opens the game's teleport window) and by Lifestream (a click on a shard takes the aethernet). Lifestream deliberately ignores Ctrl clicks, so Ctrl + left click is free. Dragging the map with the button held never counts as a click.

**Does it cost gil?**
Teleports cost the usual gil, like teleporting yourself. The aethernet is free.

**It teleported even though I could have walked.**
Beeline estimates the time of each route and picks the shortest one. A teleport takes a while (the cast plus the loading screen), but if the aetheryte is much closer to the spot than you are, it still wins. The estimates come from your own trips: Beeline times your teleports and aethernet hops (a fast PC loads quicker) and measures how fast you actually get somewhere on foot, riding and flying, so its choices get better as you use it. You can see these numbers in **Advanced → Route timing**.

**Why didn't it mount?**
It only mounts when the spot is further than the "mount up" distance, where mounts are allowed (not in most cities), and not in combat. The mount is summoned while you're already moving; once you're mounted in a zone where you can fly, Beeline takes off and flies the rest of the way.

**I use another plugin that mounts me automatically. Will they fight?**
No. Beeline only summons a mount if you aren't mounted yet, and it waits while you're already casting or getting on a mount, so another plugin's auto-mount (on entering a zone, after a fight...) simply wins and Beeline uses that mount. You can also turn off "Mount up when the point is far" and let your other plugin handle mounting.

**Why didn't it fly?**
Flying needs all the aether currents of the zone, and the "Fly when the zone allows it" option. Otherwise you ride on the ground.

**Why didn't it target the NPC?**
It only targets when the option is on, when the NPC or object is friendly, and when you're close enough to talk to it or use it. Some quest objects can't be targeted until a quest step is done.

**Why did it stop so far from the enemies?**
That's the safe distance, so you don't pull them by accident (20 yalms by default, adjustable). Enemies 10 or more levels below you can't aggro you, so Beeline walks right up to them instead.

**I got "move the map cursor a little".**
The game only updates the cursor position once you move it, so right after the map opens it's still the old one. Nudge the cursor and press again. Quest markers named under the cursor work without moving it.

**I got "no attuned aetheryte in that zone".**
Beeline only uses aetherytes you've attuned to. Attune to one in that zone, or go there yourself once.

**Does it work in my own zone without Lifestream?**
Yes. Without Lifestream, Beeline can still take you anywhere in the zone you're in. It just can't teleport or use the aethernet.

**Can I use it with Questionable, AutoDuty or other plugins that move my character?**
Don't run both at the same time: they would fight over vnavmesh.

**It stopped short / went to the wrong place.**
Turn on **Advanced → Write each button press and trip step to the Dalamud log**, try again, and include the `[Beeline]` lines from `dalamud.log` when you [open an issue](https://github.com/KeydenK/Beeline/issues). Those lines contain map coordinates, zone numbers, quest and NPC names, never your character's name or world.

**Is this allowed?**
Like every third-party tool, Dalamud plugins are against the game's terms of service, and Beeline moves your character for you (through vnavmesh). Use it at your own risk, and stay at your keyboard or controller while it runs.

## Limitations

- **Spots without a marker** are only as precise as the map cursor (about 5 yalms). Quest markers are exact.
- **Plain spots in layered places** (streets with walkways or roofs above) go to the floor at your own height when there is one there, otherwise to the highest one. Quest markers always pick the right floor.
- **Residential district shards** (housing wards) and **airship landings** aren't used for routes.
- **Gathering points** aren't walked to or targeted: gathering leves take you to the area.
- **Quest areas whose enemies are already there** before you arrive count as roaming enemies: you stop at the safe distance.
- **Rebound movement keys:** only the default keys (W, A, S, D, Q, E, arrows) and the left stick cancel a trip.
- **"Far below your level"** is set to 10 levels by default; the game's exact rule for when enemies stop aggroing you may differ. You can change it.

## Performance

Beeline is built to cost nothing when you're not using it:

- **Map closed and no trip running:** it does nothing at all.
- **Map open:** one quick check per frame for your button or click.
- **During a trip:** a check for your own movement every frame (so stopping is instant), and the trip steps 10 times a second, until you arrive.
- **While mounted:** only reacts when your target changes (for the dismount option).
- **At load:** the aetheryte list is built once, in the background.

## How it works (for the curious)

Aetheryte and shard positions come from the game's own map data, the same source Lifestream uses.
Quest markers come from the game's marker list, which holds each marker's exact 3D position, its area radius, and the NPC, object or enemy it points at.

The route is picked by estimating how many seconds each one takes: the teleport and aethernet hops, then riding or flying (the mount is summoned on the way) or running the rest of the way. These times are learned from your own trips (how long your teleports and aethernet hops take, and how fast you really progress toward a spot, detours included), starting from sensible defaults.
Teleports go through Lifestream: first the teleport, then, once you've arrived, the aethernet hop if the route needs one.

After arriving, Beeline asks vnavmesh for the walkable ground at the spot (at the marker's height when there is one) and sends you there, or right next to the quest NPC or object once it's loaded.
Targeting only happens within the game's talking distance. "Enemy" means anything the game would let you attack.

## Building

Requires the .NET 10 SDK and a local Dalamud install (or set `DALAMUD_HOME`).

```
dotnet build -c Release
```

## License

[MIT](LICENSE) © KeydenK

Thanks to NightmareXIV for Lifestream and awgil for vnavmesh, and their APIs, which made this plugin possible.
