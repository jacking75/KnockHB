using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text;
using RemoteAssist.Host.Core.Ipc;

internal static class IpcRegression
{
    internal static async Task Run()
    {
        foreach(var text in new[]{"{\"v\":1,\"t\":\"heartbeat\",\"seq\":1,\"seq\":2}","{\"v\":1,\"t\":\"heartbeat\",\"seq\":1,\"extra\":0}","{\"v\":1,\"t\":\"launch-arbitrary\",\"seq\":1}","{\"v\":1,\"t\":\"start\",\"seq\":1,\"nonce\":\"not-hello\"}"})
            await Reject(()=>Task.FromResult(LocalIpcMessage.Parse(Encoding.UTF8.GetBytes(text))));
        var message=Frame("{\"v\":1,\"t\":\"state\",\"seq\":1,\"id\":\"test\",\"body\":\"한글 😀\"}");
        await using(var input=new LocalIpcChannel(new SplitStream(message)))
        { var parsed=await input.ReceiveAsync(TimeSpan.FromSeconds(1));Require(parsed?.Body?.GetString()=="한글 😀","partial UTF8 damaged"); }
        await using(var input=new LocalIpcChannel(new SplitStream(message.Concat(message).ToArray())))
        {await input.ReceiveAsync(TimeSpan.FromSeconds(1));await Reject(()=>input.ReceiveAsync(TimeSpan.FromSeconds(1)));await Reject(()=>input.SendAsync("heartbeat"));}
        foreach(var broken in new[]{message[..^1],new byte[]{255,255,255,255},new byte[]{1,0}})
            await using(var input=new LocalIpcChannel(new SplitStream(broken)))await Reject(()=>input.ReceiveAsync(TimeSpan.FromSeconds(1)));
        try {await PipeCase(false);await PipeCase(true);}
        catch(Exception ex){throw new Exception("실제 pipe 검사: "+ex);}
    }
    static async Task PipeCase(bool mismatch)
    {
        var name="RemoteAssist-ipc-test-"+Guid.NewGuid().ToString("N");
        await using var server=new NamedPipeServerStream(name,PipeDirection.InOut,1,PipeTransmissionMode.Byte,PipeOptions.Asynchronous|PipeOptions.CurrentUserOnly);
        await using var client=new NamedPipeClientStream(".",name,PipeDirection.InOut,PipeOptions.Asynchronous);
        using var ct=new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await Task.WhenAll(server.WaitForConnectionAsync(ct.Token),client.ConnectAsync(ct.Token));
        await using var service=new LocalIpcChannel(server);await using var helper=new LocalIpcChannel(client);
        var nonce=new string('A',64);
        var auth=Task.WhenAll(service.AuthenticateAsync("service","helper",nonce,ct.Token),helper.AuthenticateAsync("helper","service",mismatch?new string('B',64):nonce,ct.Token));
        if(mismatch) {await Reject(()=>auth);await Reject(()=>helper.SendAsync("heartbeat"));return;}
        await auth;
        var receiving=service.ReceiveAsync(TimeSpan.FromSeconds(1),ct.Token);
        await helper.SendAsync("consent-request",new {displayName="지원자 😀"},"request",ct:ct.Token);
        var message=await receiving;
        Require(message?.Type=="consent-request"&&message.Sequence==2&&message.Body?.GetProperty("displayName").GetString()=="지원자 😀","real pipe authenticated data damaged");
    }
    static byte[] Frame(string json)
    {
        var bytes=Encoding.UTF8.GetBytes(json);var result=new byte[bytes.Length+4];BinaryPrimitives.WriteUInt32LittleEndian(result,(uint)bytes.Length);bytes.CopyTo(result,4);return result;
    }
    static void Require(bool value,string reason){if(!value)throw new Exception(reason);}
    static async Task Reject(Func<Task> run)
    {
        try{await run();}catch{return;}throw new Exception("invalid IPC accepted");
    }
    sealed class SplitStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer,CancellationToken cancellationToken=default)=>base.ReadAsync(buffer[..Math.Min(buffer.Length,1)],cancellationToken);
    }
}
