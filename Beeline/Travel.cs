using System.Diagnostics;
using System.Numerics;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using Dalamud.Game.ClientState.Keys;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;

namespace Beeline;

/// <summary>
/// Goes to a point picked on the map: the fastest of walking/riding there directly, the aethernet,
/// or a teleport (+ aethernet) to the aetheryte closest to it, then vnavmesh to that spot (no map flag is placed).
/// Runs a per-frame check only while a trip is in progress.
/// </summary>
internal sealed unsafe class Travel : IDisposable
{
    private enum Phase { None, Arriving, Mounting, WaitMesh, Moving }

    // Keys that move the character with the default keyboard layout.
    private static readonly VirtualKey[] MoveKeys =
    [
        VirtualKey.W, VirtualKey.A, VirtualKey.S, VirtualKey.D, VirtualKey.Q, VirtualKey.E,
        VirtualKey.UP, VirtualKey.DOWN, VirtualKey.LEFT, VirtualKey.RIGHT,
    ];

    private readonly Plugin _p;
    private Phase _phase;
    private uint _territory;
    private Vector2 _target;
    private Vector2? _arrival;     // where we expect to land after teleport/aethernet
    private bool _navStarted;      // vnavmesh was seen moving during this trip
    private float? _targetY;       // exact height when going to a game marker
    private uint _dataId;          // NPC/object/mob the marker points at (0 = none)
    private float _radius;         // quest area radius (0 = a single spot)
    private bool _approached;      // already walked up to that NPC/object
    private uint _finalTerritory;  // zone of the destination (_territory is the zone of the current step)
    private uint _pendingShard;    // aethernet hop still to take after the teleport (0 = none)
    private Vector2? _shardArrival;
    private bool _endOnArrival;    // shard trips: done once we're there
    private readonly Stopwatch _tripTime = new();
    private readonly Stopwatch _phaseTime = new();
    private readonly Stopwatch _tickTime = new();
    private int _mountTries;
    private const int StepIntervalMs = 100;

    public Travel(Plugin p) => _p = p;

    private Configuration Config => _p.Config;
    public bool IsActive => _phase != Phase.None;
    public string Status => _phase switch
    {
        Phase.Arriving => "Teleporting...",
        Phase.Mounting => "Mounting...",
        Phase.WaitMesh => "Waiting for vnavmesh...",
        Phase.Moving   => "Moving there...",
        _ => "",
    };

    public void Dispose()
    {
        if (_phase != Phase.None)
            try { _p.Ipc.NavStop(); } catch { /* vnavmesh missing */ }
        Stop();
    }

