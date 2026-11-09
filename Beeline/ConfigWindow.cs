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

internal sealed class ConfigWindow : Window
{
    private const float ContentWidth = 420;
    private readonly Plugin _plugin;
    private long _depsCheckedAt = long.MinValue / 2;
    private bool _lifestream, _vnav;

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
            ImGuiHelpers.ScaledDummy(6);
            DrawTravel();
            ImGuiHelpers.ScaledDummy(6);
            DrawAdvanced();
        }
        finally
        {
            ImGui.PopTextWrapPos();
        }
    }

    private void DrawStatus()
    {
        // Listing installed plugins allocates: refresh once a second, not every frame.
        if (Environment.TickCount64 - _depsCheckedAt > 1000)
        {
            _depsCheckedAt = Environment.TickCount64;
            _lifestream = Plugin.IsPluginLoaded("Lifestream");
            _vnav       = Plugin.IsPluginLoaded("vnavmesh");
        }
        var lifestream = _lifestream;
        var vnav       = _vnav;
        Requirement("Lifestream", lifestream, "needed for teleports and the aethernet");
        Requirement("vnavmesh", vnav, "needed to move to the point after arriving");

        if (_plugin.Travel.IsActive)
        {
            ImGuiHelpers.ScaledDummy(4);
            StatusLine(ImGuiColors.DalamudYellow, FontAwesomeIcon.Route, $"Trip in progress: {_plugin.Travel.Status}");
            ImGui.SameLine();
            if (ImGui.SmallButton("Stop"))
                _plugin.Travel.Cancel("cancelled");
        }
    }

    private static void Requirement(string name, bool ok, string why)
    {
        if (ok)
            StatusLine(ImGuiColors.HealerGreen, FontAwesomeIcon.CheckCircle, $"{name} found");
        else
            StatusLine(ImGuiColors.DalamudRed, FontAwesomeIcon.ExclamationTriangle, $"{name} missing: {why}");
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

    private void DrawTravel()
    {
        Header("Map button");
        var on = Config.Enabled;
        if (ImGui.Checkbox("Go to what is under the map cursor", ref on))
        {
            Config.Enabled = on;
            _plugin.SaveConfig();
        }

        using var disabled = ImRaii.Disabled(!on);

        var b = Config.Button;
        if (ButtonCombo("Controller button", ref b))
        {
            Config.Button = b;
            _plugin.SaveConfig();
        }

        ImGui.SetNextItemWidth(160 * ImGuiHelpers.GlobalScale);
        using (var combo = ImRaii.Combo("Mouse button", Plugin.MouseName(Config.MouseButton)))
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
            ImGui.SameLine();
            ImGui.SetNextItemWidth(90 * ImGuiHelpers.GlobalScale);
            using var mod = ImRaii.Combo("##modifier", Config.MouseModifier == KeyModifier.None ? "no key" : "+ " + Config.MouseModifier);
            if (mod)
                foreach (var m in Enum.GetValues<KeyModifier>())
                    if (ImGui.Selectable(m == KeyModifier.None ? "no key" : "+ " + m, m == Config.MouseModifier))
                    {
                        Config.MouseModifier = m;
                        _plugin.SaveConfig();
                    }
        }
        if (Config.MouseButton is MouseTrigger.Left or MouseTrigger.Right && Config.MouseModifier == KeyModifier.None)
            Hint("The map also uses this click (left click on an aetheryte opens the teleport window, right click opens a menu). Ctrl + left click avoids that, and Lifestream ignores Ctrl clicks.");

        ImGui.SetNextItemWidth(160 * ImGuiHelpers.GlobalScale);
        using (var combo = ImRaii.Combo("Keyboard key", KeyName(Config.Hotkey)))
        {
            if (combo)
            {
                if (ImGui.Selectable("None", Config.Hotkey == VirtualKey.NO_KEY))
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
        Hint("Mouse and keyboard: point at the spot on the map with the mouse, then click / press. Pick a key the game doesn't use, or it will do both.");
        if (Config.Button == GamepadButtons.South || Config.Button == GamepadButtons.East)
            Hint("This may also be the game's confirm/cancel button on the map; Square / X or Triangle / Y avoids clashes.");

        Hint("On an aetheryte or an aethernet shard: go there (aethernet if you are on that city's network, otherwise teleport).\n"
           + "Anywhere else (quest marker, NPC, object, any spot, any zone): the quickest way is used (no map flag is placed): going directly, the aethernet, or a teleport to the closest aetheryte, then vnavmesh does the rest. Moving your character cancels the trip.");
        ImGuiHelpers.ScaledDummy(4);

        var shards = Config.UseShardsForTravel;
        if (ImGui.Checkbox("Use aethernet shards to get closer", ref shards))
        {
            Config.UseShardsForTravel = shards;
            _plugin.SaveConfig();
        }

        var mount = Config.AutoMount;
        if (ImGui.Checkbox("Mount up when the point is far", ref mount))
        {
            Config.AutoMount = mount;
            _plugin.SaveConfig();
        }
        if (mount)
        {
            ImGui.SameLine();
            ImGui.SetNextItemWidth(110 * ImGuiHelpers.GlobalScale);
            var d = Config.MountDistance;
            if (ImGui.SliderInt("##mountdist", ref d, 10, 200, "over %d yalms"))
            {
                Config.MountDistance = d;
                _plugin.SaveConfig();
            }
        }

        var fly = Config.AllowFlying;
        if (ImGui.Checkbox("Fly when the zone allows it", ref fly))
        {
            Config.AllowFlying = fly;
            _plugin.SaveConfig();
        }
        Hint("Uses Mount Roulette. Flying needs all aether currents of the zone.");

        ImGui.SetNextItemWidth(200 * ImGuiHelpers.GlobalScale);
        var mob = Config.MobDistance;
        if (ImGui.SliderInt("Stop this far from quest mobs", ref mob, 5, 40, "%d yalms"))
        {
            Config.MobDistance = mob;
            _plugin.SaveConfig();
        }
        var low = Config.IgnoreLowLevelMobs;
        if (ImGui.Checkbox("Walk right up to mobs far below your level", ref low))
        {
            Config.IgnoreLowLevelMobs = low;
            _plugin.SaveConfig();
        }
        if (low)
        {
            ImGui.SameLine();
            ImGui.SetNextItemWidth(110 * ImGuiHelpers.GlobalScale);
            var gap = Config.NoAggroLevelGap;
            if (ImGui.SliderInt("##gap", ref gap, 1, 30, "%d+ levels"))
            {
                Config.NoAggroLevelGap = gap;
                _plugin.SaveConfig();
            }
        }

        var tgt = Config.TargetOnArrival;
        if (ImGui.Checkbox("Target the quest NPC or object when you arrive", ref tgt))
        {
            Config.TargetOnArrival = tgt;
            _plugin.SaveConfig();
        }

        var dis = Config.DismountOnEnemy;
        if (ImGui.Checkbox("Dismount when you target an enemy", ref dis))
        {
            Config.DismountOnEnemy = dis;
            _plugin.SaveConfig();
        }
        Hint("Quest NPCs and objects: you walk right up to them, and (if the option above is on) they get targeted once you are close enough to talk to them or use them. Enemies are never targeted.");
    }

    private void DrawAdvanced()
    {
        if (!ImGui.CollapsingHeader("Advanced"))
            return;

        ImGui.SetNextItemWidth(200 * ImGuiHelpers.GlobalScale);
        var tp = Config.TeleportCost;
        if (ImGui.SliderInt("Teleport is worth", ref tp, 0, 500, "%d yalms"))
        {
            Config.TeleportCost = tp;
            _plugin.SaveConfig();
        }
        ImGui.SetNextItemWidth(200 * ImGuiHelpers.GlobalScale);
        var ae = Config.AethernetCost;
        if (ImGui.SliderInt("Aethernet is worth", ref ae, 0, 300, "%d yalms"))
        {
            Config.AethernetCost = ae;
            _plugin.SaveConfig();
        }
        Hint("How far you could ride in the time a teleport or an aethernet hop takes. Higher = teleports less often.");

        var log = Config.LogPresses;
        if (ImGui.Checkbox("Write each button press and trip step to the Dalamud log", ref log))
        {
            Config.LogPresses = log;
            _plugin.SaveConfig();
        }
    }

    private static string KeyName(VirtualKey k) => k == VirtualKey.NO_KEY ? "None" : k.GetFancyName();

    private static bool ButtonCombo(string label, ref GamepadButtons value)
    {
        var changed = false;
        ImGui.SetNextItemWidth(160 * ImGuiHelpers.GlobalScale);
        using var combo = ImRaii.Combo(label, Plugin.ButtonName(value));
        if (!combo)
            return false;
        foreach (var b in Plugin.FaceButtons)
        {
            if (ImGui.Selectable(Plugin.ButtonName(b), b == value))
            {
                value = b;
                changed = true;
            }
        }
        return changed;
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
}
