using System.Runtime.InteropServices;
using System.IO;
using System.Runtime.InteropServices.ComTypes;

namespace RemoteAssist.E2e;

// Vtable/IIDs checked against Windows SDK 10.0.26100.0 um/objidl.h:
// IStorage lines 11345-11445, IEnumSTATSTG lines 11193-11223.
[ComImport, Guid("0000000b-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IStorage
{
    void CreateStream([MarshalAs(UnmanagedType.LPWStr)] string name, uint mode, uint r1, uint r2, out IStream stream);
    void OpenStream([MarshalAs(UnmanagedType.LPWStr)] string name, IntPtr r1, uint mode, uint r2, out IStream stream);
    void CreateStorage([MarshalAs(UnmanagedType.LPWStr)] string name, uint mode, uint r1, uint r2, out IStorage storage);
    void OpenStorage([MarshalAs(UnmanagedType.LPWStr)] string name, IStorage? priority, uint mode, IntPtr exclude, uint reserved, out IStorage storage);
    void CopyTo(uint count, IntPtr iids, IntPtr exclude, IStorage destination);
    void MoveElementTo([MarshalAs(UnmanagedType.LPWStr)] string name, IStorage destination, [MarshalAs(UnmanagedType.LPWStr)] string newName, uint flags);
    void Commit(uint flags);
    void Revert();
    void EnumElements(uint r1, IntPtr r2, uint r3, out IEnumStorage elements);
    void DestroyElement([MarshalAs(UnmanagedType.LPWStr)] string name);
    void RenameElement([MarshalAs(UnmanagedType.LPWStr)] string name, [MarshalAs(UnmanagedType.LPWStr)] string newName);
    void SetElementTimes([MarshalAs(UnmanagedType.LPWStr)] string? name, IntPtr created, IntPtr accessed, IntPtr modified);
    void SetClass(ref Guid clsid);
    void SetStateBits(uint bits, uint mask);
    void Stat(out STATSTG stat, uint flags);
}
[ComImport, Guid("0000000d-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IEnumStorage
{
    [PreserveSig] int Next(uint count, [Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex=0)] STATSTG[] items, out uint fetched);
    [PreserveSig] int Skip(uint count);
    void Reset();
    void Clone(out IEnumStorage clone);
}
[ComImport,Guid("0000000a-0000-0000-C000-000000000046"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ILockBytes
{
    void ReadAt(ulong offset,IntPtr bytes,uint count,IntPtr read);
    void WriteAt(ulong offset,IntPtr bytes,uint count,IntPtr written);
    void Flush();
    void SetSize(ulong size);
    void LockRegion(ulong offset,ulong size,uint type);
    void UnlockRegion(ulong offset,ulong size,uint type);
    void Stat(out STATSTG stat,uint flags);
}

internal static class ClipboardStorage
{
    internal static void Release(object? value) { if(value is ClipboardSnapshot.Formats formats)formats.Dispose();else if(value is not null && Marshal.IsComObject(value)) Marshal.ReleaseComObject(value); }
    internal static IStorage Create()
    {
        Marshal.ThrowExceptionForHR(CreateILockBytesOnHGlobal(IntPtr.Zero, true, out var bytes));
        try { Marshal.ThrowExceptionForHR(StgCreateDocfileOnILockBytes(bytes, 0x1012, 0, out var storage)); return storage; }
        finally { Release(bytes); }
    }
    internal static void Copy(IStorage source, IStorage destination, ClipboardCopyBudget budget, int depth=0)
    {
        budget.CheckTime();
        if(depth>budget.MaxDepth)throw new InvalidDataException("clipboard-storage-depth");
        source.Stat(out var stat,1);
        var clsid=stat.clsid;destination.SetClass(ref clsid);destination.SetStateBits(unchecked((uint)stat.grfStateBits),uint.MaxValue);
        source.EnumElements(0,IntPtr.Zero,0,out var elements);
        try
        {
            var item=new STATSTG[1];
            while(true)
            {
                budget.CheckTime();
                var hr=elements.Next(1,item,out var count);
                if(hr==1&&count==0)break;
                if(hr!=0||count!=1)throw new InvalidDataException("clipboard-storage-enumeration");
                budget.ReserveElement();
                var entry=item[0];
                if(string.IsNullOrEmpty(entry.pwcsName)||entry.pwcsName.Length>255)throw new InvalidDataException("clipboard-storage-name-bound");
                budget.ReserveBytes(checked((ulong)entry.pwcsName.Length*2));
                if(entry.type==2)
                {
                    if(entry.ctime.dwHighDateTime!=0||entry.ctime.dwLowDateTime!=0||entry.atime.dwHighDateTime!=0||entry.atime.dwLowDateTime!=0||
                       entry.mtime.dwHighDateTime!=0||entry.mtime.dwLowDateTime!=0)throw new NotSupportedException("clipboard-storage-stream-times");
                    source.OpenStream(entry.pwcsName,IntPtr.Zero,0x10,0,out var input);
                    try
                    {
                        destination.CreateStream(entry.pwcsName,0x1012,0,0,out var output);
                        try { ClipboardSnapshot.CopyStream(input,output,budget);output.Commit(0); }
                        finally { Release(output); }
                    }
                    finally { Release(input); }
                }
                else if(entry.type==1)
                {
                    source.OpenStorage(entry.pwcsName,null,0x10,IntPtr.Zero,0,out var input);
                    try
                    {
                        destination.CreateStorage(entry.pwcsName,0x1012,0,0,out var output);
                        try { Copy(input,output,budget,depth+1); }
                        finally { Release(output); }
                    }
                    finally { Release(input); }
                }
                else throw new NotSupportedException("clipboard-storage-element");
                // Compound-file IStorage does not expose independent stream
                // timestamps. Preserve substorage times; stream bytes/type are exact.
                if(entry.type==1)SetTimes(destination,entry);
            }
            destination.Commit(0);
            SetTimes(destination,stat);
        }
        finally { Release(elements); }
    }
    static void SetTimes(IStorage target,STATSTG stat)
    {
        var p=Marshal.AllocCoTaskMem(24);
        try
        {
            Marshal.StructureToPtr(stat.ctime,p,false);Marshal.StructureToPtr(stat.atime,p+8,false);Marshal.StructureToPtr(stat.mtime,p+16,false);
            target.SetElementTimes(stat.pwcsName,p,p+8,p+16);
        }
        finally { Marshal.FreeCoTaskMem(p); }
    }
    [DllImport("ole32.dll")] static extern int CreateILockBytesOnHGlobal(IntPtr memory,[MarshalAs(UnmanagedType.Bool)] bool delete,out ILockBytes bytes);
    [DllImport("ole32.dll")] static extern int StgCreateDocfileOnILockBytes(ILockBytes bytes,uint mode,uint reserved,out IStorage storage);
}
