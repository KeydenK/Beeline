// TestProbe: copy this file into a plugin so Claude's tests can ask it "what are you doing right now?".
// It answers only in dev builds (installed plugins from a repo never expose it).
//
// In the plugin's constructor:
//     TestProbe.Register(PluginInterface, "Beeline", () => $"trip={_trip?.State}; route={_route}; target={_target}");
// In Dispose():
//     TestProbe.Unregister();
//
// Tests then check it with   { "waitFor": "probe:Beeline:trip=Flying" }
// or Claude reads it with DalamudMCP call_plugin_ipc ("Beeline.TestState", Func<string>).
// Keep the answer short, one line, key=value pairs; never include player names.

using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;

internal static class TestProbe
{
    private static ICallGateProvider<string>? _provider;

    public static void Register(IDalamudPluginInterface pi, string pluginName, System.Func<string> state)
    {
        if (!pi.IsDev) return;
        _provider = pi.GetIpcProvider<string>($"{pluginName}.TestState");
        _provider.RegisterFunc(() =>
        {
            try { return state(); }
            catch (System.Exception e) { return "error: " + e.Message; }
        });
    }

    public static void Unregister()
    {
        _provider?.UnregisterFunc();
        _provider = null;
    }
}
