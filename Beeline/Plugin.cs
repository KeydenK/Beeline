using System.Numerics;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.ClientState.GamePad;
using Dalamud.Game.ClientState.Keys;
using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace Beeline;

[Serializable]
public sealed class Configuration : Dalamud.Configuration.IPluginConfiguration
{
    /// <summary>2: new defaults (Square/X closed the map, middle click is auto-run). 3: no master switch any more.</summary>
    public int Version { get; set; } = 5;

    /// <summary>Old master switch (until version 2). Each input now has its own None / Off instead.</summary>
    public bool Enabled { get; set; } = true;
    /// <summary>The one button: aetheryte/shard under the cursor → go there; anywhere else → travel there.</summary>
    public GamepadButtons Button { get; set; } = GamepadButtons.South;
    /// <summary>Optional controller button to hold at the same time (a combo, e.g. L2 + Cross). None = no combo.</summary>
    public GamepadButtons ButtonHold { get; set; } = GamepadButtons.None;
    /// <summary>Same thing with the mouse (cursor on the map) or a keyboard key (mouse over the map).</summary>
    public MouseTrigger MouseButton { get; set; } = MouseTrigger.Left;
    /// <summary>Keys to hold with the mouse button (Ctrl + left click: Lifestream ignores Ctrl clicks on the map).</summary>
    public KeyMods MouseMods { get; set; } = KeyMods.Ctrl;
    /// <summary>Keys to hold with the keyboard key.</summary>
    public KeyMods HotkeyMods { get; set; } = KeyMods.None;
    /// <summary>Target the quest NPC/object when you arrive within talking range (never enemies).</summary>
    public bool TargetOnArrival { get; set; } = true;
    public VirtualKey Hotkey { get; set; } = VirtualKey.NO_KEY;
    public bool UseShardsForTravel { get; set; } = true;
    public bool AutoMount { get; set; } = true;
    /// <summary>Old fixed "mount up when further than" distance; replaced by a time calculation.</summary>
    public int MountDistance { get; set; } = 40;
    public bool AllowFlying { get; set; } = true;
    /// <summary>Allow paid teleports to an aetheryte on the map you're already on (the aethernet is always allowed, it's free).</summary>
    public bool SameMapTeleport { get; set; } = true;
    /// <summary>Unused (an earlier test build calibrated the map view; the maths is now known).</summary>
    public int MapCalibration { get; set; } = -1;
    /// <summary>Which mount Beeline summons: a roulette, or one of your mounts (MountId).</summary>
    public MountChoice MountKind { get; set; } = MountChoice.Roulette;
    public uint MountId { get; set; }
    /// <summary>On foot (no mount on this trip): use Peloton if your job has it and it's ready, else Sprint if ready.</summary>
    public bool SpeedBuffs { get; set; } = true;
    /// <summary>How far from a quest mob to stop (yalms), so it does not pull it.</summary>
    public int MobDistance { get; set; } = 20;
    /// <summary>No safe distance from mobs this many levels (or more) below you: they can't aggro you.</summary>
    public bool IgnoreLowLevelMobs { get; set; } = true;
    public int NoAggroLevelGap { get; set; } = 10;
    public bool DismountOnEnemy { get; set; } = false;
    /// <summary>Targeting an enemy stops the trip (being attacked doesn't). On: you can start the fight.</summary>
    public bool StopOnEnemyTarget { get; set; } = false;
    /// <summary>Show the time left for the current trip in the server info bar (top right).</summary>
    public bool ShowTripTimer { get; set; } = false;
    // Route timing, learned from your own trips (seconds, and yalms per second). The values below are only
    // the starting point; each trip refines them. "Samples" = how many trips were measured.
    public float TeleportSeconds { get; set; } = 12f;
    public int TeleportSamples { get; set; }
    public float AethernetSeconds { get; set; } = 7f;
    public int AethernetSamples { get; set; }
    public float RunSpeed { get; set; } = 6f;
    public int RunSamples { get; set; }
    /// <summary>Running with Peloton kept up (physical ranged jobs can keep it on all the time out of combat).</summary>
    public float PelotonSpeed { get; set; } = 7.2f;
    public int PelotonSamples { get; set; }
    /// <summary>Sprint, and Jog (which follows Sprint and lasts until you change zone or enter combat).</summary>
    public float SprintSpeed { get; set; } = 7.8f;
    public int SprintSamples { get; set; }
    public float SprintSeconds { get; set; } = 20f; // out of combat
    public float JogSpeed { get; set; } = 7.2f;
    public int JogSamples { get; set; }
    /// <summary>How much longer vnavmesh's path on the ground is than the straight line (walls, stairs, cliffs).</summary>
    public float Detour { get; set; } = 1.15f;
    public int DetourSamples { get; set; }
    /// <summary>From casting Mount Roulette to being on the mount (you keep moving meanwhile).</summary>
    public float MountSeconds { get; set; } = 2f;
    public int MountSamples { get; set; }
    public float RideSpeed { get; set; } = 9f;
    public int RideSamples { get; set; }
    public float FlySpeed { get; set; } = 11f;
    public int FlySamples { get; set; }

