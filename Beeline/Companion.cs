using Dalamud.Plugin.Ipc;

namespace Beeline;

/// <summary>
/// The link with Giddy Up (mount / Sprint / Peloton plugin by the same author). Both sides work alone; together:
/// - Beeline tells Giddy Up when a trip starts and ends, whether the current leg will mount, and shares its learned
///   speeds (so Giddy Up doesn't measure them again).
/// - Giddy Up tells Beeline it handles Sprint/Peloton and enemy dismounts (Beeline then leaves those to it), and
///   that it dismounted you for a fight (the trip ends).
/// Nothing here runs every frame: events, plus one question per trip or per mount.
/// </summary>
internal sealed class Companion : IDisposable
{
    private readonly Plugin _p;
    private readonly ICallGateProvider<bool> _isTrip;
    private readonly ICallGateProvider<bool> _willMount;
    private readonly ICallGateProvider<bool> _stopsForEnemies;
    private readonly ICallGateProvider<Dictionary<string, float>> _speeds;
    private readonly ICallGateProvider<object> _tripStarted, _tripEnded;
    private readonly ICallGateSubscriber<bool> _guBoosts, _guEnemy;
    private readonly ICallGateSubscriber<object> _guDismounted;
    private readonly ICallGateSubscriber<float> _guStopRange;

    public Companion(Plugin p)
    {
        _p = p;
        var pi = Plugin.PluginInterface;
        _isTrip      = pi.GetIpcProvider<bool>("Beeline.IsTripActive");
        _willMount   = pi.GetIpcProvider<bool>("Beeline.WillMount");
        _speeds      = pi.GetIpcProvider<Dictionary<string, float>>("Beeline.GetLearnedSpeeds");
        _tripStarted = pi.GetIpcProvider<object>("Beeline.TripStarted");
        _tripEnded   = pi.GetIpcProvider<object>("Beeline.TripEnded");
        _isTrip.RegisterFunc(() => _p.Travel.IsActive);
        _willMount.RegisterFunc(() => _p.Travel.WillMount);
        // Giddy Up respects it: off, it leaves you on the mount during a trip (you break the trip by moving).
        _stopsForEnemies = pi.GetIpcProvider<bool>("Beeline.StopsTripForEnemies");
        _stopsForEnemies.RegisterFunc(() => _p.Config.StopOnEnemyTarget);
        _speeds.RegisterFunc(LearnedSpeeds);

        _guBoosts     = pi.GetIpcSubscriber<bool>("GiddyUp.HandlesSpeedBuffs");
        _guEnemy      = pi.GetIpcSubscriber<bool>("GiddyUp.HandlesEnemyDismount");
        _guDismounted = pi.GetIpcSubscriber<object>("GiddyUp.DismountedForCombat");
        _guStopRange  = pi.GetIpcSubscriber<float>("GiddyUp.EnemyStopRange");
        _guDismounted.Subscribe(OnGiddyUpDismounted);
    }

    public void Dispose()
    {
        _isTrip.UnregisterFunc();
        _willMount.UnregisterFunc();
        _stopsForEnemies.UnregisterFunc();
        _speeds.UnregisterFunc();
        _guDismounted.Unsubscribe(OnGiddyUpDismounted);
    }

    internal void TripStarted() => Send(_tripStarted);
    internal void TripEnded()   => Send(_tripEnded);

    private static void Send(ICallGateProvider<object> gate)
    {
        try { gate.SendMessage(); }
        catch (Exception e) { Plugin.Log.Debug(e, $"{Plugin.Tag} Couldn't notify Giddy Up"); }
    }

    /// <summary>Giddy Up is loaded and uses Sprint/Peloton itself (asked once per trip).</summary>
    internal bool GiddyUpBoosts() => Ask(_guBoosts);
    /// <summary>Giddy Up is loaded and dismounts you for enemies itself (asked once per mount).</summary>
    internal bool GiddyUpEnemyDismount() => Ask(_guEnemy);

    /// <summary>
    /// How close a targeted enemy must be before a trip stops: your attack range per Giddy Up's settings, or -1 =
    /// at once (Giddy Up set to "as soon as you target it", or not installed). Asked once per targeted enemy.
    /// </summary>
    internal float GiddyUpEnemyStopRange()
    {
        try { return _guStopRange.InvokeFunc(); }
        catch { return -1f; }
    }

    private static bool Ask(ICallGateSubscriber<bool> gate)
    {
        try { return gate.InvokeFunc(); }
        catch { return false; } // Giddy Up not installed / not loaded
    }

    private void OnGiddyUpDismounted()
    {
        if (!_p.Travel.IsActive)
            return;
        Plugin.Info($"{Plugin.Tag} Giddy Up dismounted you for a fight: trip stopped");
        _p.Travel.Cancel("stopped (dismounted for a fight)", quiet: true);
    }

    private Dictionary<string, float> LearnedSpeeds()
    {
        var c = _p.Config;
        return new Dictionary<string, float>
        {
            ["Run"] = c.RunSpeed,         ["RunSamples"] = c.RunSamples,
            ["Sprint"] = c.SprintSpeed,   ["SprintSamples"] = c.SprintSamples,
            ["SprintSeconds"] = c.SprintSeconds,
            ["Jog"] = c.JogSpeed,         ["JogSamples"] = c.JogSamples,
            ["Peloton"] = c.PelotonSpeed, ["PelotonSamples"] = c.PelotonSamples,
            ["Ride"] = c.RideSpeed,       ["RideSamples"] = c.RideSamples,
            ["Fly"] = c.FlySpeed,         ["FlySamples"] = c.FlySamples,
        };
    }
}
