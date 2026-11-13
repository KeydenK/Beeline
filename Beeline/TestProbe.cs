// TestProbe: copy this file into a plugin so Claude's tests can ask it "what are you doing right now?".
//
// DEV BUILDS ONLY, and left out of public releases entirely: everything is inside `#if DEVTOOLS`.
// Add this to the plugin's .csproj so local builds define DEVTOOLS and the GitHub release build (which always
// runs with CI=true) doesn't:
//
//   <!-- Dev-only test tools: in local builds only; the GitHub release build (CI=true) leaves them out. -->
//   <PropertyGroup Condition="'$(CI)' != 'true'">
//     <DefineConstants>$(DefineConstants);DEVTOOLS</DefineConstants>
//   </PropertyGroup>
//
// In the plugin (wrap these, and the state method itself, plus any other test-only command or IPC, in #if DEVTOOLS):
//   #if DEVTOOLS
//       TestProbe.Register(PluginInterface, "Beeline", () => $"trip={_trip?.State}; route={_route}");  // constructor
//   #endif
//   #if DEVTOOLS
//       TestProbe.Unregister();                                                                         // Dispose()
//   #endif
//
// Tests then check it with   { "waitFor": "probe:Beeline:trip=Flying" }
// or Claude reads it with DalamudMCP call_plugin_ipc ("Beeline.TestState", Func<string>).
// Keep the answer short, one line, key=value pairs; never include player names.
// Check a public build is clean: build with `-p:CI=true` and search the DLL for "TestState" (must not be there).

#if DEVTOOLS
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;

internal static class TestProbe
{
    private static ICallGateProvider<string>? _provider;

    public static void Register(IDalamudPluginInterface pi, string pluginName, System.Func<string> state)
    {
        if (!pi.IsDev) return; // also off for anyone installing a local build from a repo
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
#endif