    public void ResetTimings()
    {
        TeleportSeconds = 12f; AethernetSeconds = 7f;
        RunSpeed = 6f; PelotonSpeed = 7.2f; SprintSpeed = 7.8f; SprintSeconds = 20f; JogSpeed = 7.2f;
        RideSpeed = 9f; FlySpeed = 11f; Detour = 1.15f; MountSeconds = 2f;
        TeleportSamples = AethernetSamples = RunSamples = PelotonSamples = SprintSamples = JogSamples = RideSamples = FlySamples = DetourSamples = MountSamples = 0;
    }

    /// <summary>Write every press and trip step to the Dalamud log at Information level (else Debug).</summary>
    public bool LogPresses { get; set; } = false;
}

/// <summary>FlyingRoulette (1) is no longer in the game; kept so old settings load, treated as Roulette.</summary>
public enum MountChoice { Roulette = 0, FlyingRoulette = 1, Specific = 2 }
public enum MouseTrigger { Off = 0, Middle = 1, Back = 2, Forward = 3, Left = 4, Right = 5 }
[Flags] public enum KeyMods { None = 0, Shift = 1, Ctrl = 2, Alt = 4 }

public sealed unsafe class Plugin : IDalamudPlugin
{
    private const string Command = "/beeline";
    internal const string Tag = "[Beeline]";

    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static IAddonLifecycle         AddonLifecycle  { get; private set; } = null!;
    [PluginService] internal static IGamepadState           Gamepad         { get; private set; } = null!;
    [PluginService] internal static IGameGui                GameGui         { get; private set; } = null!;
    [PluginService] internal static IDataManager            Data            { get; private set; } = null!;
    [PluginService] internal static ICommandManager         Commands        { get; private set; } = null!;
    [PluginService] internal static IChatGui                Chat            { get; private set; } = null!;
    [PluginService] internal static IPluginLog              Log             { get; private set; } = null!;
    [PluginService] internal static IClientState            ClientState     { get; private set; } = null!;
    [PluginService] internal static ICondition              Condition       { get; private set; } = null!;
    [PluginService] internal static IFramework              Framework       { get; private set; } = null!;
    [PluginService] internal static IObjectTable            ObjectTable     { get; private set; } = null!;
    [PluginService] internal static IKeyState               KeyState        { get; private set; } = null!;
    [PluginService] internal static ITargetManager          TargetManager   { get; private set; } = null!;
    [PluginService] internal static IFateTable              Fates           { get; private set; } = null!;
    [PluginService] internal static IPartyList              Party           { get; private set; } = null!;
    [PluginService] internal static IDtrBar                 DtrBar          { get; private set; } = null!;

    internal Configuration Config { get; }
    private static Configuration? _cfg;
    /// <summary>Detailed log: the "write each press" option, or always in a dev build (testing).</summary>
    internal static bool Config_LogPresses => _cfg?.LogPresses == true || PluginInterface.IsDev;

    /// <summary>Hides player names (party members, you) from text that goes to the log.</summary>
    internal static string Redact(string text)
    {
        if (string.IsNullOrEmpty(text))
            return text;
        foreach (var member in Party)
        {
            var name = member.Name.ToString();
            if (name.Length > 0)
                text = text.Replace(name, "(party member)");
        }
        var me = ObjectTable.LocalPlayer?.Name.ToString();
        if (!string.IsNullOrEmpty(me))
            text = text.Replace(me, "(you)");
        return text;
    }