    public void Start(uint territory, uint mapId, Vector2 mapCoords, string tooltip, float iconWithin, bool cursorStale = false)
    {
        if (!Plugin.Data.GetExcelSheet<Lumina.Excel.Sheets.Map>().TryGetRow(mapId, out var map) || territory == 0)
            return;

        var target = new Vector2(
            Places.MapToWorld(mapCoords.X, map.SizeFactor, map.OffsetX),
            Places.MapToWorld(mapCoords.Y, map.SizeFactor, map.OffsetY));

        // A quest (or other game) marker at the cursor gives the exact spot, height and NPC/object.
        var marker = QuestMarkers.Find(territory, target, tooltip, iconWithin);
        if (marker is { } mk)
        {
            Plugin.Info($"{Plugin.Tag} Marker \"{mk.Label}\" at {mk.Position} (radius {mk.Radius:0}, object {mk.DataId}), {Vector2.Distance(target, new Vector2(mk.Position.X, mk.Position.Z)):0.0} y from the cursor");
            target = new Vector2(mk.Position.X, mk.Position.Z);
        }
        else if (cursorStale)
        {
            // The game only updates the cursor position once the cursor moves (not when the map
            // opens centred on something): going there would be wrong.
            Plugin.Info($"{Plugin.Tag} Cursor position not updated yet: ignored");
            Plugin.Chat.Print("Beeline: move the map cursor a little, then press again.");
            return;
        }

        var player = Plugin.ObjectTable.LocalPlayer;
        if (player == null)
            return;

        // Without Lifestream only "go directly" routes are possible (BestRoute skips the others).
        try
        {
            if (_p.Ipc.LifestreamBusy()) { Plugin.Chat.PrintError("Beeline: Lifestream is busy."); return; }
        }
        catch { /* Lifestream missing */ }

        Cancel("replaced", quiet: true); // a new press replaces the current trip

        var here = new Vector2(player.Position.X, player.Position.Z);
        var route = BestRoute(territory, target, here);
        if (route == null)
        {
            Plugin.Chat.PrintError(Plugin.IsPluginLoaded("Lifestream")
                ? "Beeline: no attuned aetheryte in that zone."
                : "Beeline: Lifestream is needed to go to another zone.");
            return;
        }

        Plugin.Info($"{Plugin.Tag} Travel to ({mapCoords.X:0.0}, {mapCoords.Y:0.0}) \"{tooltip}\" in territory {territory}: {route.Value.Describe}");
        var r = route.Value;
        _territory  = r.ThenShard != 0 ? r.FirstTerritory : territory;
        _finalTerritory = territory;
        _pendingShard   = r.ThenShard;
        _shardArrival   = r.ThenArrival;
        _endOnArrival   = false;
        _target     = target;
        _arrival    = r.Arrival;
        _targetY    = marker?.Position.Y;
        _dataId     = marker?.DataId ?? 0;
        _radius     = marker?.Radius ?? 0;
        _approached = false;

        try
        {
            if (!route.Value.Go())
            {
                Plugin.Chat.PrintError($"Beeline: the game refused to {route.Value.Describe}.");
                return;
            }
        }
        catch (Exception e)
        {
            Plugin.Log.Warning($"{Plugin.Tag} Starting the trip failed: {e.Message}");
            Plugin.Chat.PrintError("Beeline: could not start the trip (Lifestream/vnavmesh missing?).");
            return;
        }

        _navStarted = false;
        _tripTime.Restart();
        _tickTime.Restart();
        SetPhase(Phase.Arriving);
        Plugin.Framework.Update += OnUpdate;
    }

    /// <summary>Shard under the cursor, away from its network: teleport to the city's aetheryte, then take the aethernet.</summary>
    public void StartHop(Place master, Place shard)
    {
        Cancel("replaced", quiet: true);
        if (!_p.Ipc.TeleportTo(master.Id))
        {
            Plugin.Chat.PrintError($"Beeline: could not teleport to {master.Name}.");
            return;
        }
        _territory      = master.Territory;
        _finalTerritory = shard.Territory;
        _arrival        = master.Position;
        _pendingShard   = shard.Id;
        _shardArrival   = shard.Position;
        _endOnArrival   = true;
        _target         = shard.Position ?? default;
        _targetY        = null;
        _dataId         = 0;
        _radius         = 0;
        _navStarted     = false;
        _tripTime.Restart();
        _tickTime.Restart();
        SetPhase(Phase.Arriving);
        Plugin.Framework.Update += OnUpdate;
    }

    public void Cancel(string why, bool quiet = false)
    {
        if (_phase == Phase.None) return;
        if (!quiet) Plugin.Chat.Print($"Beeline: trip {why}.");
        var wasTeleporting = _phase == Phase.Arriving && _arrival != null;
        Stop();
        try { _p.Ipc.NavStop(); } catch { /* vnavmesh missing */ }
        if (wasTeleporting)
            try { _p.Ipc.LifestreamAbort(); } catch { /* Lifestream missing */ }
    }

    private void Stop()
    {
        if (_phase != Phase.None)
            Plugin.Framework.Update -= OnUpdate;
        _phase = Phase.None;
    }

    private void SetPhase(Phase phase)
    {
        _phase = phase;
        _phaseTime.Restart();
    }

    /// <summary>
    /// A way to get there. Teleport + aethernet routes first teleport to <see cref="FirstTerritory"/>, then, once
    /// arrived, take the aethernet to <see cref="ThenShard"/> (two steps with Lifestream's regular calls).
    /// </summary>
    private readonly record struct Route(float Cost, string Describe, Vector2? Arrival, Func<bool> Go,
                                         uint FirstTerritory = 0, uint ThenShard = 0, Vector2? ThenArrival = null);

