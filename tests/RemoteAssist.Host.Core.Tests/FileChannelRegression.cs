using System.Collections.Concurrent;
using RemoteAssist.Host.Core.Files;
using RemoteAssist.Host.Core.Security;
using RemoteAssist.Protocol;

static class FileChannelRegression
{
    static void Check(bool condition,string reason){if(!condition)throw new Exception(reason);}
    public static async Task Run()
    {
        foreach(var size in new[]{0,FileChunkHeader.MaxPayload*40+3})await Exchange(size,false,false);
        await Exchange(3,true,false);await Exchange(FileChunkHeader.MaxPayload+3,false,true);
    }
    static async Task Exchange(int size,bool deny,bool tamper)
    {
        var root=Path.Combine(Path.GetTempPath(),"remoteassist-file-channel-"+Guid.NewGuid().ToString("N"));
        var from=Path.Combine(root,"from");var to=Path.Combine(root,"to");Directory.CreateDirectory(from);Directory.CreateDirectory(to);
        try
        {
            var bytes=Enumerable.Range(0,size).Select(i=>(byte)i).ToArray();var sourcePath=Path.Combine(from,"file.dat");await File.WriteAllBytesAsync(sourcePath,bytes);
            var sourceGate=new PermissionGate();var targetGate=new PermissionGate();
            var sourceGrant=sourceGate.Grant("sid","peer");var targetGrant=targetGate.Grant("sid","peer");
            var failures=new ConcurrentQueue<string>();var audits=new ConcurrentQueue<FileAuditNotice>();
            var sourceDone=new TaskCompletionSource<FileTransferState>(TaskCreationOptions.RunContinuationsAsynchronously);var targetDone=new TaskCompletionSource<FileTransferState>(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var a=new Wire();await using var b=new Wire();
            await using(var source=new FileTransferSession(a,sourceGate,sourceGrant,(_,_)=>Task.FromResult<string?>(null),(n,_)=>{audits.Enqueue(n);return Task.CompletedTask;},failures.Enqueue,s=>sourceDone.TrySetResult(s),CancellationToken.None))
            await using(var target=new FileTransferSession(b,targetGate,targetGrant,(_,_)=>Task.FromResult<string?>(deny?null:to),(n,_)=>{audits.Enqueue(n);return Task.CompletedTask;},failures.Enqueue,s=>targetDone.TrySetResult(s),CancellationToken.None))
            {
                a.Peer=target;b.Peer=source;a.Tamper=tamper;source.Start();target.Start();
                Check(source.SendFile(sourcePath,Guid.NewGuid().ToString("N")),"source refused idle offer");
                var result=await sourceDone.Task.WaitAsync(TimeSpan.FromSeconds(3));var receiver=await targetDone.Task.WaitAsync(TimeSpan.FromSeconds(3));
                Check(failures.IsEmpty,"valid file exchange ended session: "+string.Join(",",failures));
                Check(result.State==(deny?"denied":tamper?"failed":"completed")&&receiver.State==(deny?"denied":tamper?"failed":"completed"),"file result or denial echo mismatch");
                Check(!deny||a.Binary==0,"denied transfer emitted payload");
                Check(a.Messages.All(m=>m.Name is null||m.T=="file-offer"),"local path or excess name leaked");
                Check(audits.Count==4&&audits.Count(n=>n.Phase=="started")==2,"missing transfer audit boundary");
                if(!deny&&!tamper)Check(File.ReadAllBytes(Path.Combine(to,"file.dat")).SequenceEqual(bytes),"receiver persisted wrong bytes");
                else Check(!File.Exists(Path.Combine(to,"file.dat")),"denied or tampered file persisted");
                Check(Directory.GetFiles(to,"*.part").Length==0,"file channel retained temp link");
            }
        }
        catch(Exception ex){Console.Error.WriteLine($"FILE CHANNEL size={size} deny={deny} tamper={tamper}: {ex.GetType().Name} {ex.Message}\n{ex.StackTrace}");throw;}
        finally
        {
            try{Directory.Delete(root,true);}catch(IOException ex){Console.Error.WriteLine("FILE TEST CLEANUP: "+ex.GetType().Name);}
        }
    }
    sealed class Wire:IFileTransferTransport,IAsyncDisposable
    {
        readonly System.Threading.Channels.Channel<Action> deliveries=System.Threading.Channels.Channel.CreateBounded<Action>(new System.Threading.Channels.BoundedChannelOptions(64){SingleReader=true});
        readonly Task worker;
        public Wire(){worker=Task.Run(async ()=>{await foreach(var action in deliveries.Reader.ReadAllAsync())action();});}
        public FileTransferSession Peer=null!;public bool Tamper;public int Binary;
        public ConcurrentQueue<FileMessage> Messages=new();
        public bool IsOpen=>true;public ulong BufferedAmount=>0;
        public void Send(FileMessage message){Messages.Enqueue(message);if(!deliveries.Writer.TryWrite(()=>Peer.Receive(message)))throw new IOException("fake wire queue overflow");}
        public void Send(byte[] packet){Binary++;var copy=packet.ToArray();if(Tamper){Tamper=false;copy[^1]^=1;}if(!deliveries.Writer.TryWrite(()=>Peer.Receive(copy)))throw new IOException("fake wire queue overflow");}
        public async ValueTask DisposeAsync(){deliveries.Writer.TryComplete();await worker.WaitAsync(TimeSpan.FromSeconds(3));}
    }
}