    /// <summary>Trip details: visible in the normal log only when "Write each button press to the log" is on.</summary>
    internal static void Info(string message)
    {
        // Dalamud already prefixes every line with the plugin's name: drop our own tag (it showed twice).
        if (message.StartsWith(Tag + " ", StringComparison.Ordinal))
            message = message[(Tag.Length + 1)..];
        if (Config_LogPresses) Log.Information(message);
        else Log.Debug(message);
    }
    internal readonly Ipc Ipc;
    private readonly System.Threading.Tasks.Task<Places> _places;
    /// <summary>Aetheryte data, built once on a background thread at load (null for the first few ms).</summary>
    internal Places? Places => _places.IsCompletedSuccessfully ? _places.Result : null;
    internal Travel Travel { get; }
    private bool _placesErrorShown;
    private readonly Dismounter _dismounter;
    /// <summary>Link with Giddy Up (null for the few ms before it's created).</summary>
    internal Companion? Companion { get; private set; }

    private readonly WindowSystem _windows = new("Beeline");
    private readonly ConfigWindow _configWindow;

    public Plugin()
    {
        Config  = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        _cfg    = Config;
        if (Config.Version < 2)
        {
            // The old defaults clash with the game: Square/X closes the map, middle click is auto-run.
            if (Config.Button == GamepadButtons.West)
                Config.Button = GamepadButtons.South;
            if (Config.MouseButton == MouseTrigger.Middle)
            {
                Config.MouseButton = MouseTrigger.Left;
                Config.MouseMods   = KeyMods.Ctrl;
            }
            Config.Version = 2;
            PluginInterface.SavePluginConfig(Config);
        }
        // Sprint lasts 20 s out of combat; older builds started from 10 s.
        if (Config.SprintSamples == 0 && Config.SprintSeconds < 15)
            Config.SprintSeconds = 20f;
        if (Config.Version < 3)
        {
            // The master switch is gone: if it was off, turn every input off instead, so nothing changes.
            if (!Config.Enabled)
            {
                Config.Button      = GamepadButtons.None;
                Config.MouseButton = MouseTrigger.Off;
                Config.Hotkey      = VirtualKey.NO_KEY;
                Config.Enabled     = true;
            }
            Config.Version = 3;
            PluginInterface.SavePluginConfig(Config);
        }
        if (Config.Version < 4)
        {
            // Same-map teleports are opt-out (a test build briefly had them off by default).
            Config.SameMapTeleport = true;
            Config.Version = 4;
            PluginInterface.SavePluginConfig(Config);
        }
        if (Config.Version < 5)
        {
            // "Stop the trip when you target an enemy" is now off by default (a test build had it on): with Giddy Up,
            // the trip stops anyway when it's time to dismount for the enemy.
            Config.StopOnEnemyTarget = false;
            Config.Version = 5;
            PluginInterface.SavePluginConfig(Config);
        }
        Ipc     = new Ipc(PluginInterface);
        _places = System.Threading.Tasks.Task.Run(() => new Places(Data));
        Travel  = new Travel(this);
        _dismounter = new Dismounter(this);
        Companion   = new Companion(this);

        _configWindow = new ConfigWindow(this);
        _windows.AddWindow(_configWindow);
        PluginInterface.UiBuilder.Draw         += _windows.Draw;
        PluginInterface.UiBuilder.OpenConfigUi += _configWindow.Toggle;
        PluginInterface.UiBuilder.OpenMainUi   += _configWindow.Toggle;

        // PostUpdate only fires while the map window is open, so this reacts to the map
        // instead of polling every frame.
        AddonLifecycle.RegisterListener(AddonEvent.PostUpdate, "AreaMap", OnMapUpdate);
        // Safety net: right after a game patch Dalamud's window events can stay silent. Until one arrives, Beeline looks
        // for the map itself (see OnFrameworkFallback); it turns itself off as soon as the events work.
        Framework.Update += OnFrameworkFallback;

        Commands.AddHandler(Command, new CommandInfo(OnCommand)
        {
            HelpMessage = "Open the settings.\n/beeline stop → Cancel the current trip.",
        });
    }

    public void Dispose()
    {
        AddonLifecycle.UnregisterListener(AddonEvent.PostUpdate, "AreaMap", OnMapUpdate);
        Framework.Update -= OnFrameworkFallback;
        Commands.RemoveHandler(Command);
        Companion?.Dispose();
        Travel.Dispose();
        _dismounter.Dispose();
        PluginInterface.UiBuilder.Draw         -= _windows.Draw;
        PluginInterface.UiBuilder.OpenConfigUi -= _configWindow.Toggle;
        PluginInterface.UiBuilder.OpenMainUi   -= _configWindow.Toggle;
        _windows.RemoveAllWindows();
    }

    internal void SaveConfig() => PluginInterface.SavePluginConfig(Config);

    internal static bool IsPluginLoaded(string internalName)
        => PluginInterface.InstalledPlugins.Any(p => p.InternalName == internalName && p.IsLoaded);

