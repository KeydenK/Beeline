using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.GamePad;
using Dalamud.Game.ClientState.Keys;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Components;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;

namespace Beeline;

/// <summary>
/// Settings: a short status header (requirements, current trip), then one tab per topic. Long explanations live in
/// (?) markers so the window stays compact.
/// </summary>
internal sealed unsafe class ConfigWindow : Window
{
    private const float ContentWidth = 440;
    private readonly Plugin _plugin;
    private long _depsCheckedAt = long.MinValue / 2;
    private bool _lifestream, _vnav, _giddyUp;

    public ConfigWindow(Plugin plugin)
        : base("Beeline###BeelineConfig", ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoCollapse)
        => _plugin = plugin;

    private Configuration Config => _plugin.Config;

    public override void Draw()
    {
        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + ContentWidth * ImGuiHelpers.GlobalScale);
        try
        {
            DrawStatus();
            ImGuiHelpers.ScaledDummy(4);

            using var tabs = ImRaii.TabBar("BeelineTabs");
            if (!tabs)
                return;
            using (var tab = ImRaii.TabItem("Controls"))
                if (tab) DrawControls();
            using (var tab = ImRaii.TabItem("Travel"))
                if (tab) DrawTravel();
            using (var tab = ImRaii.TabItem("Questing"))
                if (tab) DrawQuesting();
            using (var tab = ImRaii.TabItem("Advanced"))
                if (tab) DrawAdvanced();
        }
        finally
        {
            ImGui.PopTextWrapPos();
        }
    }

    // ---------------- Status ----------------

    private void DrawStatus()
    {
        // Listing installed plugins allocates: refresh once a second, not every frame.
        if (Environment.TickCount64 - _depsCheckedAt > 1000)
        {
            _depsCheckedAt = Environment.TickCount64;
            _lifestream = Plugin.IsPluginLoaded("Lifestream");
            _vnav       = Plugin.IsPluginLoaded("vnavmesh");
            _giddyUp    = Plugin.IsPluginLoaded("GiddyUp");
        }
        Requirement("Lifestream", _lifestream, "needed for teleports and the aethernet");
        ImGui.SameLine(0, 18 * ImGuiHelpers.GlobalScale);
        Requirement("vnavmesh", _vnav, "needed to move you there");
        if (_giddyUp)
        {
            StatusLine(ImGuiColors.HealerGreen, FontAwesomeIcon.Horse, "Giddy Up found");
            Help("Giddy Up handles Sprint, Peloton and dismounting for enemies (per its own settings); Beeline still "
               + "chooses when to mount on a trip. Beeline shares its learned speeds with it.");
        }

        if (_plugin.Travel.IsActive)
        {
            StatusLine(ImGuiColors.DalamudYellow, FontAwesomeIcon.Route, $"Trip: {_plugin.Travel.Status}");
            ImGui.SameLine();
            if (ImGui.SmallButton("Stop"))
                _plugin.Travel.Cancel("cancelled");
        }
    }

    private static void Requirement(string name, bool ok, string why)
    {
        if (ok)
            StatusLine(ImGuiColors.HealerGreen, FontAwesomeIcon.CheckCircle, name);
        else
        {
            StatusLine(ImGuiColors.DalamudRed, FontAwesomeIcon.ExclamationTriangle, $"{name} missing");
            Help(why);
        }
    }

    // ---------------- Controls ----------------

    private void DrawControls()
    {
        Hint("Open the map, point at something and press: on an aetheryte or a shard you go there, anywhere else Beeline "
           + "takes you there the quickest way. Moving your character stops the trip.");
        ImGuiHelpers.ScaledDummy(2);

        // Controller
        var b = Config.Button;
        if (PadCombo("##pad", ref b, allowNone: true, noneLabel: "Off"))
        {
            Config.Button = b;
            _plugin.SaveConfig();
        }
        ImGui.SameLine();
        if (Config.Button != GamepadButtons.None)
        {
            var hold = Config.ButtonHold;
            if (PadCombo("##hold", ref hold, allowNone: true, noneLabel: "no combo", prefix: "hold "))
            {
                Config.ButtonHold = hold;
                _plugin.SaveConfig();
            }
            ImGui.SameLine();
        }
        ImGui.TextUnformatted("Controller");
        Help("Any controller button. Add a second button to hold for a combo (for example L2 + Cross).");
        if (Config.ButtonHold == GamepadButtons.None)
        {
            if (Config.Button is GamepadButtons.West or GamepadButtons.East)
                Warn("On the map, Square / X and Circle / B close it. Use a combo, or another button.");
            else if (Config.Button == GamepadButtons.North)
                Warn("On the map, Triangle / Y switches between the city and region maps. Use a combo, or another button.");
        }

        // Mouse
        ImGui.SetNextItemWidth(170 * ImGuiHelpers.GlobalScale);
        using (var combo = ImRaii.Combo("##mouse", Plugin.MouseName(Config.MouseButton)))
        {
            if (combo)
                foreach (var m in Enum.GetValues<MouseTrigger>())
                    if (ImGui.Selectable(Plugin.MouseName(m), m == Config.MouseButton))
                    {
                        Config.MouseButton = m;
                        _plugin.SaveConfig();
                    }
        }
        if (Config.MouseButton != MouseTrigger.Off)
        {
            var mm = Config.MouseMods;
            if (ModsCheckboxes("mouse", ref mm))
            {
                Config.MouseMods = mm;
                _plugin.SaveConfig();
            }
        }
        ImGui.SameLine();
        ImGui.TextUnformatted("Mouse");
        Help("Point at the spot with the mouse, then click. Dragging the map doesn't count. Lifestream ignores Ctrl clicks, so Ctrl + left click doesn't clash with it.");
        if (Config.MouseMods == KeyMods.None)
        {
            if (Config.MouseButton == MouseTrigger.Middle)
                Warn("Middle click is auto-run in game. Add Ctrl, Shift or Alt, or pick another button.");
            else if (Config.MouseButton is MouseTrigger.Left or MouseTrigger.Right)
                Warn("The map also uses this click (an aetheryte opens the teleport window, right click opens a menu). Add Ctrl, Shift or Alt.");
        }

        // Keyboard
        ImGui.SetNextItemWidth(170 * ImGuiHelpers.GlobalScale);
        using (var combo = ImRaii.Combo("##key", KeyName(Config.Hotkey)))
        {
            if (combo)
            {
                if (ImGui.Selectable("Off", Config.Hotkey == VirtualKey.NO_KEY))
                {
                    Config.Hotkey = VirtualKey.NO_KEY;
                    _plugin.SaveConfig();
                }
                foreach (var k in Plugin.KeyState.GetValidVirtualKeys())
                {
                    if (k is VirtualKey.LBUTTON or VirtualKey.RBUTTON or VirtualKey.MBUTTON or VirtualKey.XBUTTON1 or VirtualKey.XBUTTON2
                          or VirtualKey.SHIFT or VirtualKey.CONTROL or VirtualKey.MENU or VirtualKey.LSHIFT or VirtualKey.RSHIFT
                          or VirtualKey.LCONTROL or VirtualKey.RCONTROL or VirtualKey.LMENU or VirtualKey.RMENU or VirtualKey.ESCAPE)
                        continue;
                    if (ImGui.Selectable(KeyName(k), k == Config.Hotkey))
                    {
                        Config.Hotkey = k;
                        _plugin.SaveConfig();
                    }
                }
            }
        }
        if (Config.Hotkey != VirtualKey.NO_KEY)
        {
            var km = Config.HotkeyMods;
            if (ModsCheckboxes("key", ref km))
            {
                Config.HotkeyMods = km;
                _plugin.SaveConfig();
            }
        }
        ImGui.SameLine();
        ImGui.TextUnformatted("Keyboard");
        Help("Press it while the mouse is over the map. Pick a key the game doesn't use, or both will happen.");

        if (Config.Button == GamepadButtons.None && Config.MouseButton == MouseTrigger.Off && Config.Hotkey == VirtualKey.NO_KEY)
            Warn("Everything is off: Beeline won't react on the map.");
    }

    // ---------------- Travel ----------------

    private void DrawTravel()
    {
        Check("Use aethernet shards to get closer", () => Config.UseShardsForTravel, v => Config.UseShardsForTravel = v,
              "Routes may teleport to a city and take the aethernet to the shard closest to the spot.");
        Check("Paid teleports on your current map (shards stay free)", () => Config.SameMapTeleport, v => Config.SameMapTeleport = v,
              "On: Beeline may teleport (costs gil) to another aetheryte of the map you're already on when that's faster. "
              + "Off, to save gil: on your current map it only walks, rides, flies, or takes the aethernet shards, which are free and always used.");

        ImGuiHelpers.ScaledDummy(4);
        Header("Mount and speed");
        Check("Summon a mount when it's faster", () => Config.AutoMount, v => Config.AutoMount = v,
              "Summoned while you keep moving, whenever riding or flying gets you there sooner than staying on foot "
              + "(from the speeds Beeline learned). Turn it off if you'd rather mount yourself or another plugin does it.");
        if (Config.AutoMount)
            DrawMountPicker();
        Check("Fly when the zone allows it", () => Config.AllowFlying, v => Config.AllowFlying = v,
              "Needs all the aether currents of the zone.");
        Check("Sprint or Peloton on foot", () => Config.SpeedBuffs, v => Config.SpeedBuffs = v,
              "Sprint when it's ready (you then keep Jog until you change map, mount up or fight), otherwise Peloton if your job has it.");
        if (_giddyUp)
            Hint("Giddy Up is installed: when its Sprint / Peloton options are on, it handles them during trips instead.");
        Hint("Other plugins' auto-mount, auto-dismount or cancel-mount-cast features can knock you off your mount when Beeline takes off. "
           + "Turn them off: Beeline mounts you itself.");
    }

    // ---------------- Mount picker ----------------

    private List<(uint Id, string Name)>? _mounts; // your unlocked mounts, built when the list is opened
    private string _mountFilter = "";

    private uint _labelFor = uint.MaxValue;
    private string _label = "Mount Roulette";

    /// <summary>Name of the chosen mount, looked up only when the choice changes (not every frame).</summary>
    private string MountLabel()
    {
        var key = Config.MountKind == MountChoice.Specific ? Config.MountId : 0;
        if (key != _labelFor)
        {
            _labelFor = key;
            _label = key != 0 ? MountName(key) : "Mount Roulette";
        }
        return _label;
    }

    private static string MountName(uint id)
    {
        if (!Plugin.Data.GetExcelSheet<Lumina.Excel.Sheets.Mount>().TryGetRow(id, out var m))
            return $"Mount #{id}";
        var n = m.Singular.ExtractText();
        return n.Length > 0 ? char.ToUpper(n[0]) + n[1..] : $"Mount #{id}";
    }

    private void DrawMountPicker()
    {
        ImGui.Indent();
        ImGui.SetNextItemWidth(220 * ImGuiHelpers.GlobalScale);
        using (var combo = ImRaii.Combo("Mount", MountLabel(), ImGuiComboFlags.HeightLarge))
        {
            if (!combo)
            {
                _mounts = null; // rebuilt next time, so newly unlocked mounts show up
            }
            else
            {
                if (_mounts == null)
                {
                    var ps = FFXIVClientStructs.FFXIV.Client.Game.UI.PlayerState.Instance();
                    _mounts = Plugin.Data.GetExcelSheet<Lumina.Excel.Sheets.Mount>()
                        .Where(m => m.RowId != 0 && !m.Singular.IsEmpty && ps != null && ps->IsMountUnlocked(m.RowId))
                        .Select(m => (m.RowId, MountName(m.RowId)))
                        .OrderBy(m => m.Item2, StringComparer.OrdinalIgnoreCase)
                        .ToList();
                    _mountFilter = "";
                }
                if (ImGui.Selectable("Mount Roulette", Config.MountKind != MountChoice.Specific))
                    SetMount(MountChoice.Roulette, 0);
                ImGui.Separator();
                ImGui.SetNextItemWidth(-1);
                ImGui.InputTextWithHint("##mountsearch", $"Search your {_mounts.Count} mounts", ref _mountFilter, 64);
                foreach (var (id, name) in _mounts)
                {
                    if (_mountFilter.Length > 0 && !name.Contains(_mountFilter, StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (ImGui.Selectable($"{name}##m{id}", Config.MountKind == MountChoice.Specific && Config.MountId == id))
                        SetMount(MountChoice.Specific, id);
                }
            }
        }
        Help("If your chosen mount can't be summoned somewhere, Beeline uses Mount Roulette there.");
        ImGui.Unindent();
    }

    private void SetMount(MountChoice kind, uint id)
    {
        Config.MountKind = kind;
        Config.MountId = id;
        _plugin.SaveConfig();
    }

    // ---------------- Questing ----------------

    private void DrawQuesting()
    {
        Hint("Quest markers are used at their exact position: you end up next to the quest NPC or object.");
        ImGuiHelpers.ScaledDummy(2);
        Check("Target the quest NPC or object when you arrive", () => Config.TargetOnArrival, v => Config.TargetOnArrival = v,
              "Only friendly NPCs and objects, only once you're close enough to talk to them or use them. Enemies are never targeted.");

        ImGui.SetNextItemWidth(150 * ImGuiHelpers.GlobalScale);
        var mob = Config.MobDistance;
        if (ImGui.SliderInt("Safe distance from quest enemies", ref mob, 5, 40, "%d yalms"))
        {
            Config.MobDistance = mob;
            _plugin.SaveConfig();
        }
        Help("Stop this far from enemies you have to fight, so you don't pull them by accident.");

        Check("No safe distance from enemies far below your level", () => Config.IgnoreLowLevelMobs, v => Config.IgnoreLowLevelMobs = v,
              "They can't aggro you: walk right up to them.");
        if (Config.IgnoreLowLevelMobs)
        {
            ImGui.SameLine();
            ImGui.SetNextItemWidth(100 * ImGuiHelpers.GlobalScale);
            var gap = Config.NoAggroLevelGap;
            if (ImGui.SliderInt("##gap", ref gap, 1, 30, "%d+ levels"))
            {
                Config.NoAggroLevelGap = gap;
                _plugin.SaveConfig();
            }
        }

        Check("Stop the trip when you target an enemy", () => Config.StopOnEnemyTarget, v => Config.StopOnEnemyTarget = v,
              "Being attacked doesn't stop a trip (you keep going, like the game keeps you on your mount). With this "
            + "on, targeting something you can attack stops it, so you can start the fight. With Giddy Up installed, the trip goes on until the enemy "
            + "is within your attack range, as set in Giddy Up (or stops at once if Giddy Up is set to dismount as soon "
            + "as you target). Turn it off if stray tab-targets keep stopping your trips.");
        if (_giddyUp)
        {
            // Giddy Up owns dismounting: one place for that setting.
            Hint("Dismounting for enemies: handled by Giddy Up (set it there).");
        }
        else
            Check("Dismount when you target an enemy", () => Config.DismountOnEnemy, v => Config.DismountOnEnemy = v,
                  "Gets you off your mount as soon as you target something you can attack.");
    }

    // ---------------- Advanced ----------------

    private void DrawAdvanced()
    {
        ImGui.TextUnformatted("Route timing, learned from your trips");
        Help("Beeline times your teleports and aethernet hops (loading screens) and measures how fast you really move, "
           + "then picks the route with the shortest time. Each trip refines these numbers.");
        using (var table = ImRaii.Table("timings", 3, ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.RowBg))
        {
            if (table)
            {
                Row("Teleport",           $"{Config.TeleportSeconds:0.0} s", Config.TeleportSamples);
                Row("Aethernet hop",      $"{Config.AethernetSeconds:0.0} s", Config.AethernetSamples);
                Row("Running",            $"{Config.RunSpeed:0.0} yalms/s", Config.RunSamples);
                Row("Peloton",            $"{Config.PelotonSpeed:0.0} yalms/s", Config.PelotonSamples);
                Row("Sprint",             $"{Config.SprintSpeed:0.0} yalms/s for {Config.SprintSeconds:0} s", Config.SprintSamples);
                Row("Jog (after Sprint)", $"{Config.JogSpeed:0.0} yalms/s", Config.JogSamples);
                Row("Getting on a mount", $"{Config.MountSeconds:0.0} s (you keep moving)", Config.MountSamples);
                Row("Riding",             $"{Config.RideSpeed:0.0} yalms/s", Config.RideSamples);
                Row("Flying",             $"{Config.FlySpeed:0.0} yalms/s", Config.FlySamples);
                Row("Ground path detour", $"x{Config.Detour:0.00} the straight line", Config.DetourSamples);
            }
        }
        if (ImGui.Button("Reset to defaults"))
        {
            Config.ResetTimings();
            _plugin.SaveConfig();
        }

        ImGuiHelpers.ScaledDummy(6);
        Check("Write each button press and trip step to the Dalamud log", () => Config.LogPresses, v => Config.LogPresses = v,
              "For troubleshooting: include the [Beeline] lines from /xllog when you report a problem.");
    }

    // ---------------- Helpers ----------------

    private void Check(string label, Func<bool> get, Action<bool> set, string? help = null)
    {
        var v = get();
        if (ImGui.Checkbox(label, ref v))
        {
            set(v);
            _plugin.SaveConfig();
        }
        if (help != null)
            Help(help);
    }

    /// <summary>A (?) marker on the same line, showing the explanation on hover.</summary>
    private static void Help(string text)
    {
        ImGui.SameLine();
        ImGuiComponents.HelpMarker(text);
    }

    private static void Row(string what, string value, int samples)
    {
        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        ImGui.TextUnformatted(what);
        ImGui.TableNextColumn();
        ImGui.TextUnformatted(value);
        ImGui.TableNextColumn();
        using (ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.DalamudGrey))
            ImGui.TextUnformatted(samples == 0 ? "default" : samples == 1 ? "1 trip" : $"{samples} trips");
    }

    private static string KeyName(VirtualKey k) => k == VirtualKey.NO_KEY ? "Off" : k.GetFancyName();

    private static bool PadCombo(string label, ref GamepadButtons value, bool allowNone, string noneLabel = "None", string prefix = "")
    {
        var changed = false;
        ImGui.SetNextItemWidth(170 * ImGuiHelpers.GlobalScale);
        var preview = value == GamepadButtons.None ? noneLabel : prefix + Plugin.ButtonName(value);
        using var combo = ImRaii.Combo(label, preview);
        if (!combo)
            return false;
        if (allowNone && ImGui.Selectable(noneLabel, value == GamepadButtons.None))
        {
            value = GamepadButtons.None;
            changed = true;
        }
        foreach (var b in Plugin.AllPadButtons)
        {
            if (ImGui.Selectable(prefix + Plugin.ButtonName(b), b == value))
            {
                value = b;
                changed = true;
            }
        }
        return changed;
    }

    /// <summary>Ctrl / Shift / Alt checkboxes on the same line (any combination).</summary>
    private static bool ModsCheckboxes(string id, ref KeyMods mods)
    {
        var changed = false;
        foreach (var (flag, name) in new[] { (KeyMods.Ctrl, "Ctrl"), (KeyMods.Shift, "Shift"), (KeyMods.Alt, "Alt") })
        {
            ImGui.SameLine();
            var on = mods.HasFlag(flag);
            if (ImGui.Checkbox($"{name}##{id}{name}", ref on))
            {
                mods = on ? mods | flag : mods & ~flag;
                changed = true;
            }
        }
        return changed;
    }

    private static void StatusLine(Vector4 color, FontAwesomeIcon icon, string text)
    {
        using (ImRaii.PushFont(UiBuilder.IconFont))
        using (ImRaii.PushColor(ImGuiCol.Text, color))
            ImGui.TextUnformatted(icon.ToIconString());
        ImGui.SameLine();
        using (ImRaii.PushColor(ImGuiCol.Text, color))
            ImGui.TextUnformatted(text);
    }

    private static void Header(string text)
    {
        using (ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.DalamudViolet))
            ImGui.TextUnformatted(text);
        ImGui.Separator();
    }

    private static void Hint(string text)
    {
        using (ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.DalamudGrey))
            ImGui.TextUnformatted(text);
    }

    private static void Warn(string text)
    {
        using (ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.DalamudOrange))
            ImGui.TextUnformatted(text);
    }
}
