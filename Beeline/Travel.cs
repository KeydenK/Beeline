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
        _target         = shard.Position ?? default;
        _targetY        = null;
        _dataId         = 0;
        _radius         = 0;
        _navStarted     = false;
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
        _pathTask = null;
        _heldPath = null;
        // Learned timings: one write to disk per trip, not one per measurement.
        if (_timingsChanged)
        {
            _timingsChanged = false;
            _p.SaveConfig();
        }
    }

    private bool _timingsChanged;

    private void SetPhase(Phase phase)
    {
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

        _legAfterHop = true; // every option below starts with a teleport or an aethernet hop
        foreach (var place in places.InTerritory(territory))
        {
            if (place.Position is not { } pos || !ui->IsAetheryteUnlocked(place.Id))
                continue;
            var rest = LegSeconds(Vector2.Distance(pos, target), territory, alreadyMounted: false);
            var name = place.Name;

            if (Config.UseShardsForTravel && active != null && !place.IsMain && place.Group != 0 && place.Group == active.Group && place.Id != active.Id)
            {
                var id = place.Id;
                routes.Add(new Route(Config.AethernetSeconds + rest, $"aethernet to {name}", pos, () => _p.Ipc.AethernetTo(id), Hop.Aethernet));
            }

            if (place.IsMain)
            {
                var id = place.Id;
                routes.Add(new Route(Config.TeleportSeconds + rest, $"teleport to {name}", pos, () => _p.Ipc.TeleportTo(id), Hop.Teleport));
            }
            else if (Config.UseShardsForTravel && places.MasterOf(place) is { } master && ui->IsAetheryteUnlocked(master.Id))
            {
                var m = master.Id;
                routes.Add(new Route(Config.TeleportSeconds + Config.AethernetSeconds + rest, $"teleport to {master.Name} then aethernet to {name}",
                    master.Position, () => _p.Ipc.TeleportTo(m), Hop.Teleport, master.Territory, place.Id, pos));
            }
        }

        _legAfterHop = false;
        if (routes.Count == 0)
            return null;
        if (Config.LogPresses)
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

    private bool _legAfterHop; // set while estimating legs that start after a teleport / aethernet (a zone change ends Jog)

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
        if (Config.SpeedBuffs && SprintReady())
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
        return Config.SpeedBuffs && player != null && player.Level >= 20 && !Plugin.Condition[ConditionFlag.InCombat]
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
        _sawMounted = Plugin.Condition[ConditionFlag.Mounted];
    }

    private Vector3 _dest;        // where vnavmesh is taking you
    private float _destRange;     // stop this close (0 = exactly there)
    private bool _flying;         // vnavmesh was asked to fly
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

    private const uint PelotonAction = 7557, SprintGeneralAction = 4;
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
        if (mounted)
        {
            // Take off and head up and toward the target, far enough to keep going until the path is ready.
            var step = Math.Min(Math.Clamp(Config.FlySpeed * 2.5f, 30f, 150f), to.Length());
            _bridgeEnd = new Vector3(pos.X + dir.X * step, pos.Y + 15, pos.Z + dir.Y * step);
            _p.Ipc.NavFollow([_bridgeEnd], true);
            _bridgeFlying = true;
        }
        else
        {
            // A few steps toward the target while the mount is summoned.
            var step = Math.Min(Math.Clamp(Config.RunSpeed * 1.5f, 8f, 40f), to.Length());
            _bridgeEnd = new Vector3(pos.X + dir.X * step, pos.Y, pos.Z + dir.Y * step);
            _p.Ipc.NavFollow([_bridgeEnd], false);
        }
        _bridging = true;
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
        if (_pathFly && !Plugin.Condition[ConditionFlag.InFlight] && !MountSettled() && (MountComing() || Mounted()))
        {
            _heldPath = points;
            Plugin.Info($"{Plugin.Tag} Path ready after {_pathClock.Elapsed.TotalSeconds:0.0} s, waiting for the mount before taking off");
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
        var route = next == 0 ? points : points.GetRange(next, points.Count - next);
        Plugin.Info($"{Plugin.Tag} Following the path ({points.Count} points, starting at point {next}), {_pathClock.Elapsed.TotalSeconds:0.0} s after asking");
        _p.Ipc.NavFollow(route, _pathFly);
        _bridging = false;
        _heldPath = null;
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
                    Plugin.Info($"{Plugin.Tag} Moving to {obj.Name} at {obj.Position} ({(fly ? "flying" : "on foot/mount")})");
                    StartMoving(player!, obj.Position, StopDistance(obj), fly, wantMount);
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

                Plugin.Info($"{Plugin.Tag} Moving to {dest.Value} ({(fly ? "flying" : "on foot/mount")})");
                StartMoving(player!, dest.Value, 0, fly, wantMount);
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
                        Plugin.Info($"{Plugin.Tag} Took off {_moveTime.Elapsed.TotalSeconds:0.0} s into the move");
                    _wasMounted = mounted;
                    _wasInFlight = inFlight;
                    _sawMounted |= mounted;
                    _sawFlying |= inFlight;

                    // Mount Roulette on the way: once, and once more after 2.5 s if it didn't take. A call the game
                    // refuses (e.g. right after an instant action) doesn't count; give up after ~3 s of refusals.
                    // Never once you've been mounted on this move: Mount Roulette while mounted would dismount you.
                    if (_wantMount && !mounted && (!_sawMounted || _allowRemount) && !mountingNow && !c[ConditionFlag.InCombat]
                        && !c[ConditionFlag.InFlight] && !c[ConditionFlag.Jumping61] && _mountAttempts < 30
                        && (_mountTries == 0 || (_mountTries == 1 && _phaseTime.ElapsedMilliseconds > 2500)))
                    {
                        if (SummonMount())
                        {
                            _mountTries++;
                            _allowRemount = false;
                            _mountCastClock.Restart();
                        }
                        else
                            _mountAttempts++;
                    }

                    // Statuses that change your speed (read once per step).
                    bool hasSprint = false, hasPeloton = false, hasJog = false;
                    if (!mounted)
                        foreach (var st in player.StatusList)
                        {
                            if (st.StatusId == SprintStatus)
                            {
                                hasSprint = true;
                                if (st.RemainingTime > Config.SprintSeconds) Config.SprintSeconds = st.RemainingTime; // its full duration
                            }
                            else if (st.StatusId == PelotonStatus) hasPeloton = true;
                            else if (st.StatusId == JogStatus) hasJog = true;
                        }
                    _sawSprint |= hasSprint;
                    _sawPeloton |= hasPeloton;
                    SampleSpeed(player, mounted, c[ConditionFlag.InFlight], hasSprint, hasJog, hasPeloton);

                    // On foot: Sprint first (Jog follows it and lasts until you change zone or fight), otherwise keep
                    // Peloton up if your job has it. Nothing while Sprint or Jog is already running.
                    if (Config.SpeedBuffs && !mounted && !_wantMount && !mountingNow && !hasSprint && !hasJog && _boostFails < 20
                        && Vector2.Distance(Flat(player.Position), Flat(_dest)) > 20)
                    {
                        var am = ActionManager.Instance();
                        var used = false;
                        if (!_sprintUsed && SprintReady() && am->UseAction(ActionType.GeneralAction, SprintGeneralAction))
                        {
                            used = _sprintUsed = true;
                            Plugin.Info($"{Plugin.Tag} Sprint");
                        }
                        else if (!hasPeloton && !c[ConditionFlag.InCombat] && am->GetActionStatus(ActionType.Action, PelotonAction) == 0
                                 && am->UseAction(ActionType.Action, PelotonAction))
                        {
                            used = true;
                            Plugin.Info($"{Plugin.Tag} Peloton");
                        }
                        _boostFails = used || hasPeloton ? 0 : _boostFails + 1;
                    }

                    // Waiting on a flying path but the mount never came (refused, combat...): go on foot instead.
                    if (_flying && !mounted && !_sawMounted && !mountingNow && _phaseTime.ElapsedMilliseconds > 6000)
                    {
                        _flying = false;
                        _wantMount = false;
                        Plugin.Info($"{Plugin.Tag} Couldn't mount: going on foot");
                        try { SendMove(false); } catch { /* vnavmesh gone */ }
                        _phaseTime.Restart();
                        return;
                    }

                    // Mounted by something else mid-way in a zone where you can fly: switch to flying. Stop first, so
                    // the new path starts from where you are (otherwise you keep going while it's computed, then turn back).
                    if (mounted && !_flying && Config.AllowFlying && CanFly(Plugin.ClientState.TerritoryType)
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
                                try { SendMove(false); } catch { Cancel("failed: vnavmesh is not available"); }
                            }
                            else
                                Cancel("failed: vnavmesh found no path there");
                            return;
                        }
                        _phaseTime.Restart();
                    }
                    else if (_pathFly && !_bridgeFlying && MountSettled())
                    {
                        try { Bridge(player); } catch { /* vnavmesh gone */ } // mounted and settled: take off while waiting
                    }
                    else if (_bridging && Vector3.Distance(player.Position, _bridgeEnd) < (_bridgeFlying ? 15f : 3f))
                    {
                        try { Bridge(player); } catch { /* vnavmesh gone */ } // keep going until the path is ready
                    }
                }
                if (_heldPath != null && player != null
                    && (MountSettled() || c[ConditionFlag.InFlight] || (!Mounted() && !MountComing())))
                {
                    try { FollowPath(_heldPath, player); } catch { _heldPath = null; }
                    _phaseTime.Restart();
                }

                // Keep watching for your own movement until vnavmesh arrives (or gives up).
                bool moving;
                try { moving = _pathTask != null || _heldPath != null || _p.Ipc.NavPathRunning(); } catch { moving = false; }
                if (moving)
                    _navStarted = true;
                if (moving && player != null && Watchdog(player, c))
                    return;
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
                            _dest = obj.Position;
                            _destRange = range;
                            _flying = c[ConditionFlag.InFlight];
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
