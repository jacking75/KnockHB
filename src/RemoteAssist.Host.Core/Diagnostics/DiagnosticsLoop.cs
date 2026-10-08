using RemoteAssist.Host.Core.Security;
using RemoteAssist.Protocol;

namespace RemoteAssist.Host.Core.Diagnostics;

public static class DiagnosticsLoop
{
    public static async Task Run(IDiagnosticsProvider provider,PermissionGate permissions,PermissionSnapshot grant,Func<bool> ready,Action<FeatureMessage> send,CancellationToken ct,TimeProvider? time=null)
    {
        var clock=time??TimeProvider.System;
        using var timer=new PeriodicTimer(TimeSpan.FromSeconds(2),clock);
        while(await timer.WaitForNextTickAsync(ct))
        {
            if(!ready())continue;
            if(!await CollectOnce(provider,permissions,grant,send,ct,clock))return;
        }
    }
    public static async Task<bool> CollectOnce(IDiagnosticsProvider provider,PermissionGate permissions,PermissionSnapshot grant,Action<FeatureMessage> send,CancellationToken ct,TimeProvider? time=null)
    {
        ct.ThrowIfCancellationRequested();if(!permissions.Allows(Capability.Diagnostics,grant.Generation))return false;
        // One provider call only. On timeout the owning helper terminates; no orphan-call accumulation.
        var snapshot=await Task.Run(()=>provider.Read(ct),ct).WaitAsync(TimeSpan.FromSeconds(2),time??TimeProvider.System,ct);
        snapshot.Validate();ct.ThrowIfCancellationRequested();
        if(!permissions.Allows(Capability.Diagnostics,grant.Generation))return false;
        send(new(){T="telemetry",Id=Guid.NewGuid().ToString("N"),Snapshot=snapshot}); // The owning data channel applies the final send gate.
        return true;
    }
}
