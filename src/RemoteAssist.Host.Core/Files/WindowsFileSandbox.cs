using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;
using RemoteAssist.Protocol;

namespace RemoteAssist.Host.Core.Files;

/// <summary>Handle-relative local NTFS files. No SYSTEM write, paths on the wire, overwrite, or file execution.</summary>
public sealed class WindowsFileSandbox:IDisposable
{
    readonly List<SafeFileHandle> directories=[];
    readonly string folder;
    bool disposed;
    public WindowsFileSandbox(string selectedFolder)
    {
        if(IntPtr.Size!=8)throw new PlatformNotSupportedException("파일 전송은 Windows x64가 필요하다.");
        using var identity=WindowsIdentity.GetCurrent();if(identity.IsSystem)throw new UnauthorizedAccessException("SYSTEM 파일 전송을 거부한다.");
        folder=LocalPath(selectedFolder).TrimEnd('\\');var root=Path.GetPathRoot(folder)!;
        if(GetDriveType(root)!=3||new DriveInfo(root).DriveFormat!="NTFS")throw new NotSupportedException("로컬 고정 NTFS 폴더를 선택해야 한다.");
        try
        {
            var drive=CreateFile(root,0x100081,3,0,3,0x02200000,0);CheckHandle(drive);directories.Add(drive);CheckObject(drive,root,true);
            var parts=folder[root.Length..].Split('\\',StringSplitOptions.RemoveEmptyEntries);var expected=root.TrimEnd('\\');
            foreach(var part in parts)
            {
                if(!FileMessage.SafeName(part))throw new InvalidDataException("선택 폴더의 이름이 허용되지 않는다.");
                var child=Relative(directories[^1],part,0x100081,1,0x200021,3);directories.Add(child);expected+="\\"+part;CheckObject(child,expected,true);
            }
        }
        catch{Dispose();throw;}
    }
    public static string LocalPath(string path)
    {
        if(!Path.IsPathFullyQualified(path)||path.Length>240||path.Length<3||!char.IsAsciiLetter(path[0])||path[1]!=':'||path[2]!='\\'||path[2..].Contains(':')||path.StartsWith(@"\\",StringComparison.Ordinal)||path.Contains('/'))throw new InvalidDataException("로컬 드라이브의 일반 경로만 허용한다.");
        var full=Path.GetFullPath(path);if(!string.Equals(path.TrimEnd('\\'),full.TrimEnd('\\'),StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("경로 정규화·상대 성분을 거부한다.");return full;
    }
    public FileStream Read(string name)
    {
        ObjectDisposedException.ThrowIf(disposed,this);if(!FileMessage.SafeName(name))throw new InvalidDataException("전송 파일 이름 오류다.");
        var handle=Relative(directories[^1],name,0x80100000,1,0x200060,1);
        try{CheckObject(handle,Path.Combine(folder,name),false);return new FileStream(handle,FileAccess.Read,16384,false);}catch{handle.Dispose();throw;}
    }
    public Candidate Create(string finalName)
    {
        ObjectDisposedException.ThrowIf(disposed,this);if(!FileMessage.SafeName(finalName))throw new InvalidDataException("수신 파일 이름 오류다.");
        var temp=".remoteassist-"+Guid.NewGuid().ToString("N")+".part";
        var handle=Relative(directories[^1],temp,0xC0110000,2,0x201060,1); // READ|WRITE|DELETE|SYNCHRONIZE; CREATE, NON_DIRECTORY, DELETE_ON_CLOSE, NO_FOLLOW
        try
        {
            CheckObject(handle,Path.Combine(folder,temp),false);
            return new Candidate(new FileStream(handle,FileAccess.ReadWrite,16384,false),directories[^1],finalName);
        }
        catch{handle.Dispose();throw;}
    }
    public void Dispose(){if(disposed)return;disposed=true;foreach(var handle in directories.AsEnumerable().Reverse())handle.Dispose();directories.Clear();}
    public sealed class Candidate:IDisposable
    {
        readonly SafeFileHandle parent;
        readonly string finalName;
        bool committed,closed;
        public FileStream Stream {get;}
        public Candidate(FileStream stream,SafeFileHandle directory,string finalName)
        {
            if(!DuplicateHandle(GetCurrentProcess(),directory,GetCurrentProcess(),out parent,0,false,2))throw new Win32Exception(Marshal.GetLastWin32Error());
            Stream=stream;this.finalName=finalName;
        }
        public void Commit()
        {
            ObjectDisposedException.ThrowIf(closed,this);if(committed)throw new InvalidOperationException("이미 확정한 파일이다.");
            Stream.Flush(true);var bytes=Encoding.Unicode.GetBytes(finalName);var length=Math.Max(32,22+bytes.Length);var buffer=Marshal.AllocHGlobal(length);
            try
            {
                Marshal.Copy(new byte[length],0,buffer,length);Marshal.WriteIntPtr(buffer,8,parent.DangerousGetHandle());Marshal.WriteInt32(buffer,16,bytes.Length);Marshal.Copy(bytes,0,buffer+20,bytes.Length);
                var status=NtSetInformationFile(Stream.SafeFileHandle,out _,buffer,(uint)length,11);Status(status);committed=true;
            }
            finally{Marshal.FreeHGlobal(buffer);}
        }
        public void Dispose(){if(closed)return;closed=true;try{Stream.Dispose();}finally{parent.Dispose();}} // OS deletes only the temporary link, even on a process crash.
    }
    static SafeFileHandle Relative(SafeFileHandle parent,string name,uint access,uint disposition,uint options,uint sharing)
    {
        var nameBuffer=Marshal.StringToHGlobalUni(name);var unicode=Marshal.AllocHGlobal(Marshal.SizeOf<UnicodeString>());
        var pinned=false;
        try
        {
            parent.DangerousAddRef(ref pinned);
            Marshal.StructureToPtr(new UnicodeString {Length=checked((ushort)(name.Length*2)),MaximumLength=checked((ushort)(name.Length*2+2)),Buffer=nameBuffer},unicode,false);
            var attributes=new ObjectAttributes {Length=(uint)Marshal.SizeOf<ObjectAttributes>(),RootDirectory=parent.DangerousGetHandle(),ObjectName=unicode,Attributes=0x1040};
            var status=NtCreateFile(out var handle,access,ref attributes,out _,0,0x80,sharing,disposition,options,0,0);
            if(status<0){handle.Dispose();throw new Win32Exception((int)RtlNtStatusToDosError(status));}CheckHandle(handle);return handle;
        }
        finally{if(pinned)parent.DangerousRelease();Marshal.FreeHGlobal(unicode);Marshal.FreeHGlobal(nameBuffer);}
    }
    static void CheckObject(SafeFileHandle handle,string expected,bool directory)
    {
        if(!GetFileInformationByHandle(handle,out var info))throw new Win32Exception(Marshal.GetLastWin32Error());
        if((info.Attributes&0x400)!=0||((info.Attributes&0x10)!=0)!=directory||GetFileType(handle)!=1)throw new UnauthorizedAccessException("reparse·장치·파일 타입을 거부한다.");
        var text=new StringBuilder(1024);var count=GetFinalPathNameByHandle(handle,text,(uint)text.Capacity,0);
        if(count==0||count>=text.Capacity||!text.ToString().StartsWith(@"\\?\",StringComparison.Ordinal)||!string.Equals(text.ToString()[4..].TrimEnd('\\'),expected.TrimEnd('\\'),StringComparison.OrdinalIgnoreCase))throw new UnauthorizedAccessException("열린 파일의 최종 경로가 선택 경계와 다르다.");
    }
    static void CheckHandle(SafeFileHandle handle){if(handle.IsInvalid){var error=Marshal.GetLastWin32Error();handle.Dispose();throw new Win32Exception(error);}}
    static void Status(int status){if(status<0)throw new Win32Exception((int)RtlNtStatusToDosError(status));}
    [StructLayout(LayoutKind.Sequential)]struct UnicodeString {public ushort Length,MaximumLength;public nint Buffer;}
    [StructLayout(LayoutKind.Sequential)]struct ObjectAttributes {public uint Length;public nint RootDirectory,ObjectName;public uint Attributes;public nint SecurityDescriptor,SecurityQualityOfService;}
    [StructLayout(LayoutKind.Sequential)]struct IoStatus {public nint Status;public nuint Information;}
    [StructLayout(LayoutKind.Sequential)]struct FileInfo {public uint Attributes;public System.Runtime.InteropServices.ComTypes.FILETIME Created,Accessed,Written;public uint VolumeSerial,SizeHigh,SizeLow,Links,IndexHigh,IndexLow;}
    [DllImport("kernel32.dll",EntryPoint="CreateFileW",CharSet=CharSet.Unicode,SetLastError=true)]static extern SafeFileHandle CreateFile(string path,uint access,uint share,nint security,uint disposition,uint flags,nint template);
    [DllImport("kernel32.dll",EntryPoint="GetDriveTypeW",CharSet=CharSet.Unicode)]static extern uint GetDriveType(string root);
    [DllImport("kernel32.dll",SetLastError=true)]static extern bool GetFileInformationByHandle(SafeFileHandle handle,out FileInfo info);
    [DllImport("kernel32.dll")]static extern uint GetFileType(SafeFileHandle handle);
    [DllImport("kernel32.dll",EntryPoint="GetFinalPathNameByHandleW",CharSet=CharSet.Unicode,SetLastError=true)]static extern uint GetFinalPathNameByHandle(SafeFileHandle handle,StringBuilder path,uint length,uint flags);
    [DllImport("ntdll.dll")]static extern int NtCreateFile(out SafeFileHandle handle,uint access,ref ObjectAttributes attributes,out IoStatus result,nint allocation,uint fileAttributes,uint share,uint disposition,uint options,nint ea,uint eaLength);
    [DllImport("ntdll.dll")]static extern int NtSetInformationFile(SafeFileHandle handle,out IoStatus result,nint info,uint length,int informationClass);
    [DllImport("ntdll.dll")]static extern uint RtlNtStatusToDosError(int status);
    [DllImport("kernel32.dll")]static extern nint GetCurrentProcess();
    [DllImport("kernel32.dll",SetLastError=true)]static extern bool DuplicateHandle(nint sourceProcess,SafeFileHandle source,nint targetProcess,out SafeFileHandle target,uint access,bool inherit,uint options);
}