    private Route? BestRoute(uint territory, Vector2 target, Vector2 here)
    {
        var places = _p.Places;
        if (places == null)
            return null;
        var routes = new List<Route>();
        var ui = UIState.Instance();

        if (Plugin.ClientState.TerritoryType == territory)
            routes.Add(new Route(Vector2.Distance(here, target), "go directly", null, () => true));

        if (!Plugin.IsPluginLoaded("Lifestream") || ui == null)
            return routes.Count == 0 ? null : routes[0];

        uint activeId = 0;
        try { activeId = _p.Ipc.ActiveAetheryte(); } catch { /* ignore */ }
        var active = activeId != 0 ? places.ById(activeId) : null;

        foreach (var place in places.InTerritory(territory))
        {
            if (place.Position is not { } pos || !ui->IsAetheryteUnlocked(place.Id))
                continue;
            var rest = Vector2.Distance(pos, target);
            var name = place.Name;

            if (Config.UseShardsForTravel && active != null && !place.IsMain && place.Group != 0 && place.Group == active.Group && place.Id != active.Id)
            {
                var id = place.Id;
                routes.Add(new Route(Config.AethernetCost + rest, $"aethernet to {name}", pos, () => _p.Ipc.AethernetTo(id)));
            }

            if (place.IsMain)
            {
                var id = place.Id;
                routes.Add(new Route(Config.TeleportCost + rest, $"teleport to {name}", pos, () => _p.Ipc.TeleportTo(id)));
            }
            else if (Config.UseShardsForTravel && places.MasterOf(place) is { } master && ui->IsAetheryteUnlocked(master.Id))
            {
                var m = master.Id;
                routes.Add(new Route(Config.TeleportCost + Config.AethernetCost + rest, $"teleport to {master.Name} then aethernet to {name}",
                    master.Position, () => _p.Ipc.TeleportTo(m), master.Territory, place.Id, pos));
            }
        }

        return routes.Count == 0 ? null : routes.MinBy(r => r.Cost);
    }

    private void OnUpdate(IFramework framework)
    {
        try
        {
            Step();
        }
        catch (Exception e)
        {
            // Never keep throwing every frame: stop the trip and report once.
            Plugin.Log.Error(e, $"{Plugin.Tag} Trip stopped after an error");
            Cancel("stopped after an error");
        }
    }

