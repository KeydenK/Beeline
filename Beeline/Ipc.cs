using System.Numerics;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;

namespace Beeline;

/// <summary>Calls into Lifestream and vnavmesh.</summary>
internal sealed class Ipc
{
    private readonly ICallGateSubscriber<string, object> _lsExecute;
    private readonly ICallGateSubscriber<bool>           _lsIsBusy;
    private readonly ICallGateSubscriber<uint>           _lsActive;
    private readonly ICallGateSubscriber<uint, bool>     _lsAethernetById;
    private readonly ICallGateSubscriber<uint, byte, bool> _lsTeleport;
    private readonly ICallGateSubscriber<object>         _lsAbort;
    private readonly ICallGateSubscriber<bool>           _navReady;
    private readonly ICallGateSubscriber<bool>           _navPathRunning;
    private readonly ICallGateSubscriber<bool>           _navPathfinding;
    private readonly ICallGateSubscriber<object>         _navStop;
    private readonly ICallGateSubscriber<Vector3, bool, float, Vector3?> _navPointOnFloor;
    private readonly ICallGateSubscriber<Vector3, bool, bool> _navMoveTo;
    private readonly ICallGateSubscriber<Vector3, bool, float, bool> _navMoveCloseTo;

    public Ipc(IDalamudPluginInterface pi)
    {
        _lsExecute       = pi.GetIpcSubscriber<string, object>("Lifestream.ExecuteCommand");
        _lsIsBusy        = pi.GetIpcSubscriber<bool>("Lifestream.IsBusy");
        _lsActive        = pi.GetIpcSubscriber<uint>("Lifestream.GetActiveAetheryte");
        _lsAethernetById = pi.GetIpcSubscriber<uint, bool>("Lifestream.AethernetTeleportById");
        _lsTeleport      = pi.GetIpcSubscriber<uint, byte, bool>("Lifestream.Teleport");
        _lsAbort         = pi.GetIpcSubscriber<object>("Lifestream.Abort");
        _navReady        = pi.GetIpcSubscriber<bool>("vnavmesh.Nav.IsReady");
        _navPathRunning  = pi.GetIpcSubscriber<bool>("vnavmesh.Path.IsRunning");
        _navPathfinding  = pi.GetIpcSubscriber<bool>("vnavmesh.SimpleMove.PathfindInProgress");
        _navStop         = pi.GetIpcSubscriber<object>("vnavmesh.Path.Stop");
        _navPointOnFloor = pi.GetIpcSubscriber<Vector3, bool, float, Vector3?>("vnavmesh.Query.Mesh.PointOnFloor");
        _navMoveTo       = pi.GetIpcSubscriber<Vector3, bool, bool>("vnavmesh.SimpleMove.PathfindAndMoveTo");
        _navMoveCloseTo  = pi.GetIpcSubscriber<Vector3, bool, float, bool>("vnavmesh.SimpleMove.PathfindAndMoveCloseTo");
    }

    public bool LifestreamBusy()                     => _lsIsBusy.InvokeFunc();
    public uint ActiveAetheryte()                    => _lsActive.InvokeFunc();
    public bool AethernetTo(uint shardId)            => _lsAethernetById.InvokeFunc(shardId);
    public bool TeleportTo(uint aetheryteId)         => _lsTeleport.InvokeFunc(aetheryteId, 0);
    public void LifestreamCommand(string args)       => _lsExecute.InvokeAction(args);
    public void LifestreamAbort()                    => _lsAbort.InvokeAction();

    public bool NavReady()   => _navReady.InvokeFunc();
    public bool NavMoving()  => _navPathRunning.InvokeFunc() || _navPathfinding.InvokeFunc();
    public void NavStop()    => _navStop.InvokeAction();
    /// <summary>Highest walkable ground at X/Z below fromY (default: from the sky), within the given radius.</summary>
    public Vector3? NavGroundAt(float x, float z, float radius, float fromY = 1024) => _navPointOnFloor.InvokeFunc(new Vector3(x, fromY, z), true, radius);
    public bool NavMoveTo(Vector3 dest, bool fly) => _navMoveTo.InvokeFunc(dest, fly);
    public bool NavMoveCloseTo(Vector3 dest, bool fly, float range) => _navMoveCloseTo.InvokeFunc(dest, fly, range);
}