    private void OnCommand(string command, string args)
    {
        if (args.Trim().Equals("stop", StringComparison.OrdinalIgnoreCase))
            Travel.Cancel("cancelled");
        else
            _configWindow.Toggle();
    }

    internal static readonly GamepadButtons[] AllPadButtons =
    [
        GamepadButtons.South, GamepadButtons.East, GamepadButtons.West, GamepadButtons.North,
        GamepadButtons.DpadUp, GamepadButtons.DpadDown, GamepadButtons.DpadLeft, GamepadButtons.DpadRight,
        GamepadButtons.L1, GamepadButtons.L2, GamepadButtons.L3, GamepadButtons.R1, GamepadButtons.R2, GamepadButtons.R3,
        GamepadButtons.Select, GamepadButtons.Start,
    ];

    /// <summary>PlayStation / Xbox names.</summary>
    internal static string ButtonName(GamepadButtons b) => b switch
    {
        GamepadButtons.None      => "None",
        GamepadButtons.South     => "Cross / A",   GamepadButtons.East  => "Circle / B",
        GamepadButtons.West      => "Square / X",  GamepadButtons.North => "Triangle / Y",
        GamepadButtons.DpadUp    => "D-pad up",    GamepadButtons.DpadDown  => "D-pad down",
        GamepadButtons.DpadLeft  => "D-pad left",  GamepadButtons.DpadRight => "D-pad right",
        GamepadButtons.L1        => "L1 / LB",     GamepadButtons.R1 => "R1 / RB",
        GamepadButtons.L2        => "L2 / LT",     GamepadButtons.R2 => "R2 / RT",
        GamepadButtons.L3        => "L3 / left stick press", GamepadButtons.R3 => "R3 / right stick press",
        GamepadButtons.Select    => "Create / View",          GamepadButtons.Start => "Options / Menu",
        _ => b.ToString(),
    };

    internal static string ModsName(KeyMods m)
    {
        var parts = new List<string>();
        if (m.HasFlag(KeyMods.Ctrl))  parts.Add("Ctrl");
        if (m.HasFlag(KeyMods.Shift)) parts.Add("Shift");
        if (m.HasFlag(KeyMods.Alt))   parts.Add("Alt");
        return string.Join(" + ", parts);
    }

    // The press that opened or re-centred the map (e.g. "Map" from the quest journal) must not start a trip:
    // ignore presses shortly after the map became visible (or after a gap in its updates), and presses
    // made while another window (the journal) has the focus.
    private long _mapLastUpdate;
    private long _mapOpenedAt;
    private bool _mapWasVisible;
    private bool _awaitRelease;
    private System.Numerics.Vector2 _baselineCoords;
    private uint _baselineMapId;
    private bool _cursorMoved;
    private const int MapClosedGapMs = 200;
    private const int IgnoreAfterOpenMs = 500;

    // ---------------- Fallback when Dalamud's window events don't arrive (e.g. right after a game patch) ----------------

    private bool _mapEventsWork;      // a real map event arrived: the fallback is off for the session
    private long _fallbackCheckedAt;
    private bool _fallbackMapOpen;
    private bool _fallbackAnnounced;

    private void OnFrameworkFallback(IFramework framework)
    {
        if (_mapEventsWork)
        {
            Framework.Update -= OnFrameworkFallback;
            return;
        }
        // Map closed: look for it at most 10 times a second. Open: run the usual map check every frame.
        var now = Environment.TickCount64;
        if (!_fallbackMapOpen && now - _fallbackCheckedAt < 100)
            return;
        _fallbackCheckedAt = now;
        var map = (AddonAreaMap*)GameGui.GetAddonByName("AreaMap").Address;
        _fallbackMapOpen = map != null && map->AtkUnitBase.IsVisible;
        if (map == null)
            return;
        if (_fallbackMapOpen && !_fallbackAnnounced)
        {
            _fallbackAnnounced = true;
            Log.Information($"{Tag} Map events aren't arriving (Dalamud not updated for this game patch yet?): watching the map directly until they do");
        }
        HandleMap(map);
    }

    private void OnMapUpdate(AddonEvent type, AddonArgs args)
    {
        if (!_mapEventsWork)
        {
            _mapEventsWork = true;
            if (_fallbackAnnounced)
                Log.Information($"{Tag} Map events work again: direct map watching turned off");
            else
                Info($"{Tag} Map events work: the safety net isn't needed");
        }
        HandleMap((AddonAreaMap*)args.Addon.Address);
    }