    private void Step()
    {
        var player = Plugin.ObjectTable.LocalPlayer;
        var c = Plugin.Condition;
        var busy = player == null
                   || c[ConditionFlag.BetweenAreas] || c[ConditionFlag.BetweenAreas51]
                   || c[ConditionFlag.Casting] || c[ConditionFlag.Occupied33] || c[ConditionFlag.OccupiedInEvent];

        // Short grace so a stick still tilted from moving the map cursor at the press doesn't count.
        if (_tripTime.ElapsedMilliseconds > 250 && PlayerIsMoving())
        {
            Plugin.Info($"{Plugin.Tag} You moved: trip cancelled");
            Cancel("cancelled (you moved)");
            return;
        }

        if (_tickTime.ElapsedMilliseconds < StepIntervalMs)
            return;
        _tickTime.Restart();

        // After arriving: a zone change (duty, manual teleport, logout) ends the trip.
        if (_phase != Phase.Arriving && !c[ConditionFlag.BetweenAreas] && !c[ConditionFlag.BetweenAreas51]
            && Plugin.ClientState.TerritoryType != _territory)
        {
            Cancel("cancelled (you changed zone)", quiet: true);
            return;
        }
        if (_phase != Phase.Arriving && player == null)
            return; // loading screen

        switch (_phase)
        {
            case Phase.Arriving:
            {
                if (_phaseTime.Elapsed.TotalSeconds > 60) { Cancel("timed out while teleporting"); return; }
                bool lsBusy;
                try { lsBusy = _p.Ipc.LifestreamBusy(); } catch { lsBusy = false; }
                if (busy || lsBusy || (_arrival != null && _phaseTime.Elapsed.TotalSeconds < 1) || Plugin.ClientState.TerritoryType != _territory)
                    return;
                if (_arrival is { } a && Vector2.Distance(Flat(player!.Position), a) > 60)
                    return; // the teleport has not happened yet

                // Teleported to the city's aetheryte: now take the aethernet to the shard.
                if (_pendingShard != 0)
                {
                    var shard = _pendingShard;
                    _pendingShard = 0;
                    bool ok;
                    try { ok = _p.Ipc.AethernetTo(shard); } catch { ok = false; }
                    if (!ok) { Cancel("failed: could not take the aethernet"); return; }
                    Plugin.Info($"{Plugin.Tag} Arrived at the aetheryte: aethernet to #{shard}");
                    _arrival   = _shardArrival;
                    _territory = _finalTerritory;
                    SetPhase(Phase.Arriving);
                    return;
                }
                if (_endOnArrival)
                {
                    Plugin.Info($"{Plugin.Tag} Arrived");
                    Stop();
                    return;
                }
                _mountTries = 0;
                SetPhase(Phase.Mounting);
                return;
            }

            case Phase.Mounting:
            {
                if (busy) return;
                var far = Vector2.Distance(Flat(player!.Position), _target) > Config.MountDistance;
                if (c[ConditionFlag.Mounted] || !far || !Config.AutoMount || !CanMount() || c[ConditionFlag.InCombat] || _phaseTime.Elapsed.TotalSeconds > 4)
                {
                    SetPhase(Phase.WaitMesh);
                    return;
                }
                // Mount Roulette once, and once more after 1.5 s if it didn't take.
                if (_mountTries == 0 || (_mountTries == 1 && _phaseTime.ElapsedMilliseconds > 1500))
                {
                    _mountTries++;
                    ActionManager.Instance()->UseAction(ActionType.GeneralAction, 9);
                }
                return;
            }

            case Phase.WaitMesh:
            {
                if (busy) return;
                if (_phaseTime.Elapsed.TotalSeconds > 120) { Cancel("timed out waiting for vnavmesh"); return; }
                bool ready;
                try { ready = _p.Ipc.NavReady(); } catch { Cancel("failed: vnavmesh is not available"); return; }
                if (!ready) return;

                var fly = Config.AllowFlying && c[ConditionFlag.Mounted] && CanFly();
                // The marker's NPC/object is already loaded: walk right up to it.
                if (FindMarkerObject() is { } obj)
                {
                    _approached = true;
                    Plugin.Info($"{Plugin.Tag} Moving to {obj.Name} at {obj.Position} ({(fly ? "flying" : "on foot/mount")})");
                    try { _p.Ipc.NavMoveCloseTo(obj.Position, fly, StopDistance(obj)); }
                    catch { Cancel("failed: vnavmesh is not available"); return; }
                    SetPhase(Phase.Moving);
                    return;
                }

                // No map flag: ask vnavmesh for the ground at that spot and send it there directly.
                // With a game marker we know the height, so pick the floor at that height (layered cities).
                Vector3? dest;
                try
                {
                    dest = _targetY is { } y
                        ? _p.Ipc.NavGroundAt(_target.X, _target.Y, 5, y + 3) ?? _p.Ipc.NavGroundAt(_target.X, _target.Y, 25, y + 3)
                        : null;
                    // A plain spot can have several floors (street, walkways, roofs): prefer the floor at
                    // your own height, and only use the highest one if there is nothing at your level.
                    var myLevel = player!.Position.Y + 5;
                    dest ??= _p.Ipc.NavGroundAt(_target.X, _target.Y, 5, myLevel)
                          ?? _p.Ipc.NavGroundAt(_target.X, _target.Y, 5)
                          ?? _p.Ipc.NavGroundAt(_target.X, _target.Y, 25, myLevel)
                          ?? _p.Ipc.NavGroundAt(_target.X, _target.Y, 25);
                }
                catch { Cancel("failed: vnavmesh is not available"); return; }
                if (dest == null) { Cancel("failed: no walkable ground at that spot"); return; }

                Plugin.Info($"{Plugin.Tag} Moving to {dest.Value} ({(fly ? "flying" : "on foot/mount")})");
                try { _p.Ipc.NavMoveTo(dest.Value, fly); }
                catch { Cancel("failed: vnavmesh is not available"); return; }
                SetPhase(Phase.Moving);
                return;
            }

            case Phase.Moving:
            {
                // Keep watching for your own movement until vnavmesh arrives (or gives up).
                bool moving;
                try { moving = _p.Ipc.NavMoving(); } catch { moving = false; }
                if (moving)
                    _navStarted = true;
                if (!moving && _phaseTime.Elapsed.TotalSeconds > 1.5)
                {
                    // Went to the marker but its NPC/object only loaded on the way: walk up to it now.
                    var found = FindMarkerObject();
                    if (found is { } obj)
                    {
                        var objDist = Vector3.Distance(player!.Position, obj.Position);
                        var range = StopDistance(obj);
                        // Already standing in the quest area (e.g. a zone that pops the mobs on you): stay there.
                        var inArea = _radius > 0 && Vector2.Distance(Flat(player.Position), _target) <= _radius;
                        var isMob = obj.ObjectKind == Dalamud.Game.ClientState.Objects.Enums.ObjectKind.BattleNpc;
                        if (!_approached && objDist > range + 1.5f && !(isMob && inArea))
                        {
                            _approached = true;
                            Plugin.Info($"{Plugin.Tag} {obj.Name} is loaded now: moving to {range:0} y from it");
                            try { _p.Ipc.NavMoveCloseTo(obj.Position, c[ConditionFlag.InFlight], range); } catch { /* vnavmesh gone */ }
                            _navStarted = false;
                            SetPhase(Phase.Moving);
                            return;
                        }
                    }

                    // Close enough to talk to the NPC / use the object: target it (never enemies).
                    if (Config.TargetOnArrival && found is { } near && IsFriendlyInteractable(near))
                    {
                        if (InInteractRange(player!, near))
                        {
                            Plugin.TargetManager.Target = near;
                            Plugin.Info($"{Plugin.Tag} Targeted {near.Name} ({Vector3.Distance(player!.Position, near.Position):0.0} y)");
                        }
                        else
                            Plugin.Info($"{Plugin.Tag} Not targeting {near.Name}: too far to interact ({Vector3.Distance(player!.Position, near.Position):0.0} y)");
                    }

                    Stop();
                    var left = Vector2.Distance(Flat(player!.Position), _target);
                    Plugin.Info(_navStarted
                        ? $"{Plugin.Tag} vnavmesh stopped after {_phaseTime.Elapsed.TotalSeconds:0.0} s, {left:0.0} y from the target"
                        : $"{Plugin.Tag} vnavmesh never started moving ({left:0.0} y from the target): no path found?");
                }
                return;
            }
        }
    }

