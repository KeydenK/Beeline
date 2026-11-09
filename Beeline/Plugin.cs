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
    /// <summary>2: new defaults (Square/X closed the map and middle click is auto-run in game).</summary>
    public int Version { get; set; } = 2;

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
    public int MountDistance { get; set; } = 40;
    public bool AllowFlying { get; set; } = true;
    /// <summary>How far from a quest mob to stop (yalms), so it does not pull it.</summary>
    public int MobDistance { get; set; } = 20;
    /// <summary>No safe distance from mobs this many levels (or more) below you: they can't aggro you.</summary>
    public bool IgnoreLowLevelMobs { get; set; } = true;
    public int NoAggroLevelGap { get; set; } = 10;
    public bool DismountOnEnemy { get; set; } = false;
    /// <summary>How many yalms of riding a teleport is worth when comparing routes (~5 s cast + loading).</summary>
    public int TeleportCost { get; set; } = 150;
    public int AethernetCost { get; set; } = 60;

    /// <summary>Write every press and trip step to the Dalamud log at Information level (else Debug).</summary>
    public bool LogPresses { get; set; } = false;
}

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

    internal Configuration Config { get; }
    private static Configuration? _cfg;
    internal static bool Config_LogPresses => _cfg?.LogPresses == true;

    /// <summary>Trip details: visible in the normal log only when "Write each button press to the log" is on.</summary>
    internal static void Info(string message)
    {
        if (_cfg?.LogPresses == true) Log.Information(message);
        else Log.Debug(message);
    }
    internal readonly Ipc Ipc;
    private readonly System.Threading.Tasks.Task<Places> _places;
    /// <summary>Aetheryte data, built once on a background thread at load (null for the first few ms).</summary>
    internal Places? Places => _places.IsCompletedSuccessfully ? _places.Result : null;
    internal Travel Travel { get; }
    private bool _placesErrorShown;
    private readonly Dismounter _dismounter;

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
        Ipc     = new Ipc(PluginInterface);
        _places = System.Threading.Tasks.Task.Run(() => new Places(Data));
        Travel  = new Travel(this);
        _dismounter = new Dismounter(this);

        _configWindow = new ConfigWindow(this);
        _windows.AddWindow(_configWindow);
        PluginInterface.UiBuilder.Draw         += _windows.Draw;
        PluginInterface.UiBuilder.OpenConfigUi += _configWindow.Toggle;
        PluginInterface.UiBuilder.OpenMainUi   += _configWindow.Toggle;

        // PostUpdate only fires while the map window is open, so this reacts to the map
        // instead of polling every frame.
        AddonLifecycle.RegisterListener(AddonEvent.PostUpdate, "AreaMap", OnMapUpdate);

        Commands.AddHandler(Command, new CommandInfo(OnCommand)
        {
            HelpMessage = "Open the settings.\n/beeline stop → Cancel the current trip.",
        });
    }

    public void Dispose()
    {
        AddonLifecycle.UnregisterListener(AddonEvent.PostUpdate, "AreaMap", OnMapUpdate);
        Commands.RemoveHandler(Command);
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

    private void OnMapUpdate(AddonEvent type, AddonArgs args)
    {
        var map = (AddonAreaMap*)args.Addon.Address;
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
        else if (Gamepad.Pressed(Config.Button) != 0
                 && (Config.ButtonHold == GamepadButtons.None || Config.ButtonHold == Config.Button || Gamepad.Raw(Config.ButtonHold) != 0))
        {
            padPressed = true;
            _awaitRelease = true;
        }
        // Mouse and keyboard: react when the button/key goes down.
        var mousePressed = MouseClicked(map);
        var keyPressed   = HotkeyWentDown();

        if (!Config.Enabled || !(padPressed || mousePressed || keyPressed))
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

        var focused = RaptureAtkUnitManager.Instance()->AtkUnitManager.FocusedAddon;
        if (padPressed && focused != null && focused != &map->AtkUnitBase)
        {
            Info($"{Tag} Button ignored: \"{focused->NameString}\" has the focus, not the map");
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
        Info($"{Tag} {(padPressed ? "Controller" : mousePressed ? "Mouse" : "Key")} on map: tooltip=\"{tooltip}\", icon radius={IconHitRadius(map, agent):0} y, territory={territory}, map={mapId}, cursor=({coords.X:0.00}, {coords.Y:0.00}){(_cursorMoved ? "" : " (not moved since the map opened)")}");

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
                Travel.Cancel("replaced", quiet: true);
                ShardTeleport(places, shard);
                return;
            }
            if (places.FindMain(tooltip, territory) is { } main)
            {
                Travel.Cancel("replaced", quiet: true);
                AetheryteTeleport(places, main);
                return;
            }
        }

        // Anywhere else: quickest way there.
        Travel.Start(territory, mapId, coords, tooltip, IconHitRadius(map, agent), cursorStale: !_cursorMoved);
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
