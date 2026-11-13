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
    private enum Phase { None, Arriving, WaitMesh, Moving }

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
    private Vector2 _areaCenter;   // the quest's area (circle), when known: searches stay inside it
    private string _pressTooltip = "";
    private uint _pressMapId;
    private float _pressIconWithin;
    private bool _refined;         // the marker is exact already, or was looked up again in its zone
    private bool _hasMarker;       // a game marker was clicked (not a plain spot)
    private long _refineSince, _refineAt;
    private float _areaRadius;
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

    /// <summary>
    /// For Giddy Up: true during a trip unless the current leg is on foot (moving, no mount planned). Before the
    /// move starts (teleporting, waiting for vnavmesh) the leg isn't decided yet, so it counts as "will mount".
    /// </summary>
    public bool WillMount => _phase != Phase.None && (_phase != Phase.Moving || _wantMount
                             || Plugin.Condition[ConditionFlag.Mounted] || Plugin.Condition[ConditionFlag.RidingPillion]);

    /// <summary>Giddy Up uses Sprint / Peloton itself (each asked when the trip starts): Beeline then leaves that one alone.</summary>
    private bool _guSprint, _guPeloton;
    /// <summary>Sprint / Peloton will be used on foot, by Beeline or by Giddy Up (for time estimates).</summary>
    private bool SprintOn  => Config.SpeedBuffs || _guSprint;
    private bool PelotonOn => Config.SpeedBuffs || _guPeloton;
    public string Status => _phase switch
    {
        Phase.Arriving => "Teleporting...",
        Phase.WaitMesh => "Waiting for vnavmesh...",
        Phase.Moving   => "Moving there...",
        _ => "",
    };

    public void Dispose()
    {
        if (_phase != Phase.None)
            try { _p.Ipc.NavStop(); } catch { /* vnavmesh missing */ }
        Stop();
        RemoveTimer();
    }

    /// <param name="world">The spot under the mouse, worked out from the map's view (precise, works on an unfocused map);
    /// null to use the game's cursor readout (<paramref name="mapCoords"/>).</param>
    public void Start(uint territory, uint mapId, Vector2 mapCoords, string tooltip, float iconWithin, bool cursorStale = false, Vector2? world = null)
    {
        if (!Plugin.Data.GetExcelSheet<Lumina.Excel.Sheets.Map>().TryGetRow(mapId, out var map) || territory == 0)
            return;

        var target = world ?? new Vector2(
            Places.MapToWorld(mapCoords.X, map.SizeFactor, map.OffsetX),
            Places.MapToWorld(mapCoords.Y, map.SizeFactor, map.OffsetY));

        // A quest (or other game) marker at the cursor gives the exact spot, height and NPC/object.
        var marker = QuestMarkers.Find(territory, mapId, target, tooltip, iconWithin, cursorReliable: world != null);
        if (marker is { } mk)
        {
            Plugin.Info($"{Plugin.Tag} Marker \"{Plugin.Redact(mk.Label)}\" at {mk.Position} (radius {mk.Radius:0}, object {mk.DataId}"
                        + (mk.AreaCenter is { } ac ? $", in its quest area at ({ac.X:0}, {ac.Y:0}) radius {mk.AreaRadius:0}" : "")
                        + $"), {Vector2.Distance(target, new Vector2(mk.Position.X, mk.Position.Z)):0.0} y from the cursor");
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

        Plugin.Info($"{Plugin.Tag} Travel to ({mapCoords.X:0.0}, {mapCoords.Y:0.0}) \"{Plugin.Redact(tooltip)}\" in territory {territory}: {route.Value.Describe}");
        var r = route.Value;
#if DEVTOOLS
        _testRoute = r.ThenShard != 0 ? "teleport+aethernet" : r.FirstHop switch { Hop.Teleport => "teleport", Hop.Aethernet => "aethernet", _ => "direct" };
        _testMarker = marker?.Label ?? "";
#endif
        _tripEstimate = r.Cost;
        _territory  = r.ThenShard != 0 ? r.FirstTerritory : territory;
        _finalTerritory = territory;
        _pendingShard   = r.ThenShard;
        _shardArrival   = r.ThenArrival;
        _endOnArrival   = false;
        _target     = target;
        _arrival    = r.Arrival;
        _targetY    = marker is { HasHeight: true } withHeight ? withHeight.Position.Y : null;
        _dataId     = marker?.DataId ?? 0;
        _radius     = marker?.Radius ?? 0;
        _pressTooltip = tooltip;
        _hasMarker    = marker != null;
        _refineSince  = 0;
        _pressMapId   = mapId;
        _pressIconWithin = iconWithin;
        _refined      = marker is { HasHeight: true, DataId: not 0 };
        // The quest's area (circle): the marker itself if it is one, else the area its icon belongs to (if any).
        (_areaCenter, _areaRadius) = marker switch
        {
            { Radius: > OnMarkerWithin + 1 } m => (new Vector2(m.Position.X, m.Position.Z), m.Radius),
            { AreaCenter: { } c } m => (c, m.AreaRadius),
            not null when QuestMarkers.FindArea(territory, target) is { } a => (a.Center, a.Radius),
            _ => (Vector2.Zero, 0f),
        };
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
        _hop = r.FirstHop;
        _hopTime.Restart();
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
#if DEVTOOLS
        _testRoute      = "teleport+aethernet";
        _testMarker     = "";
#endif
        _target         = shard.Position ?? default;
        _targetY        = null;
        _dataId         = 0;
        _radius         = 0;
        _navStarted     = false;
        _tripEstimate   = Config.TeleportSeconds + Config.AethernetSeconds;
        _hop            = Hop.Teleport;
        _hopTime.Restart();
        _tripTime.Restart();
        _tickTime.Restart();
        SetPhase(Phase.Arriving);
        Plugin.Framework.Update += OnUpdate;
    }

    public void Cancel(string why, bool quiet = false)
    {
        if (_phase == Phase.None) return;
        if (!quiet) Plugin.Chat.Print($"Beeline: trip {why}.");
        Plugin.Info($"Trip {why}"); // every end of a trip is in the log, with its reason
#if DEVTOOLS
        _testLast = why;
#endif
        var wasTeleporting = _phase == Phase.Arriving && _arrival != null;
        Stop();
        try { _p.Ipc.NavStop(); } catch { /* vnavmesh missing */ }
        if (wasTeleporting)
            try { _p.Ipc.LifestreamAbort(); } catch { /* Lifestream missing */ }
    }

    private void Stop()
    {
        HideTimer();
        var wasActive = _phase != Phase.None;
        if (wasActive)
            Plugin.Framework.Update -= OnUpdate;
        _phase = Phase.None;
        _pathTask = null;
        _heldPath = null;
        // Learned timings: one write to disk per trip, not one per measurement.
        if (_timingsChanged)
        {
            _timingsChanged = false;
            _p.SaveConfig();
        }
        if (wasActive)
            _p.Companion?.TripEnded();
    }

    private bool _timingsChanged;

    // ---------------- Time left, in the server info bar (option) ----------------

    private float _tripEstimate;                         // the chosen route's estimate when the trip started (s)
    private Dalamud.Game.Gui.Dtr.IDtrBarEntry? _timerEntry;
    private long _timerAt;

    /// <summary>
    /// Once a second, with the option on: teleporting or waiting, the route's estimate minus the time spent; moving,
    /// recomputed from the distance left at your learned speeds (flying, riding, or on foot with a mount on its way).
    /// </summary>
    private void UpdateTimer(Dalamud.Game.ClientState.Objects.SubKinds.IPlayerCharacter? player)
    {
        if (!Config.ShowTripTimer)
        {
            HideTimer();
            return;
        }
        var now = Environment.TickCount64;
        if (now - _timerAt < 1000)
            return;
        _timerAt = now;
        var left = _tripEstimate - (float)_tripTime.Elapsed.TotalSeconds;
        if (_phase == Phase.Moving && player != null)
        {
            var flat = Math.Max(0f, Vector2.Distance(Flat(player.Position), Flat(_dest)) - _destRange);
            var c = Plugin.Condition;
            if (c[ConditionFlag.InFlight])
                left = Math.Max(0f, Vector3.Distance(player.Position, _dest) - _destRange) / Math.Max(1f, Config.FlySpeed);
            else if (Mounted())
                left = MountedSeconds(flat, _territory, summon: false);
            else
                left = _wantMount ? MountedSeconds(flat, _territory, summon: true) : FootSeconds(flat * Math.Max(1f, Config.Detour), sameZone: true);
        }
        var s = (int)MathF.Ceiling(Math.Max(0f, left));
        try
        {
            _timerEntry ??= Plugin.DtrBar.Get("Beeline");
            _timerEntry.Text = $"Beeline {s / 60}:{s % 60:00}";
            _timerEntry.Tooltip = "Time left for the Beeline trip (estimate from your learned speeds)";
            _timerEntry.Shown = true;
        }
        catch (Exception e)
        {
            Plugin.Log.Debug(e, "Trip timer unavailable");
        }
    }

    private void HideTimer()
    {
        if (_timerEntry != null)
            _timerEntry.Shown = false;
        _timerAt = 0;
    }

    /// <summary>Removes the info bar entry (plugin unload).</summary>
    internal void RemoveTimer()
    {
        _timerEntry?.Remove();
        _timerEntry = null;
    }

    private nint _tripTarget; // target when last checked (raw pointer: nothing else to do until it changes)
    private FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject* _tripEnemy; // targeted enemy, not yet in range
    private float _enemyStopRange;  // stop once it's this close (edge to edge); from Giddy Up's settings
    private bool _giddyUpDismounts; // Giddy Up dismounts you for enemies (asked when you target one)

    /// <summary>
    /// "Stop the trip when you target an enemy": with Giddy Up, once the enemy is within your attack range as Giddy Up
    /// works it out from its settings (melee option included), or at once if it's set to "as soon as you target it".
    /// Without Giddy Up, at once. Mounted with Giddy Up's enemy dismount on, Giddy Up gets you off in range (landing
    /// early when flying) and that ends the trip. Checked 10 times a second; a pointer compare unless an enemy is
    /// targeted.
    /// </summary>
    private bool EnemyStopsTrip()
    {
        var raw = (nint)FFXIVClientStructs.FFXIV.Client.Game.Control.TargetSystem.Instance()->Target;
        if (raw != _tripTarget)
        {
            _tripTarget = raw;
            _tripEnemy = null;
            var t = (FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)raw;
            if (t == null || t->ObjectKind != FFXIVClientStructs.FFXIV.Client.Game.Object.ObjectKind.BattleNpc
                || t->IsDead() || !ActionManager.CanUseActionOnTarget(7, t))
                return false;
            _enemyStopRange = _p.Companion?.GiddyUpEnemyStopRange() ?? -1f; // asked now: current settings
            _giddyUpDismounts = _p.Companion?.GiddyUpEnemyDismount() == true;
            if (_enemyStopRange < 0)
                return true;
            _tripEnemy = t;
            Plugin.Info($"{Plugin.Tag} Enemy targeted: the trip stops once it's within {_enemyStopRange:0} y");
        }
        if (_tripEnemy == null)
            return false;
        if (_tripEnemy->IsDead())
        {
            _tripEnemy = null;
            return false;
        }
        var c = Plugin.Condition;
        if (_giddyUpDismounts && (c[ConditionFlag.Mounted] || c[ConditionFlag.RidingPillion]))
            return false; // Giddy Up dismounts you in range, which ends the trip
        var me = FFXIVClientStructs.FFXIV.Client.Game.Control.Control.GetLocalPlayer();
        if (me == null)
            return false;
        var gap = Vector3.Distance(me->Position, _tripEnemy->Position) - me->HitboxRadius - _tripEnemy->HitboxRadius;
        return gap <= _enemyStopRange;
    }

    private void SetPhase(Phase phase)
    {
        if (_phase == Phase.None && phase != Phase.None)
        {
            _guSprint  = _p.Companion?.GiddyUpSprint() == true;
            _guPeloton = _p.Companion?.GiddyUpPeloton() == true;
            _tripTarget = (nint)FFXIVClientStructs.FFXIV.Client.Game.Control.TargetSystem.Instance()->Target; // already targeted: doesn't count
            _tripEnemy = null;
            _p.Companion?.TripStarted();
#if DEVTOOLS
            _testTrips++;
            _testLast = "";
            _testLastLeft = -1;
#endif
        }
        _phase = phase;
        _phaseTime.Restart();
    }

    /// <summary>
    /// A way to get there. Teleport + aethernet routes first teleport to <see cref="FirstTerritory"/>, then, once
    /// arrived, take the aethernet to <see cref="ThenShard"/> (two steps with Lifestream's regular calls).
    /// </summary>
    private readonly record struct Route(float Cost, string Describe, Vector2? Arrival, Func<bool> Go, Hop FirstHop,
                                         uint FirstTerritory = 0, uint ThenShard = 0, Vector2? ThenArrival = null);

    private enum Hop { None, Teleport, Aethernet }
    private enum MoveMode { Run, Peloton, Sprint, Jog, Ride, Fly }

    private Route? BestRoute(uint territory, Vector2 target, Vector2 here)
    {
        var places = _p.Places;
        if (places == null)
            return null;
        var routes = new List<Route>();
        var ui = UIState.Instance();

        var mounted = Plugin.Condition[ConditionFlag.Mounted];
        if (Plugin.ClientState.TerritoryType == territory)
            routes.Add(new Route(LegSeconds(Vector2.Distance(here, target), territory, mounted), "go directly", null, () => true, Hop.None));

        if (!Plugin.IsPluginLoaded("Lifestream") || ui == null)
            return routes.Count == 0 ? null : routes[0];

        uint activeId = 0;
        try { activeId = _p.Ipc.ActiveAetheryte(); } catch { /* ignore */ }
        var active = activeId != 0 ? places.ById(activeId) : null;

        uint activeResidential = 0;
        try { activeResidential = _p.Ipc.ActiveResidential(); } catch { /* ignore */ }
        var atResidential = activeResidential != 0 ? places.Residential(activeResidential) : null;

        // Jog survives a teleport / aethernet hop that stays on the same map, not a change of map.
        var current = Plugin.ClientState.TerritoryType;
        foreach (var place in places.InTerritory(territory))
        {
            if (place.Position is not { } pos)
                continue;
            if (place.Residential)
            {
                // Housing district shards: free hops, only from a shard of the same district.
                if (Config.UseShardsForTravel && atResidential != null && atResidential.Group == place.Group && atResidential.Id != place.Id)
                {
                    var hid = place.Id;
                    _legAfterHop = territory != current;
                    routes.Add(new Route(Config.AethernetSeconds + LegSeconds(Vector2.Distance(pos, target), territory, alreadyMounted: false),
                        $"aethernet to {place.Name}", pos, () => _p.Ipc.ResidentialAethernetTo(hid), Hop.Aethernet));
                }
                continue;
            }
            if (!ui->IsAetheryteUnlocked(place.Id))
                continue;
            var restDistance = Vector2.Distance(pos, target);
            _legAfterHop = territory != current;
            var rest = LegSeconds(restDistance, territory, alreadyMounted: false);
            var name = place.Name;

            if (Config.UseShardsForTravel && active != null && !place.IsMain && place.Group != 0 && place.Group == active.Group && place.Id != active.Id)
            {
                var id = place.Id;
                routes.Add(new Route(Config.AethernetSeconds + rest, $"aethernet to {name}", pos, () => _p.Ipc.AethernetTo(id), Hop.Aethernet));
            }

            // Paid teleports to the map you're already on only if allowed (the aethernet above is free and always allowed).
            var paidHopAllowed = Config.SameMapTeleport || territory != current;
            if (place.IsMain && paidHopAllowed)
            {
                var id = place.Id;
                routes.Add(new Route(Config.TeleportSeconds + rest, $"teleport to {name}", pos, () => _p.Ipc.TeleportTo(id), Hop.Teleport));
            }
            else if (!place.IsMain && paidHopAllowed && Config.UseShardsForTravel && places.MasterOf(place) is { } master && ui->IsAetheryteUnlocked(master.Id))
            {
                var m = master.Id;
                _legAfterHop = territory != current || master.Territory != current;
                rest = LegSeconds(restDistance, territory, alreadyMounted: false);
                routes.Add(new Route(Config.TeleportSeconds + Config.AethernetSeconds + rest, $"teleport to {master.Name} then aethernet to {name}",
                    master.Position, () => _p.Ipc.TeleportTo(m), Hop.Teleport, master.Territory, place.Id, pos));
            }
        }

        _legAfterHop = false;
        if (routes.Count == 0)
            return null;
        if (Plugin.Config_LogPresses)
            foreach (var r in routes.OrderBy(r => r.Cost).Take(6))
                Plugin.Info($"{Plugin.Tag}   option: {r.Describe}: ~{r.Cost:0.0} s");
        return routes.MinBy(r => r.Cost);
    }

    /// <summary>
    /// Seconds to cover a straight-line distance once there: on foot when it's short, otherwise riding or flying
    /// (the mount is summoned while you keep moving, so it costs no extra time), at the speeds learned from your trips.
    /// </summary>
    private float LegSeconds(float distance, uint territory, bool alreadyMounted)
    {
        var sameZone = territory == Plugin.ClientState.TerritoryType && !_legAfterHop;
        if (alreadyMounted)
            return MountedSeconds(distance, territory, summon: false);
        var foot = FootSeconds(distance * Math.Max(1f, Config.Detour), sameZone);
        if (!Config.AutoMount || !CanMount(territory))
            return foot;
        return Math.Min(foot, MountedSeconds(distance, territory, summon: true, sameZone));
    }

    /// <summary>
    /// Seconds riding or flying a straight-line distance. With <paramref name="summon"/>, you first keep going on foot
    /// while the mount is summoned (no standing still), then ride or fly the rest.
    /// </summary>
    private float MountedSeconds(float distance, uint territory, bool summon, bool sameZone = true)
    {
        var fly = Config.AllowFlying && CanFly(territory);
        var speed = Math.Max(1f, fly ? Config.FlySpeed : Config.RideSpeed);
        var detour = fly ? 1f : Math.Max(1f, Config.Detour); // flying goes (almost) straight
        if (!summon)
            return distance * detour / speed;
        var footSpeed = FootSeconds(100f, sameZone) is var t && t > 0 ? 100f / t : Config.RunSpeed; // your speed on foot right now
        var whileSummoning = footSpeed * Config.MountSeconds / Math.Max(1f, Config.Detour);         // straight-line ground covered meanwhile
        if (distance <= whileSummoning)
            return distance * Math.Max(1f, Config.Detour) / footSpeed;                            // there before the mount is
        return Config.MountSeconds + (distance - whileSummoning) * detour / speed;
    }

    /// <summary>Summoning a mount gets you there sooner than staying on foot.</summary>
    private bool MountIsFaster(float distance, uint territory)
        => Config.AutoMount && CanMount(territory)
           && MountedSeconds(distance, territory, summon: true) < FootSeconds(distance * Math.Max(1f, Config.Detour), sameZone: true) - 0.3f;

    private bool _legAfterHop; // set while estimating legs that start after a change of map (that ends Jog; same-map hops keep it)

    /// <summary>
    /// Seconds on foot for a path length: at Jog speed if you're jogging and staying in the zone; otherwise Sprint
    /// (if ready) for its duration, then Jog; otherwise Peloton (if your job keeps it up) or plain running.
    /// </summary>
    private float FootSeconds(float path, bool sameZone)
    {
        var player = Plugin.ObjectTable.LocalPlayer;
        var basic = Math.Max(1f, CanKeepPeloton() ? Math.Max(Config.PelotonSpeed, Config.RunSpeed) : Config.RunSpeed);
        if (sameZone && player != null && player.StatusList.Any(st => st.StatusId == JogStatus))
            return path / Math.Max(basic, Config.JogSpeed);
        if (SprintOn && SprintReady())
        {
            var sprint = Math.Max(1f, Config.SprintSpeed);
            var sprintPath = sprint * Config.SprintSeconds;
            if (path <= sprintPath)
                return path / sprint;
            return Config.SprintSeconds + (path - sprintPath) / Math.Max(basic, Config.JogSpeed);
        }
        return path / basic;
    }

    private static bool SprintReady()
        => ActionManager.Instance()->GetActionStatus(ActionType.GeneralAction, SprintGeneralAction) == 0;

    /// <summary>
    /// Physical ranged jobs (Archer, Bard, Machinist, Dancer) learn Peloton at level 20 and can keep it up the
    /// whole way out of combat, so on-foot legs are estimated at Peloton speed for them.
    /// </summary>
    private bool CanKeepPeloton()
    {
        var player = Plugin.ObjectTable.LocalPlayer;
        return PelotonOn && player != null && player.Level >= 20 && !Plugin.Condition[ConditionFlag.InCombat]
               && player.ClassJob.RowId is 5 or 23 or 31 or 38;
    }

    // ---------------- Learning from your trips ----------------

    private Hop _hop = Hop.None;                       // teleport / aethernet being timed
    private readonly Stopwatch _hopTime = new();
    private Vector2 _moveStart;                        // where vnavmesh started moving you
    private readonly Stopwatch _moveTime = new();

    /// <summary>Running average that settles quickly at first, then keeps adapting a little.</summary>
    private static float Blend(float old, int samples, float sample)
        => old + (sample - old) * MathF.Max(0.25f, 1f / (samples + 1));

    private void LearnHop()
    {
        var seconds = (float)_hopTime.Elapsed.TotalSeconds;
        switch (_hop)
        {
            case Hop.Teleport when seconds is > 3 and < 45:
                Config.TeleportSeconds = Blend(Config.TeleportSeconds, Config.TeleportSamples++, seconds);
                Plugin.Info($"{Plugin.Tag} Teleport took {seconds:0.0} s (average now {Config.TeleportSeconds:0.0} s)");
                _timingsChanged = true; // saved once, when the trip ends
                break;
            case Hop.Aethernet when seconds is > 2 and < 30:
                Config.AethernetSeconds = Blend(Config.AethernetSeconds, Config.AethernetSamples++, seconds);
                Plugin.Info($"{Plugin.Tag} Aethernet took {seconds:0.0} s (average now {Config.AethernetSeconds:0.0} s)");
                _timingsChanged = true; // saved once, when the trip ends
                break;
        }
        _hop = Hop.None;
    }

    private void StartMoving(Dalamud.Game.ClientState.Objects.Types.IGameObject player, Vector3 dest, float range, bool fly, bool wantMount)
    {
        _moveStart = Flat(player.Position);
        _moveTime.Restart();
        _dest = dest;
        _destRange = range;
        _flying = fly;
        _wantMount = wantMount;
        _mountTries = 0;
        _mountAttempts = 0;
        _boostFails = 0;
        _sprintUsed = false;
        _sawSprint = _sawPeloton = false;
        _allowRemount = false;
        _heldPath = null;
        _noFly = false;
        _walkAfter = null;
        _noWalk = false;
        _mountCastClock.Reset();
        _stuckStage = 0;
        _stuckPos = player.Position;
        _stuckTime.Restart();
        _wasMounted = Plugin.Condition[ConditionFlag.Mounted] || Plugin.Condition[ConditionFlag.RidingPillion];
        _wasInFlight = Plugin.Condition[ConditionFlag.InFlight];
        Array.Clear(_bucketDist);
        Array.Clear(_bucketTime);
        _pathLength = 0;
        _lastSample = player.Position;
        _sampleClock.Restart();
        _sawFlying = fly;
        _tookOff = Plugin.Condition[ConditionFlag.InFlight];
        _jumpPresses = 0;
        _lastPressInAir = false;
        _mountedAt = 0;
        _inFlightSince = 0;
        _sawMounted = Plugin.Condition[ConditionFlag.Mounted];
    }

    private Vector3 _dest;        // where vnavmesh is taking you
    private float _destRange;     // stop this close (0 = exactly there)
    private bool _flying;         // vnavmesh was asked to fly
    private bool _noFly;          // flying failed on this move (no path, can't take off, no mount): stay on the ground
    private Vector3? _walkAfter;  // a covered spot: first to the open sky next to it, then on in to this
    private bool _noWalk;         // no walkable ground ahead while the mount comes: stay put for this move
    private bool _wantMount;      // summon a mount on the way
    private bool _sawFlying, _sawMounted;
    private int _mountAttempts;   // calls refused by the game (e.g. just after an instant action)
    private int _boostFails;              // speed boost calls refused (nothing ready): stop after a while
    private const uint JogStatus = 4209;

    // Stuck watchdog: a trip must never hang (e.g. another plugin dismounts you while vnavmesh waits to take off).
    private int _stuckStage;
    private Vector3 _stuckPos;
    private readonly Stopwatch _stuckTime = new();
    private bool _wasMounted, _wasInFlight;
    private const int StuckMs = 3000;

    /// <summary>
    /// Called while vnavmesh has a path. If you haven't moved for 5 s (and nothing legit is going on: path still being
    /// computed, a cast, getting on a mount, waiting for the first mount), steps in: re-mount once if you got dismounted on
    /// a flying path, then go on the ground instead, then re-send the path once, then stop the trip. Returns true if it acted.
    /// </summary>
    private bool Watchdog(Dalamud.Game.ClientState.Objects.SubKinds.IPlayerCharacter player, ICondition c)
    {
        var mounted = c[ConditionFlag.Mounted] || c[ConditionFlag.RidingPillion];
        var computing = _pathTask != null || _heldPath != null;
        var legitWait = computing || c[ConditionFlag.Casting] || c[ConditionFlag.Casting87] || c[ConditionFlag.Mounting]
                        || c[ConditionFlag.Mounting71] || c[ConditionFlag.MountOrOrnamentTransition]
                        || (_flying && !mounted && !_sawMounted); // waiting for the first mount (has its own fallback)
        if (legitWait || Vector3.Distance(player.Position, _stuckPos) > 1f)
        {
            _stuckPos = player.Position;
            _stuckTime.Restart();
            return false;
        }
        if (_stuckTime.ElapsedMilliseconds < StuckMs)
            return false;
        _stuckTime.Restart();

        var here = Plugin.ClientState.TerritoryType;
        if (_flying && !mounted && _stuckStage == 0 && Config.AutoMount && CanMount(here) && !c[ConditionFlag.InCombat])
        {
            _stuckStage = 1;
            Plugin.Info($"{Plugin.Tag} Stuck: dismounted before taking off, mounting again");
            SummonMount();
            return true;
        }
        if (_flying && mounted && _stuckStage <= 1)
        {
            _stuckStage = 2;
            Plugin.Info($"{Plugin.Tag} Stuck in the air: computing the route again from here");
            try { _p.Ipc.NavStop(); SendMove(true, bridge: false); } catch { /* vnavmesh gone */ }
            return true;
        }
        if (_flying && _stuckStage <= 2)
        {
            _stuckStage = 3;
            _flying = false;
            _noFly = true;
            Plugin.Info($"{Plugin.Tag} Stuck: can't take off, going on the ground instead");
            try { _p.Ipc.NavStop(); SendMove(false); } catch { /* vnavmesh gone */ }
            return true;
        }
        if (_stuckStage <= 3)
        {
            _stuckStage = 4;
            Plugin.Info($"{Plugin.Tag} Stuck: asking vnavmesh for the path again");
            try { _p.Ipc.NavStop(); SendMove(_flying); } catch { /* vnavmesh gone */ }
            return true;
        }
        Plugin.Info($"{Plugin.Tag} Stuck: trip stopped");
        Cancel("stopped: stuck, couldn't move for a while");
        return true;
    }

    // Speed sampling: distance and time per way of moving, measured in small slices during the move.
    private readonly float[] _bucketDist = new float[6];
    private readonly float[] _bucketTime = new float[6];
    private float _pathLength;            // ground path actually walked/ridden (for the detour factor)
    private Vector3 _lastSample;
    private readonly Stopwatch _sampleClock = new();
    private bool _sprintUsed;             // Sprint only once per move (long cooldown); Peloton is re-applied
    private bool _sawSprint, _sawPeloton; // boosts seen during this move (for learning the right speed)
    private readonly System.Diagnostics.Stopwatch _sprintClock = new(); // since Sprint was used
    private readonly System.Diagnostics.Stopwatch _sprintReadyClock = new(), _sprintGoneClock = new(); // Giddy Up's Sprint: ready / used since

    private const uint PelotonAction = 7557, SprintGeneralAction = 4, JumpGeneralAction = 2;
    private const uint SprintStatus = 50, PelotonStatus = 1199;

    // ---------------- Moving: compute the path in the background, move meanwhile ----------------

    private System.Threading.Tasks.Task<List<Vector3>>? _pathTask; // path being computed by vnavmesh
    private bool _pathFly;                                         // ...for flying
    private bool _bridging, _bridgeFlying;                         // short move toward the target while it's computed
    private Vector3 _bridgeEnd;
    private List<Vector3>? _heldPath;                              // flying path ready, waiting for the mount to settle
    private bool _allowRemount;                                    // the watchdog may cast the mount once more
    private readonly Stopwatch _mountCastClock = new();            // since Beeline cast Mount Roulette (learns MountSeconds)
    private static bool _warnedKnockOff;                           // chat explanation shown once per session

    /// <summary>On the mount: ready to take off right away (Beeline doesn't slow down to suit other plugins).</summary>
    private static bool MountSettled() => Mounted();
    private readonly Stopwatch _pathClock = new();

    /// <summary>
    /// Asks vnavmesh for the path without waiting for it: long flying paths take several seconds to compute, so you
    /// start toward the target right away (a few steps on foot while the mount is summoned, or taking off once mounted),
    /// and the path takes over from where you are when it's ready.
    /// </summary>
    private void SendMove(bool fly, bool bridge = true)
    {
        var player = Plugin.ObjectTable.LocalPlayer;
        if (player == null)
            return;
        _pathFly = fly;
        _pathClock.Restart();
        _pathFrom = player.Position;
        _pathTask = _p.Ipc.NavPathfind(player.Position, _dest, fly, _destRange);
        _bridging = _bridgeFlying = false;
        if (fly && bridge)
            Bridge(player);
    }

    private Vector3 _pathFrom; // where you were when the path was asked for

    /// <summary>How far you are from the route (closest distance to any of its segments).</summary>
    private static float DistanceToPath(List<Vector3> points, Vector3 pos)
    {
        var best = Vector3.Distance(points[0], pos);
        for (var i = 1; i < points.Count; i++)
        {
            var a = points[i - 1];
            var ab = points[i] - a;
            var len2 = ab.LengthSquared();
            var t = len2 > 0 ? Math.Clamp(Vector3.Dot(pos - a, ab) / len2, 0f, 1f) : 0f;
            best = Math.Min(best, Vector3.Distance(a + ab * t, pos));
        }
        return best;
    }

    private void Bridge(Dalamud.Game.ClientState.Objects.Types.IGameObject player)
    {
        var to = Flat(_dest) - Flat(player.Position);
        if (to.Length() < 5)
            return;
        var dir = Vector2.Normalize(to);
        var mounted = Plugin.Condition[ConditionFlag.Mounted] || Plugin.Condition[ConditionFlag.RidingPillion];
        var pos = player.Position;
        if (mounted && !MountSettled())
            return; // getting on the mount: don't jump yet, keep the current little walk
        // Mounted but not in the air yet: Beeline takes off itself (TakeOff). A flying move given to vnavmesh on the
        // ground makes it spam Jump, so meanwhile keep going on the ground (below): no stop while the jumps happen.
        var takingOff = mounted && !Plugin.Condition[ConditionFlag.InFlight] && !TakeOffGaveUp();
        if (mounted && !takingOff)
        {
            // Take off and head up and toward the target, far enough to keep going until the path is ready.
            var step = Math.Min(Math.Clamp(Config.FlySpeed * 2.5f, 30f, 150f), to.Length());
            _bridgeEnd = new Vector3(pos.X + dir.X * step, pos.Y + 5, pos.Z + dir.Y * step);
            _p.Ipc.NavFollow([_bridgeEnd], true);
            _bridgeFlying = true;
        }
        else
        {
            // A few steps toward the target while the mount is summoned, only as far as there's walkable ground at
            // about your height (a straight line knows nothing of water or drops: summoned in the water, the mount
            // is gone at once). No ground ahead: you stay put and the mount comes where you stand.
            var step = Math.Min(Math.Clamp(Config.RunSpeed * 1.5f, 8f, 40f), to.Length());
            if (_noWalk)
                return;
            var safe = WalkableAhead(pos, dir, step);
            if (safe < 2f)
            {
                _noWalk = true; // decided for this leg: no lookups or log lines every step while the mount comes
                Plugin.Info($"{Plugin.Tag} No walkable ground ahead (water or a drop): waiting for the mount here");
                return;
            }
            _bridgeEnd = new Vector3(pos.X + dir.X * safe, pos.Y, pos.Z + dir.Y * safe);
            _p.Ipc.NavFollow([_bridgeEnd], false);
        }
        _bridging = true;
    }

    /// <summary>
    /// How far you can walk straight ahead on vnavmesh's walkable ground, checked every 3 y (a few mesh lookups, once
    /// per walk): each spot at most 2.5 y above or below the previous one, so slopes are fine while water (its floor
    /// is the bottom, well below) or a drop ends it.
    /// </summary>
    private float WalkableAhead(Vector3 pos, Vector2 dir, float step)
    {
        var safe = 0f;
        var lastY = pos.Y;
        for (var d = 3f; ; d += 3f)
        {
            var at = Math.Min(d, step);
            var floor = _p.Ipc.NavGroundAt(pos.X + dir.X * at, pos.Z + dir.Y * at, 1.5f, lastY + 3f);
            if (floor is not { } f || Math.Abs(f.Y - lastY) > 2.5f)
                return safe;
            lastY = f.Y;
            safe = at;
            if (at >= step)
                return safe;
        }
    }

    /// <summary>
    /// Where you are on the route, if you're on it: the segment you're on, searched only along the part you could have
    /// covered since the route was asked for (a later part that loops back near you, e.g. a way out from under a roof
    /// that climbs back over it, must not be taken for your position). Returns the index of the next point to fly to.
    /// </summary>
    private int PositionOnRoute(List<Vector3> points, Vector3 pos)
    {
        var travelled = Vector3.Distance(_pathFrom, pos);
        if (travelled < 3)
            return 0; // haven't moved: the route starts right here
        var reach = travelled * 1.5f + 10;
        var along = 0f;
        var bestIndex = -1;
        var best = float.MaxValue;
        for (var i = 1; i < points.Count && along <= reach; i++)
        {
            var a = points[i - 1];
            var ab = points[i] - a;
            var len2 = ab.LengthSquared();
            var t = len2 > 0 ? Math.Clamp(Vector3.Dot(pos - a, ab) / len2, 0f, 1f) : 0f;
            var d = Vector3.Distance(a + ab * t, pos);
            if (d < best) { best = d; bestIndex = i; }
            along += MathF.Sqrt(len2);
        }
        return bestIndex;
    }

    /// <summary>The computed path is ready: follow it from where you are now. Returns false if there is no path.</summary>
    private bool PathArrived(Dalamud.Game.ClientState.Objects.Types.IGameObject player)
    {
        var task = _pathTask!;
        _pathTask = null;
        if (!task.IsCompletedSuccessfully || task.Result is not { Count: > 0 } points)
            return false;
        if (_pathFly && !Plugin.Condition[ConditionFlag.InFlight] && (MountComing() || Mounted()) && !TakeOffGaveUp())
        {
            // Not in the air yet: Beeline takes off itself first. Handing vnavmesh a flying route on the ground makes it
            // press Jump every few frames until you fly ("Cannot execute at this time" each time).
            _heldPath = points;
            Plugin.Info($"{Plugin.Tag} Path ready after {_pathClock.Elapsed.TotalSeconds:0.0} s, taking off before following it");
            return true;
        }
        // The head-start move took you away from the planned route (it went in a straight line): rejoining it from here
        // could go through walls or a roof, so compute the route again from where you are (once), hovering meanwhile.
        // Join the route where you are (no recompute: on long routes that costs several seconds of hovering).
        // If joining it ever runs into something (rare), the stuck watchdog recomputes from there.
        FollowPath(points, player);
        return true;
    }

    private void FollowPath(List<Vector3> points, Dalamud.Game.ClientState.Objects.Types.IGameObject player)
    {
        // Continue from where you are on the route; never shortcut it.
        var next = Math.Max(0, PositionOnRoute(points, player.Position));
        var pos = player.Position;
        // On the mount for a flying route: its first points were planned from the ground right around you. Going to them
        // once you're taking off (at once with a single jump) means dipping back down or turning back: start at the first
        // point that's actually away from you.
        // Never skip a nearby point well above you, though: leaving an enclosed spot (a walled camp, a courtyard), that
        // point is the climb over the wall.
        if (_pathFly && Mounted())
            while (next < points.Count - 1 && Vector2.Distance(Flat(points[next]), Flat(pos)) < 6f && points[next].Y - pos.Y < 3f)
                next++;
        var route = next == 0 ? points : points.GetRange(next, points.Count - next);
        // vnavmesh's "tolerance" only matters when the exact spot can't be reached: its route still ends on the NPC/mob.
        // End it where it first gets within the stop distance instead. Flying to an NPC or object, that end point is put
        // on the ground (else you'd stop in the air, or fly on into its face): you land at talking distance.
        if (_destRange > 0)
        {
            route = EndWithin(route, _dest, _destRange);
            if (_pathFly && _destRange <= 8f && route.Count > 0)
            {
                var end = route[^1];
                try
                {
                    if (_p.Ipc.NavGroundAt(end.X, end.Z, 5f, end.Y + 5f) is { } ground)
                        route[^1] = ground;
                }
                catch { /* vnavmesh gone: keep the point as it is */ }
            }
        }
        Plugin.Info($"{Plugin.Tag} Following the path ({points.Count} points, starting at point {next}), {_pathClock.Elapsed.TotalSeconds:0.0} s after asking");
        _p.Ipc.NavFollow(route, _pathFly);
        _bridging = false;
        _heldPath = null;
    }

    private bool _tookOff;      // this move has been in the air (no more take-off presses after that)

    /// <summary>
    /// Beeline's own take-off didn't work (6 presses, or 1.5 s since the last one without flying): hand the flying route
    /// to vnavmesh anyway, it has its own way to get you up.
    /// </summary>
    private bool TakeOffGaveUp()
        => _jumpPresses >= 6 || (_jumpPresses > 0 && Environment.TickCount64 - _jumpAt > 1500 && !Hopping(Plugin.Condition));
    private long _jumpAt;       // last Jump press by Beeline
    private bool _lastPressInAir; // that press was the mid-air (second) one
    private long _mountedAt;      // when you got on the mount during this move (0: already mounted at the start)
    private long _inFlightSince;  // when you took off during this move (0: already flying at the start)

    /// <summary>
    /// Flying for 0.4 s: the take-off has lifted you clear of the ground. A route handed over the instant you take off
    /// can dip you back onto the ground, and vnavmesh then presses Jump again and again ("Cannot execute").
    /// </summary>
    private bool AirborneSettled(Dalamud.Plugin.Services.ICondition c)
        => c[ConditionFlag.InFlight] && Environment.TickCount64 - _inFlightSince >= 400;

    /// <summary>In a jump. On a mount the game sets Jumping; Jumping61 is the on-foot one.</summary>
    private static bool Hopping(Dalamud.Plugin.Services.ICondition c) => c[ConditionFlag.Jumping] || c[ConditionFlag.Jumping61];
    private int _jumpPresses;

    /// <summary>
    /// Mounted on a flying leg, not in the air yet: press Jump. If you're airborne but not flying 0.2 s later (the game
    /// set to take off on a double jump), press again. At most 6 presses per move; stops as soon as you fly.
    /// </summary>
    private void TakeOff(Dalamud.Plugin.Services.ICondition c)
    {
        if (c[ConditionFlag.InFlight])
        {
            _tookOff = true;
            return;
        }
        if (_tookOff || _jumpPresses >= 6)
            return;
        var now = Environment.TickCount64;
        // Just got on the mount: it can't take off for a moment (a jump then is a plain hop and the whole double-jump
        // is wasted, ~2 s). Wait 0.5 s after mounting.
        if (now - _mountedAt < 500)
            return;
        var hopping = Hopping(c);
        // In the air: second press after 0.2 s (double-jump take-off). After a mid-air press the take-off animation
        // runs ~0.4 s with no jump flag: don't press into it (refused, "Cannot execute").
        if (now - _jumpAt < (hopping ? 200 : _lastPressInAir ? 1000 : 400))
            return;
        if (ActionManager.Instance()->UseAction(ActionType.GeneralAction, JumpGeneralAction))
        {
            _jumpAt = now;
            _jumpPresses++;
            _lastPressInAir = hopping;
            Plugin.Info($"Jump to take off ({(hopping ? "second press" : "first press")})");
        }
    }

    /// <summary>Something solid above the spot (a roof, an arch, an overhang) within 60 y: no flying straight in.</summary>
    private static bool Covered(Vector3 spot)
    {
        var from = new Vector3(spot.X, spot.Y + 2f, spot.Z);
        return FFXIVClientStructs.FFXIV.Common.Component.BGCollision.BGCollisionModule
            .RaycastMaterialFilter(from, new Vector3(0, 1, 0), out _, 60f);
    }

    /// <summary>
    /// The nearest open-sky ground around a covered spot (rings of 8 points at 8, 15, 25 and 35 y, about the same
    /// height so it's the same street, not a roof or a cliff below). A few lookups, once per trip.
    /// </summary>
    private Vector3? OpenLandingNear(Vector3 spot)
    {
        foreach (var r in new[] { 8f, 15f, 25f, 35f })
        {
            Vector3? best = null;
            var bestDist = float.MaxValue;
            for (var k = 0; k < 8; k++)
            {
                var a = k * MathF.PI / 4f;
                Vector3? g;
                try { g = _p.Ipc.NavGroundAt(spot.X + r * MathF.Cos(a), spot.Z + r * MathF.Sin(a), 3f, spot.Y + 6f); }
                catch { return null; }
                if (g is not { } ground || MathF.Abs(ground.Y - spot.Y) > 6f || Covered(ground))
                    continue;
                var d = Vector3.Distance(ground, spot);
                if (d < bestDist) { bestDist = d; best = ground; }
            }
            if (best != null)
                return best;
        }
        return null;
    }

    /// <summary>The route cut where it first comes within <paramref name="range"/> of <paramref name="dest"/>.</summary>
    private static List<Vector3> EndWithin(List<Vector3> route, Vector3 dest, float range)
    {
        for (var i = 0; i < route.Count; i++)
        {
            if (Vector3.Distance(route[i], dest) > range)
                continue;
            if (i == 0)
                return route.GetRange(0, 1);
            // Where on the segment from the previous point the distance drops to the range (the distance only falls
            // there, since the previous point was outside it).
            var a = route[i - 1];
            var b = route[i];
            float lo = 0, hi = 1;
            for (var k = 0; k < 20; k++)
            {
                var mid = (lo + hi) / 2;
                if (Vector3.Distance(Vector3.Lerp(a, b, mid), dest) > range) lo = mid;
                else hi = mid;
            }
            var cut = route.GetRange(0, i);
            cut.Add(Vector3.Lerp(a, b, hi));
            return cut;
        }
        return route;
    }

    private static bool Mounted() => Plugin.Condition[ConditionFlag.Mounted] || Plugin.Condition[ConditionFlag.RidingPillion];

    /// <summary>A mount is on its way (Beeline will summon one, or something is summoning it right now).</summary>
    private bool MountComing()
    {
        var c = Plugin.Condition;
        return (_wantMount && (!_sawMounted || _allowRemount))
               || c[ConditionFlag.Mounting] || c[ConditionFlag.Mounting71] || c[ConditionFlag.MountOrOrnamentTransition];
    }

    /// <summary>Adds the slice since the last sample to the right speed bucket.</summary>
    private void SampleSpeed(Dalamud.Game.ClientState.Objects.SubKinds.IPlayerCharacter player, bool mounted, bool inFlight,
                             bool hasSprint, bool hasJog, bool hasPeloton)
    {
        var dt = (float)_sampleClock.Elapsed.TotalSeconds;
        _sampleClock.Restart();
        var pos = player.Position;
        var moved = inFlight ? Vector3.Distance(pos, _lastSample) : Vector2.Distance(Flat(pos), Flat(_lastSample));
        _lastSample = pos;
        if (dt is < 0.03f or > 1f || moved / dt < 1f)
            return; // standing still (mount cast, path being computed...) doesn't count
        var mode = inFlight ? MoveMode.Fly : mounted ? MoveMode.Ride
                 : hasSprint ? MoveMode.Sprint : hasJog ? MoveMode.Jog : hasPeloton ? MoveMode.Peloton : MoveMode.Run;
        _bucketDist[(int)mode] += moved;
        _bucketTime[(int)mode] += dt;
        if (!inFlight)
            _pathLength += moved;
    }

    /// <summary>End of a move: learn each speed that was used long enough, and the ground detour factor.</summary>
    private void LearnSpeeds(Dalamud.Game.ClientState.Objects.Types.IGameObject player)
    {
        void Learn(MoveMode mode, Func<float> get, Action<float> set, Func<int> samples, Action<int> setSamples)
        {
            var t = _bucketTime[(int)mode];
            if (t < 2f)
                return;
            var speed = _bucketDist[(int)mode] / t;
            if (speed is < 2 or > 200) // whatever your speed is, as long as it's a real measurement
                return;
            var n = samples();
            set(Blend(get(), n, speed));
            setSamples(n + 1);
            Plugin.Info($"{Plugin.Tag} {mode}: {speed:0.0} y/s over {t:0.0} s");
            _timingsChanged = true;
        }
        Learn(MoveMode.Run,     () => Config.RunSpeed,     v => Config.RunSpeed = v,     () => Config.RunSamples,     n => Config.RunSamples = n);
        Learn(MoveMode.Peloton, () => Config.PelotonSpeed, v => Config.PelotonSpeed = v, () => Config.PelotonSamples, n => Config.PelotonSamples = n);
        Learn(MoveMode.Sprint,  () => Config.SprintSpeed,  v => Config.SprintSpeed = v,  () => Config.SprintSamples,  n => Config.SprintSamples = n);
        Learn(MoveMode.Jog,     () => Config.JogSpeed,     v => Config.JogSpeed = v,     () => Config.JogSamples,     n => Config.JogSamples = n);
        Learn(MoveMode.Ride,    () => Config.RideSpeed,    v => Config.RideSpeed = v,    () => Config.RideSamples,    n => Config.RideSamples = n);
        Learn(MoveMode.Fly,     () => Config.FlySpeed,     v => Config.FlySpeed = v,     () => Config.FlySamples,     n => Config.FlySamples = n);

        // Detour: ground path walked vs straight line, on moves that stayed on the ground and went somewhere.
        var straight = Vector2.Distance(_moveStart, Flat(player.Position));
        if (!_sawFlying && _pathLength > 30 && straight > 20)
        {
            var detour = Math.Clamp(_pathLength / straight, 1f, 3f);
            Config.Detour = Blend(Config.Detour, Config.DetourSamples++, detour);
            _timingsChanged = true;
        }
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
        UpdateTimer(player);

        // After arriving: a zone change (duty, manual teleport, logout) ends the trip.
        if (_phase != Phase.Arriving && !c[ConditionFlag.BetweenAreas] && !c[ConditionFlag.BetweenAreas51]
            && Plugin.ClientState.TerritoryType != _territory)
        {
            Cancel("cancelled (you changed zone)", quiet: true);
            return;
        }
        if (_phase != Phase.Arriving && player == null)
            return; // loading screen
        // Being attacked doesn't stop the trip (in the game, taking hits doesn't knock you off your mount either):
        // you carry on unless you target an enemy (if that option is on: so you can start the fight once the trip stops)
        // or something dismounts you for a fight.
        if (Config.StopOnEnemyTarget && EnemyStopsTrip())
        {
            Plugin.Info($"{Plugin.Tag} Enemy targeted{(_tripEnemy != null ? " and in range" : "")}: trip stopped");
            Cancel("stopped (enemy targeted)", quiet: true);
            return;
        }

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
                LearnHop();

                // Teleported to the city's aetheryte: now take the aethernet to the shard.
                if (_pendingShard != 0)
                {
                    var shard = _pendingShard;
                    _pendingShard = 0;
                    bool ok;
                    try { ok = _p.Ipc.AethernetTo(shard); } catch { ok = false; }
                    if (!ok) { Cancel("failed: could not take the aethernet"); return; }
                    Plugin.Info($"{Plugin.Tag} Arrived at the aetheryte: aethernet to #{shard}");
                    _hop = Hop.Aethernet;
                    _hopTime.Restart();
                    _arrival   = _shardArrival;
                    _territory = _finalTerritory;
                    SetPhase(Phase.Arriving);
                    return;
                }
                if (_endOnArrival)
                {
#if DEVTOOLS
                    _testLast = "arrived";
#endif
                    Plugin.Info($"{Plugin.Tag} Arrived");
                    Stop();
                    return;
                }
                SetPhase(Phase.WaitMesh);
                return;
            }

            case Phase.WaitMesh:
            {
                if (busy) return;
                if (_phaseTime.Elapsed.TotalSeconds > 120) { Cancel("timed out waiting for vnavmesh"); return; }
                bool ready;
                try { ready = _p.Ipc.NavReady(); } catch { Cancel("failed: vnavmesh is not available"); return; }
                if (!ready) return;

                var here = Plugin.ClientState.TerritoryType;
                RefineMarker(here);
                // Far away: summon a mount (on the way, or right here before taking off).
                var wantMount = Config.AutoMount && !c[ConditionFlag.Mounted] && !c[ConditionFlag.RidingPillion] && !c[ConditionFlag.InCombat] && CanMount(here)
                                && MountIsFaster(Vector2.Distance(Flat(player!.Position), _target), here);
                // Flying zone: ask for the flying path right away. vnavmesh waits in place until you're mounted, then
                // takes off, so the path is computed while the mount is summoned (no re-planning mid-way, no going back).
                var fly = Config.AllowFlying && CanFly(here) && (c[ConditionFlag.Mounted] || wantMount);
                // The marker's NPC/object is already loaded: walk right up to it.
                if (FindMarkerObject() is { } obj)
                {
                    _approached = true;
                    var (spot, stop) = Approach(player!, obj, fly);
                    Plugin.Info($"{Plugin.Tag} Moving to {obj.Name} at {spot} ({(fly ? "flying" : "on foot/mount")})");
                    StartMoving(player!, spot, stop, fly, wantMount);
                    try { SendMove(fly); }
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

                // A spot under a roof, an arch or an overhang: a straight flight there runs into it from above. Fly to
                // the open sky next to it first, then on in, low (flying is much faster than walking; if the mount
                // really can't get in, the stuck watchdog switches to the ground).
                Vector3? walkAfter = null;
                if (fly && Covered(dest.Value) && OpenLandingNear(dest.Value) is { } open)
                {
                    Plugin.Info($"{Plugin.Tag} That spot is under cover: flying in from the open sky {Vector3.Distance(open, dest.Value):0} y away");
                    walkAfter = dest;
                    dest = open with { Y = open.Y + 3f }; // above it: stay in the air, no landing on the way
                }

                Plugin.Info($"{Plugin.Tag} Moving to {dest.Value} ({(fly ? "flying" : "on foot/mount")})");
                StartMoving(player!, dest.Value, 0, fly, wantMount);
                _walkAfter = walkAfter;
                try { SendMove(fly); }
                catch { Cancel("failed: vnavmesh is not available"); return; }
                SetPhase(Phase.Moving);
                return;
            }

            case Phase.Moving:
            {
                if (player != null)
                {
                    var mounted = c[ConditionFlag.Mounted] || c[ConditionFlag.RidingPillion];
                    // Something else is already mounting you (another plugin's auto-mount, or you): leave it alone.
                    var mountingNow = c[ConditionFlag.Casting] || c[ConditionFlag.Casting87] || c[ConditionFlag.Mounting]
                                      || c[ConditionFlag.Mounting71] || c[ConditionFlag.MountOrOrnamentTransition];
                    var inFlight = c[ConditionFlag.InFlight];
                    // Log every mount / take-off change during the move (helps when another plugin interferes).
                    if (mounted != _wasMounted)
                    {
                        Plugin.Info($"{Plugin.Tag} {(mounted ? "Mounted" : "Dismounted")} {_moveTime.Elapsed.TotalSeconds:0.0} s into the move"
                                    + (mounted ? $" (Beeline had summoned it: {(_mountTries > 0 ? "yes" : "no")})" : ""));
                        if (mounted)
                        {
                            _mountedAt = Environment.TickCount64;
                            // The mount's appearance stops whatever little walk was going on: keep going on the ground
                            // while Beeline takes off (no stop between getting on and lifting off).
                            if (_pathFly && !c[ConditionFlag.InFlight] && player != null && (_pathTask != null || _heldPath != null))
                                try { Bridge(player); } catch { /* vnavmesh gone */ }
                        }
                        if (mounted && _mountCastClock.IsRunning)
                        {
                            var took = (float)_mountCastClock.Elapsed.TotalSeconds;
                            _mountCastClock.Reset();
                            if (took is > 0.5f and < 8f)
                            {
                                Config.MountSeconds = Blend(Config.MountSeconds, Config.MountSamples++, took);
                                _timingsChanged = true;
                            }
                        }
                        // Knocked off before taking off: usually another plugin's mount module. Explain it once.
                        if (!mounted && _flying && !inFlight && _sawMounted && !_warnedKnockOff)
                        {
                            _warnedKnockOff = true;
                            Plugin.Chat.Print("Beeline: you were dismounted before taking off. This is usually another plugin's "
                                            + "auto-mount, auto-dismount or cancel-mount-cast feature: turn it off for smooth trips, Beeline mounts you itself.");
                        }
                    }
                    if (inFlight && !_wasInFlight)
                    {
                        _inFlightSince = Environment.TickCount64;
                        // Airborne: the ground walk-ahead would pull the mount back down. Climb straight up a little until
                        // the route takes over (straight up: forward could be a wall, e.g. leaving a walled camp).
                        if (_pathFly && player != null && (_heldPath != null || _pathTask != null))
                            try
                            {
                                _p.Ipc.NavFollow([player.Position + new Vector3(0, 6, 0)], true);
                                _bridging = false;
                            }
                            catch { /* vnavmesh gone */ }
                    }
                    if (inFlight && !_wasInFlight)
                        Plugin.Info($"{Plugin.Tag} Took off {_moveTime.Elapsed.TotalSeconds:0.0} s into the move");
                    _wasMounted = mounted;
                    _wasInFlight = inFlight;
                    _sawMounted |= mounted;
                    _sawFlying |= inFlight;

                    // Mount Roulette on the way. A summon that goes through shows as a cast at once: a press with no cast
                    // a step later was dropped (right after a loading screen, or another plugin's action at the same
                    // moment): pressed again right away, the game queues it behind that action's animation (up to 30
                    // presses). A call the game refuses (e.g. right after an instant action) doesn't count; give up
                    // after ~3 s of refusals.
                    // Never once you've been mounted on this move: Mount Roulette while mounted would dismount you.
                    if (_wantMount && !mounted && (!_sawMounted || _allowRemount) && !mountingNow && !c[ConditionFlag.InCombat]
                        && !c[ConditionFlag.InFlight] && !Hopping(c) && _mountAttempts < 30
                        && (_mountTries == 0 || (_mountTries < 30 && _mountCastClock.ElapsedMilliseconds >= 90)))
                    {
                        if (SummonMount())
                        {
                            _mountTries++;
                            _allowRemount = false;
                            _mountCastClock.Restart();
                            if (_mountTries == 2)
                                Plugin.Info($"{Plugin.Tag} The mount didn't start: summoning again (until it does)");
                        }
                        else
                            _mountAttempts++;
                    }

                    // Statuses that change your speed (read once per step).
                    bool hasSprint = false, hasPeloton = false, hasJog = false;
                    var sprintLeft = 0f;
                    if (!mounted)
                        foreach (var st in player.StatusList)
                        {
                            if (st.StatusId == SprintStatus)
                            {
                                hasSprint = true;
                                sprintLeft = st.RemainingTime;
                                if (st.RemainingTime > Config.SprintSeconds) { Config.SprintSeconds = st.RemainingTime; _timingsChanged = true; } // its full duration
                            }
                            else if (st.StatusId == PelotonStatus) hasPeloton = true;
                            else if (st.StatusId == JogStatus) hasJog = true;
                        }
                    _sawSprint |= hasSprint;
                    _sawPeloton |= hasPeloton;
                    SampleSpeed(player, mounted, c[ConditionFlag.InFlight], hasSprint, hasJog, hasPeloton);

                    // On foot: Sprint first (Jog follows it and lasts until you change map, mount up or fight), otherwise keep
                    // Peloton up if your job has it. Nothing while Jog runs, nor while Sprint runs except in its last 2 s
                    // (Peloton then takes over the moment Sprint ends), nor right after using Sprint (its effect takes
                    // a moment to show up: Peloton would otherwise be cast on top of it).
                    var sprintPending = _sprintUsed && !hasSprint && _sprintClock.ElapsedMilliseconds < 2000;
                    // Giddy Up uses Sprint: one it's about to use (ready for under 3 s) or just used (on cooldown for
                    // under 2 s, effect not showing yet) would end up under a Peloton put on now.
                    if (_guSprint && !hasSprint && !mounted)
                    {
                        if (SprintReady()) { _sprintGoneClock.Reset(); if (!_sprintReadyClock.IsRunning) _sprintReadyClock.Restart(); }
                        else { _sprintReadyClock.Reset(); if (!_sprintGoneClock.IsRunning) _sprintGoneClock.Restart(); }
                        sprintPending |= _sprintReadyClock.IsRunning ? _sprintReadyClock.ElapsedMilliseconds < 3000
                                                                    : _sprintGoneClock.ElapsedMilliseconds < 2000;
                    }
                    if (Config.SpeedBuffs && !(_guSprint && _guPeloton) && !mounted && !_wantMount && !mountingNow && (!hasSprint || sprintLeft <= 2f) && !sprintPending
                        && !hasJog && _boostFails < 20
                        && Vector2.Distance(Flat(player.Position), Flat(_dest)) > 20)
                    {
                        var am = ActionManager.Instance();
                        var used = false;
                        if (!_guSprint && !_sprintUsed && !hasSprint && SprintReady() && am->UseAction(ActionType.GeneralAction, SprintGeneralAction))
                        {
                            used = _sprintUsed = true;
                            _sprintClock.Restart();
                            Plugin.Info($"{Plugin.Tag} Sprint");
                        }
                        else if (!_guPeloton && !hasPeloton && !c[ConditionFlag.InCombat] && am->GetActionStatus(ActionType.Action, PelotonAction) == 0
                                 && am->UseAction(ActionType.Action, PelotonAction))
                        {
                            used = true;
                            Plugin.Info($"{Plugin.Tag} Peloton");
                        }
                        _boostFails = used || hasPeloton ? 0 : _boostFails + 1;
                    }

                    // Waiting on a flying path but the mount never came (refused, combat...): go on foot instead.
                    if (_flying && !mounted && !_sawMounted && !mountingNow
                        && (_mountCastClock.IsRunning ? _mountCastClock.ElapsedMilliseconds : _moveTime.ElapsedMilliseconds) > 6000)
                    {
                        _flying = false;
                        _noFly = true;
                        _wantMount = false;
                        _heldPath = null; // the flying path waiting for the mount: never follow it on foot
                        Plugin.Info($"{Plugin.Tag} Couldn't mount: going on foot");
                        try { SendMove(false); } catch { /* vnavmesh gone */ }
                        _phaseTime.Restart();
                        return;
                    }

                    // Mounted by something else mid-way in a zone where you can fly: switch to flying. Stop first, so
                    // the new path starts from where you are (otherwise you keep going while it's computed, then turn back).
                    if (mounted && !_flying && !_noFly && Config.AllowFlying && CanFly(Plugin.ClientState.TerritoryType)
                        && Vector2.Distance(Flat(player.Position), Flat(_dest)) > 30f)
                    {
                        _flying = true;
                        Plugin.Info($"{Plugin.Tag} Mounted: flying the rest of the way");
                        try { _p.Ipc.NavStop(); SendMove(true); } catch { /* vnavmesh gone */ }
                        _phaseTime.Restart(); // the new path takes a moment to start
                        return;
                    }
                }

                // The path computed in the background is ready: follow it (or deal with "no path").
                if (_pathTask != null && player != null)
                {
                    if (_pathTask.IsCompleted)
                    {
                        bool ok;
                        try { ok = PathArrived(player); } catch { ok = false; }
                        if (!ok)
                        {
                            if (_pathFly)
                            {
                                Plugin.Info($"{Plugin.Tag} No flying path found: trying on the ground");
                                _flying = false;
                                _noFly = true; // for this move: don't switch back to flying
                                try { SendMove(false); } catch { Cancel("failed: vnavmesh is not available"); }
                            }
                            else
                                Cancel("failed: vnavmesh found no path there");
                            return;
                        }
                        _phaseTime.Restart();
                    }
                    else if (_pathFly && !_bridgeFlying && MountSettled() && (AirborneSettled(c) || TakeOffGaveUp()))
                    {
                        try { Bridge(player); } catch { /* vnavmesh gone */ } // mounted and settled: take off while waiting
                    }
                    else if (_bridging && Vector3.Distance(player.Position, _bridgeEnd) < (_bridgeFlying ? 15f : 3f))
                    {
                        try { Bridge(player); } catch { /* vnavmesh gone */ } // keep going until the path is ready
                    }
                }
                if (_heldPath != null && player != null
                    && (AirborneSettled(c) || TakeOffGaveUp() || (!Mounted() && !MountComing())))
                {
                    try { FollowPath(_heldPath, player); } catch { _heldPath = null; }
                    _phaseTime.Restart();
                }
                else if (_heldPath != null && player != null && _bridging && !_bridgeFlying
                         && Vector3.Distance(player.Position, _bridgeEnd) < 3f)
                {
                    // Flying path ready, mount still coming: keep walking toward the target meanwhile (no stop).
                    try { Bridge(player); } catch { /* vnavmesh gone */ }
                }

                // Take off on a flying leg: Beeline presses Jump itself, so it works whether the game's setting takes off
                // on the first jump or on a double jump (a hop that didn't take off gets a second press).
                if (_pathFly && player != null && Mounted() && MountSettled() && _navStarted)
                    TakeOff(c);

                // Keep watching for your own movement until vnavmesh arrives (or gives up).
                bool moving;
                try { moving = _pathTask != null || _heldPath != null || _p.Ipc.NavPathRunning(); } catch { moving = false; }
                if (moving)
                    _navStarted = true;
                if (moving && player != null && Watchdog(player, c))
                    return;
                // The quest's exact marker showed up after arriving (height, NPC): re-route there (the right floor).
                if (moving && !_refined && player != null && Environment.TickCount64 >= _refineAt)
                {
                    _refineAt = Environment.TickCount64 + 1000;
                    if (RefineMarker(Plugin.ClientState.TerritoryType))
                    {
                        _flying = c[ConditionFlag.InFlight];
                        if (FindMarkerObject() is { } exact)
                        {
                            _approached = true;
                            (_dest, _destRange) = Approach(player, exact, _flying);
                        }
                        else if (_targetY is { } ty && _p.Ipc.NavGroundAt(_target.X, _target.Y, 5, ty + 3) is { } g)
                            (_dest, _destRange) = (g, 0f);
                        Plugin.Info($"{Plugin.Tag} Re-routing to the marker's exact spot: {_dest}");
                        try { SendMove(_flying); } catch { /* vnavmesh gone */ }
                        _navStarted = false;
                        SetPhase(Phase.Moving);
                        return;
                    }
                }
                // A quest marker without an object id: look for its object/NPC on the way, once a second, and head
                // straight for it as soon as the game has loaded it (instead of going to the marker first).
                if (moving && !_approached && _dataId == 0 && player != null && Environment.TickCount64 >= _questScanAt)
                {
                    _questScanAt = Environment.TickCount64 + 1000;
                    if (QuestObjectInArea() is { } early)
                    {
                        _approached = true;
                        _flying = c[ConditionFlag.InFlight];
                        (_dest, _destRange) = Approach(player, early, _flying);
                        Plugin.Info($"{Plugin.Tag} Found {Plugin.Redact(early.Name.TextValue)} on the way: heading to {_dest}");
                        try { SendMove(_flying); } catch { /* vnavmesh gone */ }
                        _navStarted = false;
                        SetPhase(Phase.Moving);
                        return;
                    }
                }
                if (!moving && _phaseTime.Elapsed.TotalSeconds > 1.5 && _walkAfter is { } rest && player != null)
                {
                    // Next to a covered spot, in the open: on in, still flying (low) if you are.
                    _walkAfter = null;
                    _flying = c[ConditionFlag.InFlight];
                    _dest = rest;
                    _destRange = 0;
                    Plugin.Info($"{Plugin.Tag} In the open next to it: {(_flying ? "flying" : "going")} in, the last {Vector3.Distance(player.Position, rest):0} y");
                    try { SendMove(_flying, bridge: false); } catch { /* vnavmesh gone */ }
                    _navStarted = false;
                    SetPhase(Phase.Moving);
                    return;
                }
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
                            _flying = c[ConditionFlag.InFlight];
                            (_dest, _destRange) = Approach(player, obj, _flying);
                            try { SendMove(_flying); } catch { /* vnavmesh gone */ }
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

                    if (_navStarted)
                        LearnSpeeds(player!);
                    var left = Vector2.Distance(Flat(player!.Position), _target);
#if DEVTOOLS
                    _testLast = _navStarted ? "arrived" : "no-path";
                    _testLastLeft = left;
#endif
                    Stop();
                    Plugin.Info(_navStarted
                        ? $"{Plugin.Tag} vnavmesh stopped after {_phaseTime.Elapsed.TotalSeconds:0.0} s, {left:0.0} y from the target"
                        : $"{Plugin.Tag} vnavmesh never started moving ({left:0.0} y from the target): no path found?");
                }
                return;
            }
        }
    }

    private const float OnMarkerWithin = 4f;

    private static bool IsFriendlyInteractable(Dalamud.Game.ClientState.Objects.Types.IGameObject o)
        => o.IsTargetable && o.ObjectKind is Dalamud.Game.ClientState.Objects.Enums.ObjectKind.EventNpc
                                          or Dalamud.Game.ClientState.Objects.Enums.ObjectKind.EventObj
                                          or Dalamud.Game.ClientState.Objects.Enums.ObjectKind.GatheringPoint;

    /// <summary>
    /// In an area marker with no NPC/object (gathering leves, fishing/mining areas...): the nearest gathering point you can
    /// use there (nearest to you once you're inside the area).
    /// </summary>
    private const float QuestIconSearch = 60f; // around a quest icon: about the distance objects are loaded at
    private long _questScanAt;

    private Dalamud.Game.ClientState.Objects.Types.IGameObject? GatheringInArea()
    {
        if (_radius <= OnMarkerWithin + 1)
            return null;
        var player = Plugin.ObjectTable.LocalPlayer;
        var inArea = player != null && Vector2.Distance(Flat(player.Position), _target) <= _radius + 10f;
        var from = inArea ? Flat(player!.Position) : _target;
        Dalamud.Game.ClientState.Objects.Types.IGameObject? best = null;
        var bestDist = float.MaxValue;
        foreach (var o in Plugin.ObjectTable)
        {
            if (o.ObjectKind != Dalamud.Game.ClientState.Objects.Enums.ObjectKind.GatheringPoint || !o.IsTargetable)
                continue;
            if (Vector2.Distance(Flat(o.Position), _target) > _radius + 10f)
                continue;
            var d = Vector2.Distance(Flat(o.Position), from);
            if (d < bestDist) { bestDist = d; best = o; }
        }
        return best;
    }

    /// <summary>
    /// In an area marker with no NPC/object id (quests where you look for something in a zone): a quest object or NPC
    /// in the area, preferring ones with a marker above them (the game's quest icon); the nearest to you once you're
    /// inside the area, else the nearest to its centre.
    /// </summary>
    private unsafe Dalamud.Game.ClientState.Objects.Types.IGameObject? QuestObjectInArea()
    {
        // The quest's area (the circle, also when you clicked its icon): anything usable inside it, marked ones first.
        // An icon without an area: only objects showing the game's quest marker, within the distance the game loads
        // objects around you.
        var area = _areaRadius > 0;
        if (!area && !_hasMarker)
            return null; // a plain spot on the map, not a quest marker
        var center = area ? _areaCenter : _target;
        var searchRadius = area ? _areaRadius : QuestIconSearch;
        var player = Plugin.ObjectTable.LocalPlayer;
        var inArea = player != null && Vector2.Distance(Flat(player.Position), center) <= searchRadius;
        var from = inArea ? Flat(player!.Position) : center;
        Dalamud.Game.ClientState.Objects.Types.IGameObject? best = null;
        var bestScore = float.MaxValue;
        foreach (var o in Plugin.ObjectTable)
        {
            if (!o.IsTargetable || o.ObjectKind is not (Dalamud.Game.ClientState.Objects.Enums.ObjectKind.EventObj
                                                     or Dalamud.Game.ClientState.Objects.Enums.ObjectKind.EventNpc))
                continue;
            if (Vector2.Distance(Flat(o.Position), center) > searchRadius + o.HitboxRadius)
                continue; // outside the quest's area
            var marked = ((FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)o.Address)->NamePlateIconId != 0;
            if (!area && !marked)
                continue;
            // A marked object always beats an unmarked one; then the nearest.
            var score = Vector2.Distance(Flat(o.Position), from) + (marked ? 0f : 100_000f);
            if (score < bestScore) { bestScore = score; best = o; }
        }
        return best;
    }

    /// <summary>
    /// Where to go for an NPC/object/mob, and how close to stop. Flying to an NPC or object: a spot on the ground on
    /// your side of it, a little inside talking distance (NPCs ~6 y, objects ~4.5, plus their size), flown to directly;
    /// otherwise the thing itself, stopping at <see cref="StopDistance"/>.
    /// </summary>
    private (Vector3 Spot, float Stop) Approach(Dalamud.Game.ClientState.Objects.Types.IGameObject player,
                                                Dalamud.Game.ClientState.Objects.Types.IGameObject obj, bool flying)
    {
        if (!flying || obj is Dalamud.Game.ClientState.Objects.Types.IBattleNpc)
            return (obj.Position, StopDistance(obj));
        var talk = obj.ObjectKind == Dalamud.Game.ClientState.Objects.Enums.ObjectKind.EventNpc ? 4f : 2.5f;
        var away = Flat(player.Position) - Flat(obj.Position);
        var dir = away.Length() > 0.1f ? Vector2.Normalize(away) : new Vector2(1, 0);
        var dist = obj.HitboxRadius + talk;
        var x = obj.Position.X + dir.X * dist;
        var z = obj.Position.Z + dir.Y * dist;
        try
        {
            if (_p.Ipc.NavGroundAt(x, z, 3f, obj.Position.Y + 5f) is { } ground && MathF.Abs(ground.Y - obj.Position.Y) < 4f)
                return (ground, 0f);
        }
        catch { /* vnavmesh gone */ }
        return (obj.Position, StopDistance(obj)); // no ground there (a ledge...): the usual way
    }

    /// <summary>
    /// In the destination zone, look the clicked marker up again: from another map only the map's icon was known (no
    /// height: in a layered city that picked the wrong floor), now the quest's exact marker (height, NPC, area) is.
    /// </summary>
    private bool RefineMarker(uint here)
    {
        if (_refined || here != _finalTerritory || string.IsNullOrWhiteSpace(_pressTooltip))
            return false;
        // Right after arriving the quest's own markers may not be loaded yet: try again (once a second, on the way) for
        // up to 15 s, then make do with the icon.
        var now = Environment.TickCount64;
        if (_refineSince == 0)
            _refineSince = now;
        if (now - _refineSince > 15000)
        {
            _refined = true;
            return false;
        }
        if (QuestMarkers.Find(here, _pressMapId, _target, _pressTooltip, _pressIconWithin, cursorReliable: true, preferHeight: true, quiet: true) is not { } mk)
            return false;
        var at = new Vector2(mk.Position.X, mk.Position.Z);
        if (Vector2.Distance(at, _target) > Math.Max(30f, mk.Radius))
            return false; // something else of that name
        if (!mk.HasHeight && mk.DataId == 0)
            return false; // still only the map's icon
        _refined = true;
        Plugin.Info($"{Plugin.Tag} Marker looked up again in its zone: at {mk.Position} (radius {mk.Radius:0}, object {mk.DataId})");
        _target = at;
        if (mk.HasHeight)
            _targetY = mk.Position.Y;
        if (mk.DataId != 0)
            _dataId = mk.DataId;
        _radius = mk.Radius;
        if (mk.Radius > OnMarkerWithin + 1)
            (_areaCenter, _areaRadius) = (at, mk.Radius);
        else if (mk.AreaCenter is { } c)
            (_areaCenter, _areaRadius) = (c, mk.AreaRadius);
        else if (_areaRadius == 0 && QuestMarkers.FindArea(here, at) is { } a)
            (_areaCenter, _areaRadius) = (a.Center, a.Radius);
        return true;
    }

    /// <summary>Within the game's talk/use distance (NPCs ~6 yalms, objects ~4.5, plus their size).</summary>
    private static bool InInteractRange(Dalamud.Game.ClientState.Objects.Types.IGameObject player, Dalamud.Game.ClientState.Objects.Types.IGameObject o)
    {
        var max = o.ObjectKind == Dalamud.Game.ClientState.Objects.Enums.ObjectKind.EventNpc ? 6f : 4.5f;
        return Vector3.Distance(player.Position, o.Position) - o.HitboxRadius <= max;
    }

    /// <summary>Marker without an NPC/object id: the friendly NPC or object standing right on the marker.</summary>
    private Dalamud.Game.ClientState.Objects.Types.IGameObject? ObjectOnMarker()
    {
        if (!_hasMarker || _radius > OnMarkerWithin + 1)
            return null; // not a game marker, or an area
        var flatOnly = _targetY == null; // a map icon: no height known, compare on the map
        var marker = new Vector3(_target.X, _targetY ?? 0, _target.Y);
        Dalamud.Game.ClientState.Objects.Types.IGameObject? best = null;
        var bestDist = OnMarkerWithin;
        foreach (var o in Plugin.ObjectTable)
        {
            if (!IsFriendlyInteractable(o))
                continue;
            var d = flatOnly ? Vector2.Distance(Flat(o.Position), _target) : Vector3.Distance(o.Position, marker);
            if (d < bestDist)
            {
                bestDist = d;
                best = o;
            }
        }
        return best;
    }

    /// <summary>
    /// Stop within talking/using distance of NPCs and objects (a little inside the game's limit: NPCs ~6 yalms, objects
    /// ~4.5, plus their size), not nose to nose; stay at a safe distance from mobs.
    /// </summary>
    private float StopDistance(Dalamud.Game.ClientState.Objects.Types.IGameObject obj)
    {
        if (obj is not Dalamud.Game.ClientState.Objects.Types.IBattleNpc mob)
            return obj.HitboxRadius + (obj.ObjectKind == Dalamud.Game.ClientState.Objects.Enums.ObjectKind.EventNpc ? 4.5f : 3f);
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
            return ObjectOnMarker() ?? QuestObjectInArea() ?? GatheringInArea();
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

        // Both mouse buttons held: the game's other way to walk (in the game window, not over a plugin window).
        const int LeftButton = 0x01, RightButton = 0x02;
        if (Plugin.IsDown(LeftButton) && Plugin.IsDown(RightButton) && Plugin.GameHasFocus()
            && !Dalamud.Bindings.ImGui.ImGui.GetIO().WantCaptureMouse)
            return true;
        return false;
    }

    private static Vector2 Flat(Vector3 v) => new(v.X, v.Z);

