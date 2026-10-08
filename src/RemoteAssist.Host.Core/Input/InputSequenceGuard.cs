using RemoteAssist.Protocol;

namespace RemoteAssist.Host.Core.Input;

/// <summary>Independent channel order plus a reliable reset barrier. DTLS peer identity is bound by the owning session.</summary>
public sealed class InputSequenceGuard(string sessionId,long connectionGeneration,long permissionGeneration,string monitorIdentity,long topologyGeneration)
{
    readonly object gate=new();
    long inputSequence,pointerSequence,epoch=1;
    public long Epoch {get{lock(gate)return epoch;}}
    public bool Accept(string channel,InputMsg message)
    {
        lock(gate)
        {
            if(message.Version!=1||message.T is not ("key" or "mouse" or "reset")||message.SessionId!=sessionId||message.Generation!=connectionGeneration||message.PermissionGeneration!=permissionGeneration||
                message.MonitorIdentity!=monitorIdentity||message.TopologyGeneration!=topologyGeneration||message.Sequence is <1 or >9007199254740991L||message.InputEpoch is <1 or >9007199254740991L)return false;
            if(channel=="pointer")
            {
                if(message.T!="mouse"||message.A!="move"||message.InputEpoch!=epoch||message.Sequence<=pointerSequence)return false;
                pointerSequence=message.Sequence;return true;
            }
            if(channel!="input"||message.T=="mouse"&&message.A=="move"||message.Sequence<=inputSequence)return false;
            if(message.T=="reset")
            {
                if(message.A!="all"||message.InputEpoch!=epoch+1)return false;
                epoch=message.InputEpoch;pointerSequence=0;
            }
            else if(message.InputEpoch!=epoch)return false;
            inputSequence=message.Sequence;return true;
        }
    }
}
