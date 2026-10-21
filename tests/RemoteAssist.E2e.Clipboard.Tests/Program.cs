using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using RemoteAssist.E2e;
using ComData=System.Runtime.InteropServices.ComTypes.IDataObject;

internal static class Program
{
    [STAThread] static int Main()
    {
        var tests=new (string,Action)[]{("empty",Empty),("stream independent and two GetData",StreamCopy),("nested storage",Storage),
            ("target ownership and matching",Target),("budgets",Budget),("partial failure",Partial),("unsupported before mutation",Unsupported),("enumeration failure",Enumeration),
            ("HGLOBAL independent",Global),("bitmap independent",Bitmap),("enhanced metafile independent",Enhanced),("metafile picture independent",Metafile),
            ("medium aspect index matching",Matching),("OCR scene separation",Ocr)};
        foreach(var (name,run) in tests){try{run();Console.WriteLine("PASS "+name);}catch(Exception ex){Console.WriteLine("FAIL "+name+" "+ex.ToString());return 1;}}return 0;
    }
    static void Check(bool value){if(!value)throw new Exception("contract");}
    static void Reject(Action action){try{action();}catch(Exception ex)when(ex is InvalidDataException or NotSupportedException or TimeoutException or COMException or OverflowException){return;}throw new Exception("accepted");}
    static FORMATETC Format(TYMED kind)=>new(){cfFormat=1000,dwAspect=DVASPECT.DVASPECT_CONTENT,lindex=-1,tymed=kind};
    static IStream Stream(byte[] data){Marshal.ThrowExceptionForHR(ClipboardSnapshot.CreateStreamOnHGlobal(IntPtr.Zero,true,out var s));s.Write(data,data.Length,IntPtr.Zero);return s;}
    static void Empty(){using var snap=ClipboardSnapshot.CaptureFrom(new Source([],_=>throw new Exception()),new());Check(snap.Count==0);}
    static void StreamCopy()
    {
        var stream=Stream([0,1,255,65]);stream.Seek(2,0,IntPtr.Zero);var format=Format(TYMED.TYMED_ISTREAM);
        var source=new Source([format],_=>ClipboardSnapshot.InterfaceMedium(stream,TYMED.TYMED_ISTREAM,typeof(IStream)));
        using var snap=ClipboardSnapshot.CaptureFrom(source,new());
        var p=Marshal.AllocCoTaskMem(8);
        try{stream.Seek(0,1,p);Check(Marshal.ReadInt64(p)==2);}finally{Marshal.FreeCoTaskMem(p);}
        stream.Seek(0,0,IntPtr.Zero);stream.Write([9,9,9,9],4,IntPtr.Zero);ClipboardStorage.Release(stream);
        snap.GetData(ref format,out var first);snap.GetData(ref format,out var second);Check(first.unionmember!=second.unionmember);
        ClipboardSnapshot.ReleaseStgMedium(ref first);
        try{var read=(IStream)Marshal.GetObjectForIUnknown(second.unionmember);try{var bytes=new byte[4];read.Read(bytes,4,IntPtr.Zero);Check(bytes.SequenceEqual(new byte[]{0,1,255,65}));}finally{ClipboardStorage.Release(read);}}
        finally{ClipboardSnapshot.ReleaseStgMedium(ref second);}
        using var again=ClipboardSnapshot.CaptureFrom(snap,new());Check(snap.EquivalentTo(again));
    }
    static void Storage()
    {
        var storage=ClipboardStorage.Create();storage.CreateStream("A",0x1012,0,0,out var a);a.Write([1,2],2,IntPtr.Zero);ClipboardStorage.Release(a);
        storage.CreateStorage("child",0x1012,0,0,out var child);child.CreateStream("B",0x1012,0,0,out var b);b.Write([3,4,5],3,IntPtr.Zero);ClipboardStorage.Release(b);child.Commit(0);ClipboardStorage.Release(child);storage.Commit(0);
        var f=Format(TYMED.TYMED_ISTORAGE);using var snap=ClipboardSnapshot.CaptureFrom(new Source([f],_=>ClipboardSnapshot.InterfaceMedium(storage,TYMED.TYMED_ISTORAGE,typeof(IStorage))),new());
        storage.DestroyElement("child");ClipboardStorage.Release(storage);
        using var second=ClipboardSnapshot.CaptureFrom(snap,new());Check(snap.EquivalentTo(second));
    }
    static void Target()
    {
        var f=Format(TYMED.TYMED_ISTREAM);f.ptd=Marshal.AllocCoTaskMem(16);Marshal.Copy(new byte[16],0,f.ptd,16);Marshal.WriteInt32(f.ptd,16);
        var s=Stream([7]);
        try
        {
            using var snap=ClipboardSnapshot.CaptureFrom(new Source([f],_=>ClipboardSnapshot.InterfaceMedium(s,TYMED.TYMED_ISTREAM,typeof(IStream))),new());
            var e=snap.EnumFormatEtc(DATADIR.DATADIR_GET);e.Clone(out var clone);var a=new FORMATETC[1];var n=new int[1];var b=new FORMATETC[1];e.Next(1,a,n);clone.Next(1,b,n);
            Check(a[0].ptd!=b[0].ptd&&a[0].ptd!=f.ptd);Marshal.FreeCoTaskMem(a[0].ptd);Marshal.FreeCoTaskMem(b[0].ptd);
            Check(snap.QueryGetData(ref f)==0);var wrong=f;wrong.ptd=IntPtr.Zero;Check(snap.QueryGetData(ref wrong)!=0);
            wrong=f;wrong.lindex=1;Check(snap.QueryGetData(ref wrong)!=0);
        }
        finally{Marshal.FreeCoTaskMem(f.ptd);ClipboardStorage.Release(s);}
    }
    static void Budget()
    {
        var b=new ClipboardCopyBudget(4);b.ReserveBytes(4);Reject(()=>b.ReserveBytes(1));
        var overflow=new ClipboardCopyBudget(ulong.MaxValue);overflow.ReserveBytes(ulong.MaxValue);Reject(()=>overflow.ReserveBytes(1));
        Reject(()=>new ClipboardCopyBudget(clock:()=>10,deadline:10).CheckTime());
        var s=Stream([1,2]);try{Reject(()=>ClipboardSnapshot.CaptureFrom(new Source([Format(TYMED.TYMED_ISTREAM)],_=>ClipboardSnapshot.InterfaceMedium(s,TYMED.TYMED_ISTREAM,typeof(IStream))),new(1)));}finally{ClipboardStorage.Release(s);}
    }
    static void Partial()
    {
        var s=Stream([1]);var count=0;
        try{Reject(()=>ClipboardSnapshot.CaptureFrom(new Source([Format(TYMED.TYMED_ISTREAM),Format(TYMED.TYMED_ISTREAM)],_=>++count==2?throw new COMException():ClipboardSnapshot.InterfaceMedium(s,TYMED.TYMED_ISTREAM,typeof(IStream))),new()));Check(count==2);}
        finally{ClipboardStorage.Release(s);}
    }
    static void Unsupported()
    {
        var calls=0;
        Reject(()=>ClipboardSnapshot.CaptureFrom(new Source([Format(TYMED.TYMED_FILE)],_=>{calls++;throw new Exception("file must not materialize");}),new()));Check(calls==0);
    }
    static void Enumeration(){Reject(()=>ClipboardSnapshot.CaptureFrom(new Source([], _=>throw new Exception(),true),new()));}
    static void Ocr(){Check(OcrPolicy.Security("잠금 로그아웃 작업 관리자")&&!OcrPolicy.Security("User Account Control Lock Sign out")&&OcrPolicy.Uac("사용자 계정 컨트롤")&&OcrPolicy.Cancel("아니요")&&!OcrPolicy.Cancel("Yes"));}
    static void Roundtrip(short formatId,TYMED kind,Func<IntPtr> handle)
    {
        var f=Format(kind);f.cfFormat=formatId;
        using var a=ClipboardSnapshot.CaptureFrom(new Source([f],_=>new(){tymed=kind,unionmember=handle()}),new());
        using var b=ClipboardSnapshot.CaptureFrom(a,new());Check(a.EquivalentTo(b));
        a.GetData(ref f,out var one);a.GetData(ref f,out var two);Check(one.unionmember!=two.unionmember);
        ClipboardSnapshot.ReleaseStgMedium(ref one);ClipboardSnapshot.ReleaseStgMedium(ref two);
    }
    static IntPtr Memory(int count){var h=GlobalAlloc(0x42,(nuint)count);if(h==IntPtr.Zero)throw new Exception();return h;}
    static void Global()=>Roundtrip(1000,TYMED.TYMED_HGLOBAL,()=>
    {var h=Memory(4);var p=GlobalLock(h);try{Marshal.Copy(new byte[]{1,2,3,4},0,p,4);}finally{GlobalUnlock(h);}return h;});
    static void Bitmap()=>Roundtrip(2,TYMED.TYMED_GDI,()=>CreateBitmap(2,2,1,32,new byte[16]));
    static void Enhanced()=>Roundtrip(14,TYMED.TYMED_ENHMF,()=>CloseEnhMetaFile(CreateEnhMetaFile(IntPtr.Zero,null,IntPtr.Zero,null)));
    static void Metafile()=>Roundtrip(3,TYMED.TYMED_MFPICT,()=>
    {var mf=CloseMetaFile(CreateMetaFile(null));var h=Memory(IntPtr.Size==8?24:16);var p=GlobalLock(h);try{Marshal.WriteInt32(p,8);Marshal.WriteInt32(p,4,100);Marshal.WriteInt32(p,8,100);Marshal.WriteIntPtr(p,IntPtr.Size==8?16:12,mf);}finally{GlobalUnlock(h);}return h;});
    static void Matching()
    {
        var f=Format(TYMED.TYMED_ISTREAM);var other=f;other.lindex=2;var aspect=f;aspect.dwAspect=DVASPECT.DVASPECT_ICON;
        var stream=Stream([7]);
        try
        {
            using var snap=ClipboardSnapshot.CaptureFrom(new Source([f,other,aspect],_=>ClipboardSnapshot.InterfaceMedium(stream,TYMED.TYMED_ISTREAM,typeof(IStream))),new());
            Check(snap.Count==3&&snap.QueryGetData(ref other)==0);other.tymed=TYMED.TYMED_HGLOBAL;Check(snap.QueryGetData(ref other)!=0);
        }
        finally{ClipboardStorage.Release(stream);}
    }
    [DllImport("kernel32.dll")] static extern IntPtr GlobalAlloc(uint flags,nuint bytes);
    [DllImport("kernel32.dll")] static extern IntPtr GlobalLock(IntPtr handle);
    [DllImport("kernel32.dll")] static extern bool GlobalUnlock(IntPtr handle);
    [DllImport("gdi32.dll")] static extern IntPtr CreateBitmap(int width,int height,uint planes,uint bits,byte[] bytes);
    [DllImport("gdi32.dll",CharSet=CharSet.Unicode,EntryPoint="CreateEnhMetaFileW")] static extern IntPtr CreateEnhMetaFile(IntPtr dc,string? file,IntPtr bounds,string? description);
    [DllImport("gdi32.dll")] static extern IntPtr CloseEnhMetaFile(IntPtr dc);
    [DllImport("gdi32.dll",CharSet=CharSet.Unicode,EntryPoint="CreateMetaFileW")] static extern IntPtr CreateMetaFile(string? file);
    [DllImport("gdi32.dll")] static extern IntPtr CloseMetaFile(IntPtr dc);
    sealed class Source(FORMATETC[] formats,Func<FORMATETC,STGMEDIUM> data,bool bad=false):ComData
    {
        public void GetData(ref FORMATETC f,out STGMEDIUM m)=>m=data(f);
        public IEnumFORMATETC EnumFormatEtc(DATADIR d)=>bad?new BadEnum():new ClipboardSnapshot.Formats(formats);
        public int QueryGetData(ref FORMATETC f)=>0;
        public void GetDataHere(ref FORMATETC f,ref STGMEDIUM m)=>throw new NotSupportedException();
        public int GetCanonicalFormatEtc(ref FORMATETC f,out FORMATETC o){o=default;return 1;}
        public void SetData(ref FORMATETC f,ref STGMEDIUM m,bool r)=>throw new Exception("OS mutation forbidden");
        public int DAdvise(ref FORMATETC f,ADVF a,IAdviseSink s,out int c){c=0;return 1;}
        public void DUnadvise(int c)=>throw new NotSupportedException();
        public int EnumDAdvise(out IEnumSTATDATA? e){e=null;return 1;}
    }
    sealed class BadEnum:IEnumFORMATETC
    {
        public int Next(int c,FORMATETC[] f,int[] n){n[0]=0;return 0;}
        public int Skip(int c)=>0;public int Reset()=>0;public void Clone(out IEnumFORMATETC e)=>e=new BadEnum();
    }
}