#if DEVTOOLS
    // ---------------- Dev-build test probe (read only; see TestProbe.cs) ----------------

    private string _testRoute = "", _testMarker = "", _testLast = "";
    private float _testLastLeft = -1;
    private int _testTrips;

    /// <summary>
    /// One line for the tests: what the trip is doing now, e.g.
    /// "trip=on; phase=moving; move=flying; route=direct; dest=957@(230,520); left=84; ...". No names.
    /// </summary>
    internal string TestState()
    {
        static string Code(string s) => System.Text.RegularExpressions.Regex.Replace(s.ToLowerInvariant(), "[^a-z0-9+]+", "-").Trim('-');
        var c = Plugin.Condition;
        var player = Plugin.ObjectTable.LocalPlayer;
        var zone = Plugin.ClientState.TerritoryType;
        var mounted = Mounted();
        var tail = $"trips={_testTrips}; last={(_testLast.Length > 0 ? Code(_testLast) : "none")}"
                   + (_testLastLeft >= 0 ? $"; lastleft={_testLastLeft:0}" : "")
                   + $"; zone={zone}; mounted={(mounted ? 1 : 0)}; inflight={(c[ConditionFlag.InFlight] ? 1 : 0)}";
        if (_phase == Phase.None)
            return $"trip=off; phase=idle; {tail}";

        var phase = _phase switch
        {
            Phase.Arriving => _arrival != null ? "teleporting" : "starting",
            Phase.WaitMesh => "waitmesh",
            _ => "moving",
        };
        var move = _phase != Phase.Moving ? "-"
                 : c[ConditionFlag.InFlight] ? "flying"
                 : mounted ? (_pathFly && !_tookOff ? "takeoff" : "riding")
                 : MountComing() ? "mounting"
                 : "walking";
        var left = player != null ? Vector2.Distance(Flat(player.Position), _target) : -1f;
        var quest = _testMarker.Length == 0 ? "none" : _testMarker == "flag" ? "flag" : "marker";
        return $"trip=on; phase={phase}; move={move}; route={_testRoute}; dest={_finalTerritory}@({_target.X:0},{_target.Y:0}); left={left:0}"
               + $"; path={(_pathTask != null ? "computing" : _heldPath != null ? "held" : "ready")}; fly={(_pathFly ? 1 : 0)}; wantmount={(_wantMount ? 1 : 0)}"
               + $"; tookoff={(_tookOff ? 1 : 0)}; jumps={_jumpPresses}; target={quest}; approached={(_approached ? 1 : 0)}"
               + $"; walkafter={(_walkAfter != null ? 1 : 0)}; nofly={(_noFly ? 1 : 0)}"
               + $"; t={_tripTime.Elapsed.TotalSeconds:0}; est={_tripEstimate:0}; stuck={_stuckStage}; {tail}";
    }
#endif

    private const uint MountRouletteAction = 9;

    /// <summary>
    /// Summons the mount chosen in the settings: Mount Roulette, or one of your mounts (Mount Roulette if that one can't
    /// be summoned here). Returns whether the game took it.
    /// </summary>
    private bool SummonMount()
    {
        var am = ActionManager.Instance();
        if (Config.MountKind == MountChoice.Specific && Config.MountId != 0
            && PlayerState.Instance()->IsMountUnlocked(Config.MountId) && _mountAttempts < 5
            && am->UseAction(ActionType.Mount, Config.MountId))
            return true;
        return am->UseAction(ActionType.GeneralAction, MountRouletteAction);
    }

    private static bool CanMount(uint territory)
        => Plugin.Data.GetExcelSheet<TerritoryType>().TryGetRow(territory, out var t) && t.Mount;

    private static bool CanFly(uint territory)
    {
        if (!Plugin.Data.GetExcelSheet<TerritoryType>().TryGetRow(territory, out var t))
            return false;
        var set = t.AetherCurrentCompFlgSet.RowId;
        return set != 0 && PlayerState.Instance()->IsAetherCurrentZoneComplete(set);
    }
}
