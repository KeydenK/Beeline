using System.Globalization;
using System.Numerics;
using Dalamud.Plugin.Ipc;

namespace Beeline;

/// <summary>
/// Dev builds only: lets an automated test start a trip without the map press (the mouse path reads the real
/// Windows mouse/keys, which test tools can't reach). Same code path as a press after the cursor maths.
/// IPC "Beeline.DevTest" (string -> string):
///   go &lt;x&gt; &lt;z&gt;                          world spot in the current zone
///   map &lt;territory&gt; &lt;mapId&gt; &lt;mapX&gt; &lt;mapY&gt; [tooltip...]   map coordinates (any zone), like a press with that readout
///   stop                                 cancel the trip
/// Not registered at all in normal installs.
/// </summary>
internal sealed class DevTest : IDisposable
{
    private readonly Plugin _p;
    private readonly ICallGateProvider<string, string>? _gate;

    public DevTest(Plugin p)
    {
        _p = p;
        if (!Plugin.PluginInterface.IsDev)
            return;
        _gate = Plugin.PluginInterface.GetIpcProvider<string, string>("Beeline.DevTest");
        _gate.RegisterFunc(Run);
    }

    public void Dispose() => _gate?.UnregisterFunc();

    private string Run(string input)
        => Plugin.Framework.RunOnFrameworkThread(() => RunHere(input ?? "")).GetAwaiter().GetResult();

    private string RunHere(string input)
    {
        var parts = input.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
            return "go <x> <z> | map <territory> <mapId> <mapX> <mapY> [tooltip] | stop";
        float F(int i) => float.Parse(parts[i], CultureInfo.InvariantCulture);
        switch (parts[0].ToLowerInvariant())
        {
            case "go" when parts.Length >= 3:
            {
                var territory = (uint)Plugin.ClientState.TerritoryType;
                var mapId = Plugin.ClientState.MapId;
                var world = new Vector2(F(1), F(2));
                Plugin.Info($"{Plugin.Tag} [dev test] trip to world ({world.X:0.0}, {world.Y:0.0})");
                _p.Travel.Start(territory, mapId, Vector2.Zero, "", 10f, world: world);
                return $"started: territory {territory}, world ({world.X:0.0}, {world.Y:0.0}); active={_p.Travel.IsActive}";
            }
            case "map" when parts.Length >= 5:
            {
                var territory = uint.Parse(parts[1], CultureInfo.InvariantCulture);
                var mapId = uint.Parse(parts[2], CultureInfo.InvariantCulture);
                var coords = new Vector2(F(3), F(4));
                var tooltip = parts.Length > 5 ? string.Join(' ', parts.Skip(5)) : "";
                Plugin.Info($"{Plugin.Tag} [dev test] trip to map ({coords.X:0.0}, {coords.Y:0.0}) in territory {territory}");
                _p.Travel.Start(territory, mapId, coords, tooltip, 10f);
                return $"started: territory {territory}, map ({coords.X:0.0}, {coords.Y:0.0}); active={_p.Travel.IsActive}";
            }
            case "stop":
                _p.Travel.Cancel("cancelled (dev test)");
                return "stopped";
            default:
                return "unknown: go <x> <z> | map <territory> <mapId> <mapX> <mapY> [tooltip] | stop";
        }
    }
}
