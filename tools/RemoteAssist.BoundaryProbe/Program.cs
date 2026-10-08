using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using RemoteAssist.BoundaryProbe;
using RemoteAssist.Host.Core.Capture;

if(args.Length==2 && args[0] is "--user-probe" or "--system-probe")
{
    SetProcessDpiAwarenessContext(-4);
    if(!Guid.TryParseExact(args[1],"N",out _))return 2;
    using var pipe=new NamedPipeClientStream(".",args[1],PipeDirection.Out,PipeOptions.None,TokenImpersonationLevel.Identification);
    pipe.Connect(10000);
    using var writer=new StreamWriter(pipe,new UTF8Encoding(false)) {AutoFlush=true};
    writer.WriteLine(JsonSerializer.Serialize(Identity(args[0])));
    if(args[0]=="--user-probe")writer.WriteLine(JsonSerializer.Serialize(InputProbe.Run()));
    if(args[0]=="--system-probe")
    {
        if(!WindowsIdentity.GetCurrent().IsSystem)return 3;
        writer.WriteLine(JsonSerializer.Serialize(InputProbe.Run()));
        var seconds=int.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"observation-seconds")));
        if(seconds is < 15 or > 120)return 4;
        string? previous=null;
        var deadline=Stopwatch.StartNew();
        while(deadline.Elapsed<TimeSpan.FromSeconds(seconds))
        {
            var desktop=Native.OpenInputDesktop(0,false,0x1FF);
            var current=desktop==0?"inaccessible:"+Marshal.GetLastWin32Error():Native.Name(desktop);
            if(desktop!=0)Native.CloseDesktop(desktop);
            if(current!=previous)
            {
                previous=current;
                var result=new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
                var thread=new Thread(()=>
                {
                    var original=Native.GetThreadDesktop(Native.GetCurrentThreadId());
                    var input=Native.OpenInputDesktop(0,false,0x1FF);
                    try
                    {
                        if(input==0)throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                        Native.Check(Native.SetThreadDesktop(input));
                        var attachedDesktop=Native.Name(input);
                        // Wait for the desktop compositor after a transition; never persist pixels.
                        Thread.Sleep(300);
                        var monitor=Monitors.Enumerate().First();
                        using var capture=new DxgiScreenCapture(monitor.Index);
                        var clock=Stopwatch.StartNew();
                        while(clock.Elapsed<TimeSpan.FromSeconds(5))
                            if(capture.TryGetFrame(TimeSpan.FromMilliseconds(100),out var frame))
                            {
                                var pointer=attachedDesktop=="Winlogon"?InputProbe.SecurePointer():null;
                                result.TrySetResult(new {utc=DateTimeOffset.UtcNow,kind="desktop-capture",desktop=current,attachedDesktop,threadDesktop=Native.Name(Native.GetThreadDesktop(Native.GetCurrentThreadId())),frame.Width,frame.Height,frame.Stride,hash=Convert.ToHexString(SHA256.HashData(frame.Bgra.Span)),pointer});
                                return;
                            }
                        throw new TimeoutException("데스크톱에서 5초 안에 프레임을 얻지 못했다.");
                    }
                    catch(Exception ex) {result.TrySetResult(new {utc=DateTimeOffset.UtcNow,kind="desktop-capture",desktop=current,error=ex.GetType().Name,message=ex.Message});}
                    finally {if(input!=0){Native.SetThreadDesktop(original);Native.CloseDesktop(input);}}
                }) {IsBackground=true};
                thread.Start();
                writer.WriteLine(JsonSerializer.Serialize(await result.Task.WaitAsync(TimeSpan.FromSeconds(8))));
            }
            Thread.Sleep(100);
        }
    }
    return 0;
}
if(args.Length!=1 || args[0]!="--service") {Console.Error.WriteLine("이 도구는 scripts/invoke-boundary-probe.ps1의 일회성 서비스로 실행한다.");return 2;}
var builder=Host.CreateApplicationBuilder(Array.Empty<string>());
builder.Services.AddWindowsService(options=>options.ServiceName="RemoteAssistBoundaryProbe");
builder.Services.AddHostedService<BoundaryWorker>();
await builder.Build().RunAsync();
return 0;

static object Identity(string role)
{
    using var identity=WindowsIdentity.GetCurrent();
    var desktop=Native.OpenInputDesktop(0,false,1);
    var input=desktop==0?null:Native.Name(desktop);var error=desktop==0?Marshal.GetLastWin32Error():0;
    if(desktop!=0)Native.CloseDesktop(desktop);
    return new {utc=DateTimeOffset.UtcNow,kind="identity",role,pid=Environment.ProcessId,session=Process.GetCurrentProcess().SessionId,console=Native.WTSGetActiveConsoleSessionId(),system=identity.IsSystem,admin=new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator),inputDesktop=input,desktopError=error,threadDesktop=Native.Name(Native.GetThreadDesktop(Native.GetCurrentThreadId()))};
}
[DllImport("user32.dll")] static extern bool SetProcessDpiAwarenessContext(nint value);