    private void HandleMap(AddonAreaMap* map)
    {
        if (map == null)
            return;

        var now = Environment.TickCount64;
        var visible = map->AtkUnitBase.IsVisible;
        var justOpened = (visible && !_mapWasVisible) || now - _mapLastUpdate > MapClosedGapMs;
        _mapWasVisible = visible;
        _mapLastUpdate = now;
        if (!visible)
            return; // closed map: nothing to do
        if (justOpened)
            _mapOpenedAt = now;

        // The game only updates the cursor position when the cursor moves: after the map opens or switches
        // to another map (e.g. "Map" from the journal), it still holds the old one until you move it.
        var coords = new System.Numerics.Vector2(map->MouseXInteger + map->MouseXDecimal / 10f, map->MouseYInteger + map->MouseYDecimal / 10f);
        var shownMap = AgentMap.Instance() != null ? AgentMap.Instance()->SelectedMapId : 0;
        if (justOpened || shownMap != _baselineMapId)
        {
            _baselineCoords = coords;
            _baselineMapId  = shownMap;
            _cursorMoved    = false;
        }
        else if (coords != _baselineCoords)
            _cursorMoved = true;

        // Controller: holding the button makes the game repeat it, so after a press wait until it is released.
        var padPressed = false;
        if (_awaitRelease)
        {
            if (Gamepad.Raw(Config.Button) == 0)
                _awaitRelease = false;
        }
        else if (Config.Button != GamepadButtons.None && Gamepad.Pressed(Config.Button) != 0
                 && (Config.ButtonHold == GamepadButtons.None || Config.ButtonHold == Config.Button || Gamepad.Raw(Config.ButtonHold) != 0))
        {
            padPressed = true;
            _awaitRelease = true;
        }
        // Mouse and keyboard: react when the button/key goes down.
        var mousePressed = MouseClicked(map);
        var keyPressed   = HotkeyWentDown();

        if (!(padPressed || mousePressed || keyPressed))
            return;

        if (now - _mapOpenedAt < IgnoreAfterOpenMs)
        {
            Info($"{Tag} Button ignored: the map has just opened");
            return;
        }

        // Mouse / keyboard: the mouse must be over the map (that's where the cursor position comes from).
        if (!padPressed && !MouseOverMap(map))
        {
            Info($"{Tag} {(mousePressed ? "Mouse button" : "Key")} ignored: the mouse is not over the map");
            return;
        }

        // Controller: only when the map itself has the focus and shows its crosshair. Otherwise the button is meant for
        // the game (the same button targets things), and the map's cursor readout would be stale anyway.
        var focused = RaptureAtkUnitManager.Instance()->AtkUnitManager.FocusedAddon;
        if (padPressed && (focused != &map->AtkUnitBase || CrosshairPoint(map) == null))
        {
            Info($"{Tag} Button ignored: the map isn't focused ({(focused == null ? "the game has it" : $"\"{focused->NameString}\" has it")})");
            return;
        }

        var agent = AgentMap.Instance();
        if (agent == null)
            return;

        var tooltip   = ReadTooltip();
        // A mouse click hides the tooltip before the button comes back up: use the one seen when it went down.
        if (mousePressed && string.IsNullOrWhiteSpace(tooltip))
            tooltip = _tooltipAtMouseDown;
        var territory = agent->SelectedTerritoryId;
        var mapId     = agent->SelectedMapId;
        // (coords: the decimal part is stored in tenths, 12.4 → 12 and 4; the ClientStructs helper divides by 100.)
        Info($"{Tag} {(padPressed ? "Controller" : mousePressed ? "Mouse" : "Key")} on map: tooltip=\"{Redact(tooltip)}\", icon radius={IconHitRadius(map, agent):0} y, territory={territory}, map={mapId}, cursor=({coords.X:0.00}, {coords.Y:0.00}){(_cursorMoved ? "" : " (not moved since the map opened)")}");

        var places = Places;
        if (places == null)
        {
            if (_places.IsFaulted && !_placesErrorShown)
            {
                _placesErrorShown = true;
                Log.Error(_places.Exception!, $"{Tag} Could not read the aetheryte data");
                Chat.PrintError("Beeline: could not read the aetheryte data (see /xllog). Please report it.");
            }
            return;
        }

        // Cursor on an aetheryte or a shard: go exactly there.
        if (!string.IsNullOrWhiteSpace(tooltip))
        {
            if (places.FindShard(tooltip, territory) is { } shard)
            {
                // Housing district shard: the aethernet works only from a shard of that district; otherwise walk there.
                if (!shard.Residential || ResidentialHop(shard))
                {
                    Travel.Cancel("replaced", quiet: true);
                    if (!shard.Residential)
                        ShardTeleport(places, shard);
                    return;
                }
            }
            if (places.FindMain(tooltip, territory) is { } main)
            {
                Travel.Cancel("replaced", quiet: true);
                AetheryteTeleport(places, main);
                return;
            }
        }

        // Anywhere else: quickest way there.
        // Mouse / keyboard: the spot under the mouse, worked out from the map's view. Works on an unfocused map too (the
        // game only updates its cursor readout while the map is focused) and is more precise than that readout.
        // Controller: the same maths at the map's crosshair (where the controller cursor points), if it's shown.
        Vector2? world = null;
        Vector2? pointer = padPressed ? CrosshairPoint(map) : MousePoint();
        var what = padPressed ? "Crosshair" : "Mouse";
        if (pointer is { } pt && Plugin.Data.GetExcelSheet<Lumina.Excel.Sheets.Map>().TryGetRow(mapId, out var mapRow)
            && WorldAt(map, agent, mapRow, pt) is { } computed)
        {
            var readout = new Vector2(Places.MapToWorld(coords.X, mapRow.SizeFactor, mapRow.OffsetX), Places.MapToWorld(coords.Y, mapRow.SizeFactor, mapRow.OffsetY));
            var diff = Vector2.Distance(computed, readout);
            if (!padPressed || diff <= 10)
            {
                world = computed;
                Info($"{Tag} {what} position ({computed.X:0.0}, {computed.Y:0.0}), {diff:0} y from the game's readout (which can be stale)");
            }
            else
                Info($"{Tag} {what} position {diff:0} y from the game's readout: using the game's");
        }

        Travel.Start(territory, mapId, coords, tooltip, IconHitRadius(map, agent), cursorStale: !_cursorMoved && world == null, world: world);
    }

    private void AetheryteTeleport(Places places, Place main)
    {
        try
        {
            if (Ipc.LifestreamBusy())
            {
                Info($"{Tag} Lifestream is busy, ignoring.");
                return;
            }
            var activeId = Ipc.ActiveAetheryte();
            if (activeId == main.Id) { Chat.Print("Beeline: you are already here."); return; }

            // On that city's aethernet already: the aethernet is free and quicker than a teleport.
            if (activeId != 0 && main.Group != 0 && places.ById(activeId) is { } active && active.Group == main.Group)
            {
                Info($"{Tag} Aethernet to {main.Name} (#{main.Id}) from #{activeId}");
                if (!Ipc.AethernetTo(main.Id))
                    Chat.PrintError($"Beeline: could not take the aethernet to {main.Name}.");
                return;
            }
            Info($"{Tag} Teleport to {main.Name} (#{main.Id})");
            if (!Ipc.TeleportTo(main.Id))
                Chat.PrintError($"Beeline: could not teleport to {main.Name}.");
        }
        catch (Exception e)
        {
            Log.Warning($"{Tag} Lifestream call failed (is Lifestream installed and enabled?): {e.Message}");
            Chat.PrintError("Beeline: Lifestream is not available.");
        }
    }

    /// <summary>Takes the housing district aethernet if you're at one of that district's shards. False if you're not.</summary>
    private bool ResidentialHop(Place shard)
    {
        try
        {
            var active = Ipc.ActiveResidential();
            if (active == 0 || Places?.Residential(active) is not { } at || at.Group != shard.Group)
                return false;
            if (active == shard.Id) { Chat.Print("Beeline: you are already here."); return true; }
            Info($"{Tag} Residential aethernet to {shard.Name} (#{shard.Id}) from #{active}");
            if (!Ipc.ResidentialAethernetTo(shard.Id))
                Chat.PrintError($"Beeline: could not take the aethernet to {shard.Name}.");
            return true;
        }
        catch { return false; }
    }

    private void ShardTeleport(Places places, Place shard)
    {
        try
        {
            if (Ipc.LifestreamBusy())
            {
                Info($"{Tag} Lifestream is busy, ignoring.");
                return;
            }

            // Already standing at an aetheryte/shard of the same network: travel directly.
            var activeId = Ipc.ActiveAetheryte();
            if (activeId != 0 && places.ById(activeId) is { } active && active.Group == shard.Group)
            {
                if (activeId == shard.Id) { Chat.Print("Beeline: you are already here."); return; }
                Info($"{Tag} Aethernet to {shard.Name} (#{shard.Id}) from #{activeId}");
                if (!Ipc.AethernetTo(shard.Id))
                    Chat.PrintError($"Beeline: could not take the aethernet to {shard.Name}.");
                return;
            }

            // Otherwise: teleport to that city's main aetheryte, then take the aethernet.
            var master = places.MasterOf(shard);
            if (master == null)
            {
                Info($"{Tag} No main aetheryte found for {shard.Name}, using /li {shard.Name}");
                Ipc.LifestreamCommand(shard.Name);
                return;
            }
            Info($"{Tag} Teleport to {master.Name} (#{master.Id}) then aethernet to {shard.Name} (#{shard.Id})");
            Travel.StartHop(master, shard);
        }
        catch (Exception e)
        {
            Log.Warning($"{Tag} Lifestream call failed (is Lifestream installed and enabled?): {e.Message}");
            Chat.PrintError("Beeline: Lifestream is not available.");
        }
    }

    // ---------------- Mouse and keyboard ----------------

    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vKey);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern nint GetForegroundWindow();

    private bool _mouseWasDown;
    private bool _mouseDownOnMap;
    private System.Numerics.Vector2 _mouseDownAt;
    private string _tooltipAtMouseDown = "";
    private bool _keyWasDown;

    private static bool GameHasFocus()
    {
        var fw = FFXIVClientStructs.FFXIV.Client.System.Framework.Framework.Instance();
        return fw != null && fw->GameWindow != null && GetForegroundWindow() == fw->GameWindow->WindowHandle;
    }

    private static bool IsDown(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    /// <summary>
    /// A click on the map: the button goes down and up over the map without dragging it (so moving the map
    /// with a left drag doesn't count), with the chosen modifier held.
    /// </summary>
    private bool MouseClicked(AddonAreaMap* map)
    {
        var vk = Config.MouseButton switch
        {
            MouseTrigger.Left    => 0x01, // VK_LBUTTON
            MouseTrigger.Right   => 0x02, // VK_RBUTTON
            MouseTrigger.Middle  => 0x04, // VK_MBUTTON
            MouseTrigger.Back    => 0x05, // VK_XBUTTON1
            MouseTrigger.Forward => 0x06, // VK_XBUTTON2
            _ => 0,
        };
        if (vk == 0)
        {
            _mouseWasDown = false;
            return false;
        }

        var down = IsDown(vk) && GameHasFocus();
        var pos = Dalamud.Bindings.ImGui.ImGui.GetIO().MousePos;
        var clicked = false;
        if (down && !_mouseWasDown)
        {
            _mouseDownOnMap = MouseOverMap(map);
            _mouseDownAt = pos;
            _tooltipAtMouseDown = _mouseDownOnMap ? ReadTooltip() : "";
        }
        else if (!down && _mouseWasDown)
        {
            clicked = _mouseDownOnMap && System.Numerics.Vector2.Distance(pos, _mouseDownAt) < 6f && ModsHeld(Config.MouseMods);
        }
        _mouseWasDown = down;
        return clicked;
    }

    /// <summary>Every chosen key (Shift / Ctrl / Alt) is held.</summary>
    private static bool ModsHeld(KeyMods m)
        => (!m.HasFlag(KeyMods.Shift) || IsDown(0x10))
        && (!m.HasFlag(KeyMods.Ctrl)  || IsDown(0x11))
        && (!m.HasFlag(KeyMods.Alt)   || IsDown(0x12));

    private bool HotkeyWentDown()
    {
        var key = Config.Hotkey;
        var down = key != VirtualKey.NO_KEY && KeyState.IsVirtualKeyValid(key) && KeyState[key] && ModsHeld(Config.HotkeyMods)
                   && !Dalamud.Bindings.ImGui.ImGui.GetIO().WantTextInput
                   && !RaptureAtkModule.Instance()->AtkModule.IsTextInputActive();
        var pressed = down && !_keyWasDown;
        _keyWasDown = down;
        return pressed;
    }

    /// <summary>The mouse is over the map window (and not over a plugin window).</summary>
    private static bool MouseOverMap(AddonAreaMap* map)
    {
        var io = Dalamud.Bindings.ImGui.ImGui.GetIO();
        if (io.WantCaptureMouse)
            return false;
        var unit = &map->AtkUnitBase;
        if (unit->RootNode == null)
            return false;
        var mouse = io.MousePos - Dalamud.Bindings.ImGui.ImGui.GetMainViewport().Pos;
        var w = unit->RootNode->Width * unit->Scale;
        var h = unit->RootNode->Height * unit->Scale;
        return mouse.X >= unit->X && mouse.Y >= unit->Y && mouse.X <= unit->X + w && mouse.Y <= unit->Y + h;
    }

    internal static string MouseName(MouseTrigger m) => m switch
    {
        MouseTrigger.Left    => "Left click",
        MouseTrigger.Right   => "Right click",
        MouseTrigger.Middle  => "Middle click",
        MouseTrigger.Back    => "Back side button",
        MouseTrigger.Forward => "Forward side button",
        _ => "Off",
    };

    /// <summary>The mouse position on the game window.</summary>
    private static Vector2 MousePoint()
        => Dalamud.Bindings.ImGui.ImGui.GetIO().MousePos - Dalamud.Bindings.ImGui.ImGui.GetMainViewport().Pos;

    /// <summary>Centre of the map's crosshair on screen (shown with a controller), or null if it isn't shown.</summary>
    private static Vector2? CrosshairPoint(AddonAreaMap* map)
    {
        var cross = map->CrosshairImageNode;
        if (cross == null || !cross->AtkResNode.IsVisible())
            return null;
        var n = &cross->AtkResNode;
        var scale = map->AtkUnitBase.Scale;
        return new Vector2(n->ScreenX + n->Width * n->ScaleX * scale / 2f, n->ScreenY + n->Height * n->ScaleY * scale / 2f);
    }

    /// <summary>
    /// The visible map image sits a little right of and below the middle of its frame (local units). Measured on
    /// 189 cursor samples over Azys Lla, Limsa Lominsa and La Noscea at several zooms and window sizes.
    /// </summary>
    private static readonly Vector2 MapImageShift = new(11f, 20.5f);

    /// <summary>
    /// World position (X, Z) under a screen point of the map (the mouse, or the controller crosshair), from the map's view:
    /// world = ((point - centre) / UI scale - image shift) / (zoom * map size) + scroll offset / map size - map offset.
    /// Works whether the map is focused or not (the game's own cursor readout only updates while it's focused), and is
    /// as precise as that readout (median 5 yalms off it, which is the readout's own rounding).
    /// </summary>
    private static Vector2? WorldAt(AddonAreaMap* map, AgentMap* agent, Lumina.Excel.Sheets.Map mapRow, Vector2 screenPoint)
    {
        var view = map->AreaMap.ComponentMap;
        if (view == null || view->OwnerNode == null)
            return null;
        var node = &view->OwnerNode->AtkResNode;
        var uiScale = map->AtkUnitBase.Scale;
        var zoom = map->AreaMap.MapScale;
        var size = agent->SelectedMapSizeFactorFloat;
        if (uiScale <= 0 || zoom <= 0 || size <= 0)
            return null;
        var centre = new Vector2(node->ScreenX + node->Width * uiScale / 2f, node->ScreenY + node->Height * uiScale / 2f);
        var local = (screenPoint - centre) / uiScale - MapImageShift;
        var scroll = new Vector2(map->AreaMap.MapOffsetX, map->AreaMap.MapOffsetY);
        return local / (zoom * size) + scroll / size - new Vector2(mapRow.OffsetX, mapRow.OffsetY);
    }

    /// <summary>
    /// How much ground (yalms) a map icon covers around its centre: about 0.6 map units zoomed out and 0.2 zoomed in
    /// (the values Lifestream uses for shards), halved on city maps, with some margin.
    /// </summary>
    private static float IconHitRadius(AddonAreaMap* map, AgentMap* agent)
    {
        var zoom = 0.5f;
        var slider = map->ZoomSlider;
        if (slider != null && slider->MaxValue > slider->MinValue)
            zoom = Math.Clamp((slider->Value - slider->MinValue) / (float)(slider->MaxValue - slider->MinValue), 0f, 1f);
        var mapUnits = 0.6f * (1 - zoom) + 0.2f * zoom;
        var sizeFactor = agent->SelectedMapSizeFactorFloat > 0 ? agent->SelectedMapSizeFactorFloat : 1f;
        return mapUnits * 50f / sizeFactor * 1.25f;
    }

    private static string ReadTooltip()
    {
        var tooltip = (AtkUnitBase*)GameGui.GetAddonByName("Tooltip").Address;
        if (tooltip == null || !tooltip->IsVisible || tooltip->UldManager.NodeListCount < 3)
            return "";
        var node = tooltip->UldManager.NodeList[2];
        if (node == null || node->Type != NodeType.Text)
            return "";
        return ((AtkTextNode*)node)->NodeText.ToString();
    }
}