    private const float ApproachRange = 2.5f;
    private const float OnMarkerWithin = 4f;

    private static bool IsFriendlyInteractable(Dalamud.Game.ClientState.Objects.Types.IGameObject o)
        => o.IsTargetable && o.ObjectKind is Dalamud.Game.ClientState.Objects.Enums.ObjectKind.EventNpc
                                          or Dalamud.Game.ClientState.Objects.Enums.ObjectKind.EventObj;

    /// <summary>Within the game's talk/use distance (NPCs ~6 yalms, objects ~4.5, plus their size).</summary>
    private static bool InInteractRange(Dalamud.Game.ClientState.Objects.Types.IGameObject player, Dalamud.Game.ClientState.Objects.Types.IGameObject o)
    {
        var max = o.ObjectKind == Dalamud.Game.ClientState.Objects.Enums.ObjectKind.EventNpc ? 6f : 4.5f;
        return Vector3.Distance(player.Position, o.Position) - o.HitboxRadius <= max;
    }

    /// <summary>Marker without an NPC/object id: the friendly NPC or object standing right on the marker.</summary>
    private Dalamud.Game.ClientState.Objects.Types.IGameObject? ObjectOnMarker()
    {
        if (_targetY == null || _radius > OnMarkerWithin + 1)
            return null; // not a game marker, or an area
        var marker = new Vector3(_target.X, _targetY.Value, _target.Y);
        Dalamud.Game.ClientState.Objects.Types.IGameObject? best = null;
        var bestDist = OnMarkerWithin;
        foreach (var o in Plugin.ObjectTable)
        {
            if (!IsFriendlyInteractable(o))
                continue;
            var d = Vector3.Distance(o.Position, marker);
            if (d < bestDist)
            {
                bestDist = d;
                best = o;
            }
        }
        return best;
    }