sealed class BoundaryWorker(IHostApplicationLifetime lifetime) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var file=Path.Combine(AppContext.BaseDirectory,"result.ndjson");
        using var writer=new StreamWriter(new FileStream(file,FileMode.CreateNew,FileAccess.Write,FileShare.Read),new UTF8Encoding(false)) {AutoFlush=true};
        using var identity=WindowsIdentity.GetCurrent();
        var session=Native.WTSGetActiveConsoleSessionId();
        writer.WriteLine(JsonSerializer.Serialize(new {utc=DateTimeOffset.UtcNow,kind="service",session=Process.GetCurrentProcess().SessionId,console=session,system=identity.IsSystem}));
        try
        {
            if(!identity.IsSystem || Process.GetCurrentProcess().SessionId!=0 || session==uint.MaxValue)throw new InvalidOperationException("활성 콘솔과 Session 0 SYSTEM 서비스가 필요하다.");
            foreach(var system in new[]{false,true})
            {
                var name=Guid.NewGuid().ToString("N");
                var acl=new PipeSecurity();acl.SetAccessRuleProtection(true,false);
                acl.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid,null),PipeAccessRights.FullControl,AccessControlType.Allow));
                // User SID is obtained from WTS, never from a remote message.
                var sid=SessionSid(session);
                acl.AddAccessRule(new PipeAccessRule(sid,PipeAccessRights.ReadWrite,AccessControlType.Allow));
                using var pipe=NamedPipeServerStreamAcl.Create(name,PipeDirection.In,1,PipeTransmissionMode.Byte,PipeOptions.Asynchronous,8192,8192,acl);
                using var child=Native.Launch(system?"--system-probe":"--user-probe",name,session,system);
                using var timeout=CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);timeout.CancelAfter(TimeSpan.FromSeconds(system?130:15));
                do
                {
                    await pipe.WaitForConnectionAsync(timeout.Token);
                    Native.Check(Native.GetNamedPipeClientProcessId(pipe.SafePipeHandle,out var pid));
                    if(pid==child.Pid)break;
                    pipe.Disconnect();
                }while(true);
                Task? sasTask=null;
                if(system && File.Exists(Path.Combine(AppContext.BaseDirectory,"sas-request")))
                    sasTask=Task.Run(async()=> {await Task.Delay(5000,timeout.Token);SendSAS(false);},timeout.Token);
                // Bounded framing prevents a compromised probe from growing a privileged buffer.
                var buffer=new byte[8192];var line=new List<byte>();var records=0;
                while(true)
                {
                    var count=await pipe.ReadAsync(buffer,timeout.Token);if(count==0)break;
                    for(var i=0;i<count;i++)
                    {
                        if(buffer[i]==10)
                        {
                            if(++records>128)throw new InvalidDataException("report 개수 초과");
                            var json=Encoding.UTF8.GetString(line.ToArray()).TrimEnd('\r');using var parsed=JsonDocument.Parse(json);
                            writer.WriteLine(json);line.Clear();
                        }
                        else {if(line.Count>=8192)throw new InvalidDataException("report 크기 초과");line.Add(buffer[i]);}
                    }
                }
                if(line.Count!=0)throw new InvalidDataException("미완성 report");
                if(sasTask!=null) {await sasTask;writer.WriteLine(JsonSerializer.Serialize(new {utc=DateTimeOffset.UtcNow,kind="sas-called",policy="services",result="반환만으로 SAS 성공을 판정하지 않는다"}));}
            }
        }
        catch(Exception ex) {writer.WriteLine(JsonSerializer.Serialize(new {utc=DateTimeOffset.UtcNow,kind="service-error",error=ex.GetType().Name,message=ex.Message}));}
        finally {writer.WriteLine(JsonSerializer.Serialize(new {utc=DateTimeOffset.UtcNow,kind="service-finished"}));lifetime.StopApplication();}
    }
    static SecurityIdentifier SessionSid(uint session)
    {
        Native.Enable("SeTcbPrivilege");
        Native.Check(WTSQueryUserToken(session,out var token));
        using(token)using(var user=new WindowsIdentity(token.DangerousGetHandle()))return user.User??throw new InvalidOperationException("사용자 SID 없음");
    }
    [DllImport("wtsapi32.dll",SetLastError=true)] static extern bool WTSQueryUserToken(uint session,out Native.KernelHandle token);
    [DllImport("sas.dll")] static extern void SendSAS([MarshalAs(UnmanagedType.Bool)]bool asUser);
}
