// Read-only E05 prerequisite inventory. Does not launch/elevate processes or inject input.
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using RemoteAssist.Host.Core.Capture;
using Vortice.DXGI;

SetProcessDpiAwarenessContext(-4);
try
{
    var outputs=new List<object>();
    using var factory=DXGI.CreateDXGIFactory1<IDXGIFactory1>();
    for(uint a=0;factory.EnumAdapters1(a,out var adapter).Success;a++)using(adapter)
        for(uint o=0;adapter.EnumOutputs(o,out var output).Success;o++)using(output)
        {var d=output.Description;outputs.Add(new{adapter=adapter.Description1.Description,deviceName=d.DeviceName,handle=d.Monitor.ToString(),rotation=d.Rotation.ToString()});}
    var desktop=OpenInputDesktop(0,false,1);
    string? inputDesktop=null;var desktopError=0;
    if(desktop!=0) {try{inputDesktop=Name(desktop);}finally{CloseDesktop(desktop);}}else desktopError=Marshal.GetLastWin32Error();
    var process=Process.GetCurrentProcess();
    using var identity=WindowsIdentity.GetCurrent();
    var result=new {utc=DateTimeOffset.UtcNow,processId=process.Id,processSession=process.SessionId,consoleSession=WTSGetActiveConsoleSessionId(),
        elevated=new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator),threadDesktop=Name(GetThreadDesktop(GetCurrentThreadId())),inputDesktop,desktopError,
        wgcSupported=WgcScreenCapture.IsSupported(),monitors=Monitors.Enumerate().Select(m=>new{m.Index,m.DeviceName,m.Width,m.Height,handle=m.Handle.ToString()}),dxgiOutputs=outputs};
    Console.WriteLine(JsonSerializer.Serialize(result,new JsonSerializerOptions{WriteIndented=true}));return 0;
}
catch(Exception ex){Console.Error.WriteLine(ex.Message);return 1;}

static string? Name(nint handle)
{
    GetUserObjectInformation(handle,2,null,0,out var required);if(required==0)return null;
    var name=new StringBuilder(checked((int)required/2));return GetUserObjectInformation(handle,2,name,required,out _)?name.ToString():null;
}
[DllImport("user32.dll")] static extern bool SetProcessDpiAwarenessContext(nint value);
[DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
[DllImport("kernel32.dll")] static extern uint WTSGetActiveConsoleSessionId();
[DllImport("user32.dll")] static extern nint GetThreadDesktop(uint threadId);
[DllImport("user32.dll",SetLastError=true)] static extern nint OpenInputDesktop(uint flags,[MarshalAs(UnmanagedType.Bool)]bool inherit,uint access);
[DllImport("user32.dll")] static extern bool CloseDesktop(nint desktop);
[DllImport("user32.dll",EntryPoint="GetUserObjectInformationW",CharSet=CharSet.Unicode,SetLastError=true)] static extern bool GetUserObjectInformation(nint handle,int index,StringBuilder? text,uint bytes,out uint needed);
