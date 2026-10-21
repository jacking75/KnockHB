using System.Runtime.InteropServices;
using System.IO;
using System.Runtime.InteropServices.ComTypes;
using System.Security.Cryptography;
using System.Text;
using ComData = System.Runtime.InteropServices.ComTypes.IDataObject;

namespace RemoteAssist.E2e;

// Only Capture/Restore access OS clipboard. Private bytes/names/hashes stay in RAM.
[ComVisible(true), ClassInterface(ClassInterfaceType.None)]
internal sealed class ClipboardSnapshot : ComData, IDisposable
{
    readonly List<(FORMATETC Format, STGMEDIUM Medium)> values=[];
    bool disposed;
    internal int Count => values.Count;
    public static ClipboardSnapshot Capture()
    {
        Marshal.ThrowExceptionForHR(OleGetClipboard(out var source));
        try { return CaptureFrom(source,new()); } finally { ClipboardStorage.Release(source); }
    }
    internal static ClipboardSnapshot CaptureFrom(ComData source,ClipboardCopyBudget budget)
    {
        var result=new ClipboardSnapshot();
        try
        {
            var enumerator=source.EnumFormatEtc(DATADIR.DATADIR_GET);
            try
            {
                var item=new FORMATETC[1];var count=new int[1];
                while(true)
                {
                    budget.CheckTime();item[0]=default;count[0]=0;
                    var hr=enumerator.Next(1,item,count);var format=item[0];
                    try
                    {
                        if(hr==1&&count[0]==0)break;
                        if(hr!=0||count[0]!=1)throw new InvalidDataException("clipboard-enumeration-incomplete");
                        budget.ReserveBytes((ulong)TargetBytes(format.ptd).Length);
                        if(format.tymed==0||((int)format.tymed&~127)!=0)throw new NotSupportedException("clipboard-medium-not-restorable");
                        foreach(var kind in new[]{TYMED.TYMED_HGLOBAL,TYMED.TYMED_GDI,TYMED.TYMED_ENHMF,TYMED.TYMED_MFPICT,TYMED.TYMED_ISTREAM,TYMED.TYMED_ISTORAGE,TYMED.TYMED_FILE})
                        {
                            if((format.tymed&kind)==0)continue;
                            // ReleaseStgMedium(TYMED_FILE) may delete the file.
                            // Refuse before GetData, never materialize a user's file.
                            if(kind==TYMED.TYMED_FILE)throw new NotSupportedException("clipboard-medium-not-restorable");
                            budget.ReserveFormat();var request=format;request.tymed=kind;
                            source.GetData(ref request,out var original);
                            try
                            {
                                if(original.tymed!=kind)throw new InvalidDataException("clipboard-medium-mismatch");
                                if(result.values.Any(v=>v.Format.tymed==kind&&Matches(v.Format,request)))throw new InvalidDataException("clipboard-duplicate-format");
                                budget.ReserveBytes((ulong)TargetBytes(request.ptd).Length);
                                var owned=CopyFormat(request);
                                try { var copy=Duplicate(owned,original,budget);result.values.Add((owned,copy));owned.ptd=IntPtr.Zero; }
                                finally { if(owned.ptd!=IntPtr.Zero)Marshal.FreeCoTaskMem(owned.ptd); }
                            }
                            finally { ReleaseStgMedium(ref original); }
                        }
                    }
                    finally { if(format.ptd!=IntPtr.Zero)Marshal.FreeCoTaskMem(format.ptd); }
                }
            }
            finally { ClipboardStorage.Release(enumerator); }
            return result;
        }
        catch { result.Dispose();throw; }
    }
    internal static byte[] TargetBytes(IntPtr p)
    {
        if(p==IntPtr.Zero)return [];
        var size=Marshal.ReadInt32(p);
        if(size<12||size>65536)throw new InvalidDataException("clipboard-target-device-bound");
        for(var i=4;i<12;i+=2){var offset=unchecked((ushort)Marshal.ReadInt16(p,i));if(offset!=0&&(offset<12||offset>=size))throw new InvalidDataException("clipboard-target-device-offset");}
        var bytes=new byte[size];Marshal.Copy(p,bytes,0,size);return bytes;
    }
    internal static FORMATETC CopyFormat(FORMATETC value)
    {
        var bytes=TargetBytes(value.ptd);value.ptd=IntPtr.Zero;
        if(bytes.Length>0){value.ptd=Marshal.AllocCoTaskMem(bytes.Length);Marshal.Copy(bytes,0,value.ptd,bytes.Length);}return value;
    }
    static bool Matches(FORMATETC a,FORMATETC b)=>a.cfFormat==b.cfFormat&&a.dwAspect==b.dwAspect&&a.lindex==b.lindex&&
        (a.tymed&b.tymed)!=0&&TargetBytes(a.ptd).SequenceEqual(TargetBytes(b.ptd));
    internal static void CopyStream(IStream source,IStream destination,ClipboardCopyBudget budget)
    {
        IStream? input=null;long? position=null;var cloned=false;var p=Marshal.AllocCoTaskMem(8);
        try
        {
            budget.CheckTime();
            try { source.Clone(out input);cloned=true;if(ReferenceEquals(input,source)){source.Seek(0,1,p);position=Marshal.ReadInt64(p);} }
            catch(COMException){source.Seek(0,1,p);position=Marshal.ReadInt64(p);input=source;}
            input.Seek(0,0,IntPtr.Zero);var block=new byte[65536];
            while(true)
            {
                budget.CheckTime();Marshal.WriteInt64(p,0);input.Read(block,block.Length,p);var n=Marshal.ReadInt32(p);
                if(n<0||n>block.Length)throw new IOException("clipboard-stream-read");if(n==0)break;
                budget.ReserveBytes((ulong)n);destination.Write(block,n,p);if(Marshal.ReadInt32(p)!=n)throw new IOException("clipboard-stream-write");
            }
            destination.Seek(0,0,IntPtr.Zero);
        }
        finally
        {
            try { if(position.HasValue)source.Seek(position.Value,0,IntPtr.Zero); }
            finally { if(cloned)ClipboardStorage.Release(input);Marshal.FreeCoTaskMem(p); }
        }
    }
    static STGMEDIUM Duplicate(FORMATETC format,STGMEDIUM medium,ClipboardCopyBudget budget)
    {
        budget.CheckTime();if(medium.unionmember==IntPtr.Zero)throw new IOException("clipboard-null-medium");
        if(medium.tymed==TYMED.TYMED_ISTREAM)
        {
            var input=(IStream)Marshal.GetObjectForIUnknown(medium.unionmember);
            try
            {
                Marshal.ThrowExceptionForHR(CreateStreamOnHGlobal(IntPtr.Zero,true,out var output));
                try { CopyStream(input,output,budget);return InterfaceMedium(output,TYMED.TYMED_ISTREAM,typeof(IStream)); }
                finally { ClipboardStorage.Release(output); }
            }
            finally { ClipboardStorage.Release(input); }
        }
        if(medium.tymed==TYMED.TYMED_ISTORAGE)
        {
            var input=(IStorage)Marshal.GetObjectForIUnknown(medium.unionmember);
            try
            {
                var output=ClipboardStorage.Create();
                try { ClipboardStorage.Copy(input,output,budget);return InterfaceMedium(output,TYMED.TYMED_ISTORAGE,typeof(IStorage)); }
                finally { ClipboardStorage.Release(output); }
            }
            finally { ClipboardStorage.Release(input); }
        }
        if(medium.tymed is not (TYMED.TYMED_HGLOBAL or TYMED.TYMED_GDI or TYMED.TYMED_ENHMF or TYMED.TYMED_MFPICT))throw new NotSupportedException("clipboard-medium-not-restorable");
        _=NativeBytes(format,medium,budget);
        var handle=OleDuplicateData(medium.unionmember,unchecked((ushort)format.cfFormat),0);
        if(handle==IntPtr.Zero)throw new IOException("clipboard-duplicate-failed");return new(){tymed=medium.tymed,unionmember=handle};
    }
    internal static STGMEDIUM InterfaceMedium(object value,TYMED kind,Type type)=>new(){tymed=kind,unionmember=Marshal.GetComInterfaceForObject(value,type)};
    static byte[] NativeBytes(FORMATETC format,STGMEDIUM medium,ClipboardCopyBudget budget)
    {
        ulong size;IntPtr p=IntPtr.Zero,mf=IntPtr.Zero;var locked=false;Bitmap bitmap=default;
        try
        {
            if(medium.tymed==TYMED.TYMED_HGLOBAL)size=GlobalSize(medium.unionmember).ToUInt64();
            else if(medium.tymed==TYMED.TYMED_GDI)
            {
                if(format.cfFormat!=2||GetObject(medium.unionmember,Marshal.SizeOf<Bitmap>(),out bitmap)==0)throw new NotSupportedException("clipboard-gdi-kind");
                size=checked((ulong)Math.Abs((long)bitmap.height)*(ulong)bitmap.widthBytes);
            }
            else if(medium.tymed==TYMED.TYMED_ENHMF)size=GetEnhMetaFileBits(medium.unionmember,0,null);
            else { if(GlobalSize(medium.unionmember).ToUInt64()<(ulong)(IntPtr.Size==8?24:16))throw new IOException("clipboard-metafile-container");p=GlobalLock(medium.unionmember);locked=true;if(p==IntPtr.Zero)throw new IOException("clipboard-metafile-lock");mf=Marshal.ReadIntPtr(p,IntPtr.Size==8?16:12);size=GetMetaFileBitsEx(mf,0,null); }
            if(size==0&&medium.tymed!=TYMED.TYMED_HGLOBAL)throw new IOException("clipboard-native-medium-size");
            budget.ReserveBytes(size);if(size>int.MaxValue)throw new IOException("clipboard-medium-bound");var bytes=new byte[(int)size];
            switch(medium.tymed)
            {
                case TYMED.TYMED_HGLOBAL:if(size>0){p=GlobalLock(medium.unionmember);locked=true;if(p==IntPtr.Zero)throw new IOException("clipboard-memory-lock");Marshal.Copy(p,bytes,0,bytes.Length);}break;
                case TYMED.TYMED_GDI:if(GetBitmapBits(medium.unionmember,bytes.Length,bytes)!=bytes.Length)throw new IOException("clipboard-bitmap-read");break;
                case TYMED.TYMED_ENHMF:if(GetEnhMetaFileBits(medium.unionmember,(uint)bytes.Length,bytes)!=size)throw new IOException("clipboard-metafile-read");break;
                default:if(GetMetaFileBitsEx(mf,(uint)bytes.Length,bytes)!=size)throw new IOException("clipboard-metafile-read");var header=new byte[12];Marshal.Copy(p,header,0,12);bytes=header.Concat(bytes).ToArray();break;
            }
            if(medium.tymed==TYMED.TYMED_GDI)bytes=BitConverter.GetBytes(bitmap.width).Concat(BitConverter.GetBytes(bitmap.height))
                .Concat(BitConverter.GetBytes(bitmap.widthBytes)).Concat(BitConverter.GetBytes(bitmap.planes)).Concat(BitConverter.GetBytes(bitmap.bitsPixel)).Concat(bytes).ToArray();
            return bytes;
        }
        finally { if(locked)GlobalUnlock(medium.unionmember); }
    }
    internal bool EquivalentTo(ClipboardSnapshot other)
    {
        ObjectDisposedException.ThrowIf(disposed,this);if(values.Count!=other.values.Count)return false;
        var budget=new ClipboardCopyBudget(maxBytes:128*1024*1024);
        foreach(var entry in values)
        {
            var matches=other.values.Where(v=>Matches(v.Format,entry.Format)&&v.Format.tymed==entry.Format.tymed).ToArray();
            if(matches.Length!=1||!Fingerprint(entry.Format,entry.Medium,budget).SequenceEqual(Fingerprint(matches[0].Format,matches[0].Medium,budget)))return false;
        }
        return true;
    }
    static byte[] Fingerprint(FORMATETC format,STGMEDIUM medium,ClipboardCopyBudget budget)
    {
        if(medium.tymed==TYMED.TYMED_ISTREAM)
        {
            var input=(IStream)Marshal.GetObjectForIUnknown(medium.unionmember);
            try
            {
                Marshal.ThrowExceptionForHR(CreateStreamOnHGlobal(IntPtr.Zero,true,out var output));
                try { CopyStream(input,output,budget);output.Stat(out var stat,1);var bytes=new byte[checked((int)stat.cbSize)];output.Read(bytes,bytes.Length,IntPtr.Zero);return SHA256.HashData(bytes); }
                finally { ClipboardStorage.Release(output); }
            }
            finally { ClipboardStorage.Release(input); }
        }
        if(medium.tymed==TYMED.TYMED_ISTORAGE)
        {
            var storage=(IStorage)Marshal.GetObjectForIUnknown(medium.unionmember);
            try { return StorageFingerprint(storage,budget,0); }finally { ClipboardStorage.Release(storage); }
        }
        return SHA256.HashData(NativeBytes(format,medium,budget));
    }
    static byte[] StorageFingerprint(IStorage storage,ClipboardCopyBudget budget,int depth)
    {
        budget.CheckTime();if(depth>budget.MaxDepth)throw new IOException("clipboard-storage-depth");
        storage.Stat(out var stat,1);var rows=new List<string>{stat.clsid.ToString(),stat.grfStateBits.ToString(),
            stat.ctime.dwHighDateTime+":"+stat.ctime.dwLowDateTime,stat.atime.dwHighDateTime+":"+stat.atime.dwLowDateTime,stat.mtime.dwHighDateTime+":"+stat.mtime.dwLowDateTime};
        storage.EnumElements(0,IntPtr.Zero,0,out var enumerator);
        try
        {
            var entries=new List<STATSTG>();var item=new STATSTG[1];
            while(true){var hr=enumerator.Next(1,item,out var n);if(hr==1&&n==0)break;if(hr!=0||n!=1)throw new IOException("clipboard-storage-enumeration");budget.ReserveElement();entries.Add(item[0]);}
            foreach(var entry in entries.OrderBy(e=>e.pwcsName,StringComparer.Ordinal))
            {
                byte[] hash;
                if(entry.type==2)
                {
                    storage.OpenStream(entry.pwcsName,IntPtr.Zero,0x10,0,out var stream);
                    try { var m=InterfaceMedium(stream,TYMED.TYMED_ISTREAM,typeof(IStream));try{hash=Fingerprint(default,m,budget);}finally{ReleaseStgMedium(ref m);} }
                    finally { ClipboardStorage.Release(stream); }
                }
                else if(entry.type==1){storage.OpenStorage(entry.pwcsName,null,0x10,IntPtr.Zero,0,out var child);try{hash=StorageFingerprint(child,budget,depth+1);}finally{ClipboardStorage.Release(child);}}
                else throw new NotSupportedException("clipboard-storage-element");
                rows.Add(entry.pwcsName+":"+entry.type+":"+Convert.ToHexString(hash));
            }
        }
        finally { ClipboardStorage.Release(enumerator); }
        return SHA256.HashData(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(rows));
    }
    public void Restore()
    {
        ObjectDisposedException.ThrowIf(disposed,this);Marshal.ThrowExceptionForHR(OleSetClipboard(values.Count==0?null:this));
        if(values.Count>0)Marshal.ThrowExceptionForHR(OleFlushClipboard());
        using var restored=Capture();if(!EquivalentTo(restored))throw new IOException("clipboard-restore-mismatch");
    }
    public void Dispose(){if(disposed)return;disposed=true;foreach(var entry in values){var m=entry.Medium;ReleaseStgMedium(ref m);if(entry.Format.ptd!=IntPtr.Zero)Marshal.FreeCoTaskMem(entry.Format.ptd);}values.Clear();}
    public void GetData(ref FORMATETC format,out STGMEDIUM medium)
    {
        ObjectDisposedException.ThrowIf(disposed,this);var requested=format;var matches=values.Where(v=>Matches(v.Format,requested)).ToArray();
        if(matches.Length==0)throw new COMException("format-unavailable",unchecked((int)0x80040064));medium=Duplicate(matches[0].Format,matches[0].Medium,new());
    }
    public int QueryGetData(ref FORMATETC format){var requested=format;return !disposed&&values.Any(v=>Matches(v.Format,requested))?0:unchecked((int)0x80040064);}
    public IEnumFORMATETC EnumFormatEtc(DATADIR direction)=>!disposed&&direction==DATADIR.DATADIR_GET?new Formats(values.Select(v=>v.Format).ToArray()):throw new COMException("direction",unchecked((int)0x80004001));
    public int GetCanonicalFormatEtc(ref FORMATETC input,out FORMATETC output){output=input;output.ptd=IntPtr.Zero;return 0x00040130;}
    public void GetDataHere(ref FORMATETC f,ref STGMEDIUM m)=>throw new COMException("unsupported",unchecked((int)0x80004001));
    public void SetData(ref FORMATETC f,ref STGMEDIUM m,bool release)=>throw new COMException("read-only",unchecked((int)0x80004001));
    public int DAdvise(ref FORMATETC f,ADVF flags,IAdviseSink sink,out int connection){connection=0;return unchecked((int)0x80040003);}
    public void DUnadvise(int connection)=>throw new COMException("unsupported",unchecked((int)0x80040003));
    public int EnumDAdvise(out IEnumSTATDATA? e){e=null;return unchecked((int)0x80040003);}
    [ComVisible(true),ClassInterface(ClassInterfaceType.None)]
    internal sealed class Formats:IEnumFORMATETC,IDisposable
    {
        readonly FORMATETC[] formats;
        int index;
        bool disposed;
        public Formats(FORMATETC[] source)
        {
            formats=new FORMATETC[source.Length];
            try{for(var i=0;i<source.Length;i++)formats[i]=CopyFormat(source[i]);}catch{Dispose();throw;}
        }
        ~Formats(){Dispose();}
        public void Dispose(){if(disposed)return;disposed=true;foreach(var f in formats)if(f.ptd!=IntPtr.Zero)Marshal.FreeCoTaskMem(f.ptd);GC.SuppressFinalize(this);}
        public int Next(int count,FORMATETC[] target,int[] fetched)
        {
            ObjectDisposedException.ThrowIf(disposed,this);
            if(count<0||count>target.Length)return unchecked((int)0x80070057);var n=Math.Min(count,formats.Length-index);var copied=0;
            try{for(;copied<n;copied++)target[copied]=CopyFormat(formats[index+copied]);}
            catch{for(var i=0;i<copied;i++)if(target[i].ptd!=IntPtr.Zero)Marshal.FreeCoTaskMem(target[i].ptd);throw;}
            index+=n;if(fetched.Length>0)fetched[0]=n;return n==count?0:1;
        }
        public int Skip(int count){ObjectDisposedException.ThrowIf(disposed,this);if(count<0)return unchecked((int)0x80070057);var n=Math.Min(count,formats.Length-index);index+=n;return n==count?0:1;}
        public int Reset(){ObjectDisposedException.ThrowIf(disposed,this);index=0;return 0;}
        public void Clone(out IEnumFORMATETC clone){ObjectDisposedException.ThrowIf(disposed,this);clone=new Formats(formats){index=index};}
    }
    [StructLayout(LayoutKind.Sequential)] struct Bitmap{public int type,width,height,widthBytes;public ushort planes,bitsPixel;public IntPtr bits;}
    [DllImport("ole32.dll")] static extern int OleGetClipboard(out ComData data);
    [DllImport("ole32.dll")] static extern int OleSetClipboard(ComData? data);
    [DllImport("ole32.dll")] static extern int OleFlushClipboard();
    [DllImport("ole32.dll")] internal static extern void ReleaseStgMedium(ref STGMEDIUM medium);
    [DllImport("ole32.dll")] static extern IntPtr OleDuplicateData(IntPtr handle,ushort format,uint flags);
    [DllImport("ole32.dll")] internal static extern int CreateStreamOnHGlobal(IntPtr memory,[MarshalAs(UnmanagedType.Bool)] bool delete,out IStream stream);
    [DllImport("kernel32.dll")] static extern UIntPtr GlobalSize(IntPtr handle);
    [DllImport("kernel32.dll")] static extern IntPtr GlobalLock(IntPtr handle);
    [DllImport("kernel32.dll")] static extern bool GlobalUnlock(IntPtr handle);
    [DllImport("gdi32.dll",EntryPoint="GetObjectW")] static extern int GetObject(IntPtr handle,int size,out Bitmap bitmap);
    [DllImport("gdi32.dll")] static extern int GetBitmapBits(IntPtr handle,int count,[Out] byte[] data);
    [DllImport("gdi32.dll")] static extern uint GetEnhMetaFileBits(IntPtr handle,uint count,[Out] byte[]? data);
    [DllImport("gdi32.dll")] static extern uint GetMetaFileBitsEx(IntPtr handle,uint count,[Out] byte[]? data);
}