    /// <summary>Walk right up to NPCs and objects; stay at a safe distance from mobs. Nothing is targeted.</summary>
    private float StopDistance(Dalamud.Game.ClientState.Objects.Types.IGameObject obj)
    {
        if (obj is not Dalamud.Game.ClientState.Objects.Types.IBattleNpc mob)
            return ApproachRange;
        // Far below your level, mobs don't aggro: walk right up to them (melee range).
        var player = Plugin.ObjectTable.LocalPlayer;
        if (Config.IgnoreLowLevelMobs && player != null && player.Level >= mob.Level + Config.NoAggroLevelGap)
            return mob.HitboxRadius + 2f;
        return Config.MobDistance;
    }

    /// <summary>
    /// The NPC, object or mob the quest marker points at, if it is loaded. Inside a quest area, the one nearest
    /// to you; otherwise the one nearest to the marker. Mobs must be alive, everything must be targetable.
    /// </summary>
    private Dalamud.Game.ClientState.Objects.Types.IGameObject? FindMarkerObject()
    {
        if (_dataId == 0)
            return ObjectOnMarker();
        var player = Plugin.ObjectTable.LocalPlayer;
        var searchRadius = MathF.Max(60f, _radius + 20f);
        var inArea = player != null && _radius > 0 && Vector2.Distance(Flat(player.Position), _target) <= _radius + 10f;
        var from = inArea ? Flat(player!.Position) : _target;

        Dalamud.Game.ClientState.Objects.Types.IGameObject? best = null;
        var bestDist = float.MaxValue;
        foreach (var o in Plugin.ObjectTable)
        {
            if (!o.IsTargetable || Vector2.Distance(Flat(o.Position), _target) > searchRadius)
                continue;
            var match = o.ObjectKind switch
            {
                Dalamud.Game.ClientState.Objects.Enums.ObjectKind.EventNpc or
                Dalamud.Game.ClientState.Objects.Enums.ObjectKind.EventObj => o.BaseId == _dataId,
                // Kill objectives: the marker may name the mob by its base id or its name id.
                Dalamud.Game.ClientState.Objects.Enums.ObjectKind.BattleNpc =>
                    o is Dalamud.Game.ClientState.Objects.Types.IBattleNpc b && !b.IsDead && b.CurrentHp > 0
                    && (b.BaseId == _dataId || b.NameId == _dataId),
                _ => false,
            };
            if (!match)
                continue;
            var d = Vector2.Distance(Flat(o.Position), from);
            if (d < bestDist)
            {
                bestDist = d;
                best = o;
            }
        }
        return best;
    }

    /// <summary>True when you push the left stick or a movement key yourself (vnavmesh moves you without either).</summary>
    private static bool PlayerIsMoving()
    {
        // The left stick moves your character even with the map open (it takes focus away from the map).
        if (Plugin.Gamepad.LeftStick.Length() > 30) // stick range is -99..99
            return true;

        var atk = RaptureAtkModule.Instance();
        if ((atk != null && atk->AtkModule.IsTextInputActive()) || Dalamud.Bindings.ImGui.ImGui.GetIO().WantTextInput)
            return false; // typing in chat or in a plugin window
        foreach (var k in MoveKeys)
            if (Plugin.KeyState[k])
                return true;
        return false;
    }

    private static Vector2 Flat(Vector3 v) => new(v.X, v.Z);

    private static bool CanMount()
        => Plugin.Data.GetExcelSheet<TerritoryType>().TryGetRow(Plugin.ClientState.TerritoryType, out var t) && t.Mount;

    private static bool CanFly()
    {
        if (!Plugin.Data.GetExcelSheet<TerritoryType>().TryGetRow(Plugin.ClientState.TerritoryType, out var t))
            return false;
        var set = t.AetherCurrentCompFlgSet.RowId;
        return set != 0 && PlayerState.Instance()->IsAetherCurrentZoneComplete(set);
    }
}
