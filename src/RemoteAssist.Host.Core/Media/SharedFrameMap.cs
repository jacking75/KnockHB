using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;
using RemoteAssist.Host.Core.Windows;

namespace RemoteAssist.Host.Core.Media;

public sealed record SharedFrame(long Sequence,long PermissionGeneration,long TopologyGeneration,long AdminGeneration,int Width,int Height,int Stride,bool SecureDesktop,long TimestampMs,byte[] Pixels);

/// <summary>Two bounded BGRA slots. SYSTEM writes; the approved user gets only SECTION_MAP_READ.</summary>
public sealed unsafe class SharedFrameMap:IDisposable
{
    public const int HeaderBytes=64,PayloadBytes=16*1024*1024,SlotBytes=HeaderBytes+PayloadBytes,Capacity=2*SlotBytes;
    readonly SessionProcess.KernelHandle handle;
    readonly object gate=new();
    readonly bool writable;
    nint view;
    long sequence;
    public string Name {get;}
    SharedFrameMap(string name,SessionProcess.KernelHandle handle,bool writable)
    {
        Name=name;this.handle=handle;this.writable=writable;view=MapViewOfFile(handle,writable?6u:4u,0,0,(nuint)Capacity);
        if(view==0){handle.Dispose();throw new Win32Exception(Marshal.GetLastWin32Error());}
    }
    public static SharedFrameMap Create(string userSid)
    {
        using var identity=WindowsIdentity.GetCurrent();
        if(!identity.IsSystem||System.Diagnostics.Process.GetCurrentProcess().SessionId!=0)throw new UnauthorizedAccessException("프레임 mapping은 서비스만 만든다.");
        var sid=new SecurityIdentifier(userSid).Value;
        var name="Global\\RemoteAssist-"+Guid.NewGuid().ToString("N");
        if(!ConvertStringSecurityDescriptorToSecurityDescriptor($"D:P(A;;GA;;;SY)(A;;0x0004;;;{sid})",1,out var descriptor,out _))throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            var attributes=new SecurityAttributes {Size=Marshal.SizeOf<SecurityAttributes>(),Descriptor=descriptor};
            var handle=CreateFileMapping(-1,ref attributes,4,0,Capacity,name);var error=Marshal.GetLastWin32Error();
            if(handle.IsInvalid||error==183){handle.Dispose();throw new Win32Exception(error);}
            return new(name,handle,true);
        }
        finally{LocalFree(descriptor);}
    }
    public static SharedFrameMap Open(string name,bool writable)
    {
        if(!name.StartsWith("Global\\RemoteAssist-",StringComparison.Ordinal)||!Guid.TryParseExact(name[20..],"N",out _))throw new ArgumentException("고유 프레임 mapping 이름 오류다.");
        if(writable){using var identity=WindowsIdentity.GetCurrent();if(!identity.IsSystem)throw new UnauthorizedAccessException("SYSTEM만 프레임을 쓴다.");}
        var handle=OpenFileMapping(writable?6u:4u,false,name);if(handle.IsInvalid){handle.Dispose();throw new Win32Exception(Marshal.GetLastWin32Error());}
        return new(name,handle,writable);
    }
    internal static SharedFrameMap CreateAnonymousForTests()
    {
        var attributes=new SecurityAttributes {Size=Marshal.SizeOf<SecurityAttributes>()};
        var handle=CreateFileMapping(-1,ref attributes,4,0,Capacity,null);
        if(handle.IsInvalid){handle.Dispose();throw new Win32Exception(Marshal.GetLastWin32Error());}return new("",handle,true);
    }
    public void Publish(ReadOnlySpan<byte> pixels,int width,int height,int stride,long permission,long topology,long admin,bool secure,long timestampMs)
    {
        Validate(width,height,stride,pixels.Length,permission,topology,admin,timestampMs);
        lock(gate)
        {
            DemandWritable();var next=checked(sequence+1);var slot=(byte*)view+(next%2)*SlotBytes;
            Volatile.Write(ref *(long*)slot,checked(next*2-1));
            *(long*)(slot+8)=permission;*(long*)(slot+16)=topology;*(long*)(slot+24)=admin;
            *(int*)(slot+32)=width;*(int*)(slot+36)=height;*(int*)(slot+40)=stride;*(int*)(slot+44)=pixels.Length;
            *(int*)(slot+48)=secure?1:0;*(int*)(slot+52)=0;*(long*)(slot+56)=timestampMs;
            pixels.CopyTo(new Span<byte>(slot+HeaderBytes,pixels.Length));
            Volatile.Write(ref *(long*)slot,checked(next*2));sequence=next;
        }
    }
    public bool TryRead(long after,long permission,long topology,long admin,out SharedFrame? frame)
    {
        frame=null;
        lock(gate)
        {
            ObjectDisposedException.ThrowIf(view==0,this);
            var a=Volatile.Read(ref *(long*)view);var b=Volatile.Read(ref *(long*)((byte*)view+SlotBytes));
            var index=b>a?1:0;var commit=Math.Max(a,b);if(commit<=0||(commit&1)!=0||commit/2<=after)return false;
            var slot=(byte*)view+index*SlotBytes;
            var p=*(long*)(slot+8);var t=*(long*)(slot+16);var g=*(long*)(slot+24);
            var width=*(int*)(slot+32);var height=*(int*)(slot+36);var stride=*(int*)(slot+40);var bytes=*(int*)(slot+44);var secure=*(int*)(slot+48);var timestamp=*(long*)(slot+56);
            var reserved=*(int*)(slot+52);
            if(Volatile.Read(ref *(long*)slot)!=commit)return false;
            if(p!=permission||t!=topology||g!=admin)return false;
            Validate(width,height,stride,bytes,p,t,g,timestamp);if(secure is not (0 or 1)||reserved!=0)throw new InvalidDataException("프레임 header 오류다.");
            var pixels=new byte[bytes];new ReadOnlySpan<byte>(slot+HeaderBytes,bytes).CopyTo(pixels);
            if(Volatile.Read(ref *(long*)slot)!=commit){Array.Clear(pixels);return false;}
            frame=new(commit/2,p,t,g,width,height,stride,secure==1,timestamp,pixels);return true;
        }
    }
    static void Validate(int width,int height,int stride,int bytes,long permission,long topology,long admin,long timestamp)
    {
        if(width is <2 or >3840||height is <2 or >3840||stride!=checked(width*4)||bytes!=checked(stride*height)||bytes>PayloadBytes||permission<1||topology<1||admin<1||timestamp<0)
            throw new InvalidDataException("특권 프레임 크기·세대 오류다.");
    }
    void DemandWritable(){ObjectDisposedException.ThrowIf(view==0,this);if(!writable)throw new UnauthorizedAccessException("읽기 전용 프레임 mapping이다.");}
    public void Clear(){lock(gate){DemandWritable();new Span<byte>((void*)view,Capacity).Clear();}}
    public void Dispose(){lock(gate){if(view==0)return;if(writable)new Span<byte>((void*)view,Capacity).Clear();UnmapViewOfFile(view);view=0;handle.Dispose();}}
    [StructLayout(LayoutKind.Sequential)]struct SecurityAttributes {public int Size;public nint Descriptor;public int Inherit;}
    [DllImport("advapi32.dll",EntryPoint="ConvertStringSecurityDescriptorToSecurityDescriptorW",CharSet=CharSet.Unicode,SetLastError=true)]static extern bool ConvertStringSecurityDescriptorToSecurityDescriptor(string value,uint revision,out nint descriptor,out uint bytes);
    [DllImport("kernel32.dll")]static extern nint LocalFree(nint pointer);
    [DllImport("kernel32.dll",EntryPoint="CreateFileMappingW",CharSet=CharSet.Unicode,SetLastError=true)]static extern SessionProcess.KernelHandle CreateFileMapping(nint file,ref SecurityAttributes attributes,uint protect,uint high,uint low,string? name);
    [DllImport("kernel32.dll",EntryPoint="OpenFileMappingW",CharSet=CharSet.Unicode,SetLastError=true)]static extern SessionProcess.KernelHandle OpenFileMapping(uint access,bool inherit,string name);
    [DllImport("kernel32.dll",SetLastError=true)]static extern nint MapViewOfFile(SessionProcess.KernelHandle mapping,uint access,uint high,uint low,nuint bytes);
    [DllImport("kernel32.dll")]static extern bool UnmapViewOfFile(nint address);
}
