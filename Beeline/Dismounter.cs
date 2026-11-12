using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace Beeline;

/// <summary>
/// Dismounts you when you target an enemy. Only watches while you are mounted: it starts on the
/// "mounted" condition change and stops when you dismount, so it costs nothing on foot.
/// </summary>
internal sealed unsafe class Dismounter : IDisposable
{
    private const uint DismountAction = 23; // General action "Dismount"

    private readonly Plugin _p;
    private bool _watching;
    private nint _lastTarget;

    public Dismounter(Plugin p)
    {
        _p = p;
        Plugin.Condition.ConditionChange += OnConditionChange;
        if (Plugin.Condition[ConditionFlag.Mounted])
            StartWatching();
    }

    public void Dispose()
    {
        Plugin.Condition.ConditionChange -= OnConditionChange;
        StopWatching();
    }

    private void OnConditionChange(ConditionFlag flag, bool value)
    {
        if (flag != ConditionFlag.Mounted)
            return;
        if (value) StartWatching();
        else StopWatching();
    }

    private void StartWatching()
    {
        if (_watching || !_p.Config.DismountOnEnemy) return; // option off: nothing to watch on this mount
        _watching = true;
        _lastTarget = (nint)FFXIVClientStructs.FFXIV.Client.Game.Control.TargetSystem.Instance()->Target; // an enemy already targeted doesn't count
        Plugin.Framework.Update += OnUpdate;
    }

    private void StopWatching()
    {
        if (!_watching) return;
        _watching = false;
        Plugin.Framework.Update -= OnUpdate;
    }

    private void OnUpdate(IFramework framework)
    {
        // Compare the game's raw target pointer: no allocation, and nothing else to do until it changes.
        var raw = (nint)FFXIVClientStructs.FFXIV.Client.Game.Control.TargetSystem.Instance()->Target;
        if (raw == _lastTarget)
            return;
        _lastTarget = raw;
        var target = Plugin.TargetManager.Target;

        // An enemy = a battle NPC you could auto-attack (action 7), as the game itself decides.
        if (!_p.Config.DismountOnEnemy || target is not IBattleNpc npc
            || !ActionManager.CanUseActionOnTarget(7, (FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)npc.Address))
            return;
        // Giddy Up installed: dismounting is its job, per its own settings (even when you turned it off there); never
        // both. Asked now, so loading it mid-ride counts.
        if (_p.Companion?.GiddyUpPresent() == true)
            return;

        Plugin.Info($"{Plugin.Tag} You targeted {npc.Name}: dismounting");
        _p.Travel.Cancel("stopped (enemy targeted)", quiet: true);
        ActionManager.Instance()->UseAction(ActionType.GeneralAction, DismountAction);
    }
}
