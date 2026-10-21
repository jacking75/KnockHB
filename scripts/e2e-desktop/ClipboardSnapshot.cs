using System.Runtime.InteropServices;
using System.IO;
using System.Runtime.InteropServices.ComTypes;
using ComData = System.Runtime.InteropServices.ComTypes.IDataObject;

namespace RemoteAssist.E2e;

// Private STA memory only. A format which cannot be duplicated is a PRECONDITION
// failure before the product is allowed to change the clipboard. Never serialize
// clipboard contents, format names, lengths or hashes to an evidence file.
[ComVisible(true)]
[ClassInterface(ClassInterfaceType.None)]
sealed class ClipboardSnapshot : ComData, IDisposable
{
    readonly List<(FORMATETC Format, STGMEDIUM Medium)> values = [];
    bool disposed;
    public static ClipboardSnapshot Capture()
    {
        Marshal.ThrowExceptionForHR(OleGetClipboard(out var source));
        var result = new ClipboardSnapshot();
        ulong totalBytes = 0;
        try
        {
            var enumerator = source.EnumFormatEtc(DATADIR.DATADIR_GET);
            try
            {
                var item = new FORMATETC[1]; var count = new int[1];
                while (true)
                {
                    var status=enumerator.Next(1,item,count);
                    if(status==1&&count[0]==0)break;
                    Marshal.ThrowExceptionForHR(status);
                    if(status!=0)throw new InvalidDataException("clipboard-enumeration-incomplete");
                    if (count[0] != 1 || result.values.Count >= 256) throw new InvalidDataException("clipboard-format-bound");
                    var format = item[0];
                    // Target-device dependent formats cannot be safely replayed.
                    if (format.ptd != IntPtr.Zero)
                    {
                        Marshal.FreeCoTaskMem(format.ptd);
                        throw new NotSupportedException("clipboard-target-device");
                    }
                    source.GetData(ref format, out var original);
                    try
                    {
                        if (original.tymed is not (TYMED.TYMED_HGLOBAL or TYMED.TYMED_GDI or TYMED.TYMED_ENHMF or TYMED.TYMED_MFPICT))
                            throw new NotSupportedException("clipboard-medium-not-restorable");
                        if (original.tymed == TYMED.TYMED_HGLOBAL)
                        {
                            totalBytes = checked(totalBytes + GlobalSize(original.unionmember).ToUInt64());
                            if (totalBytes > 64 * 1024 * 1024) throw new InvalidDataException("clipboard-private-memory-bound");
                        }
                        format.tymed = original.tymed;
                        var copy = Duplicate(format, original);
                        result.values.Add((format, copy));
                    }
                    finally { ReleaseStgMedium(ref original); }
                }
            }
            finally { if (Marshal.IsComObject(enumerator)) Marshal.ReleaseComObject(enumerator); }
            return result;
        }
        catch { result.Dispose(); throw; }
        finally { if (Marshal.IsComObject(source)) Marshal.ReleaseComObject(source); }
    }
    static STGMEDIUM Duplicate(FORMATETC format, STGMEDIUM medium)
    {
        var handle = OleDuplicateData(medium.unionmember, unchecked((ushort)format.cfFormat), 0);
        if (handle == IntPtr.Zero) throw new InvalidOperationException("clipboard-duplicate-failed");
        return new STGMEDIUM { tymed = medium.tymed, unionmember = handle, pUnkForRelease = null };
    }
    public void Restore()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        Marshal.ThrowExceptionForHR(OleSetClipboard(this));
        // Materialize every retained format so restoration survives helper exit.
        Marshal.ThrowExceptionForHR(OleFlushClipboard());
    }
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        foreach (var entry in values) { var medium = entry.Medium; ReleaseStgMedium(ref medium); }
        values.Clear();
    }
    public void GetData(ref FORMATETC format, out STGMEDIUM medium)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var requested = format;
        var matches = values.Where(v => v.Format.cfFormat == requested.cfFormat &&
            v.Format.dwAspect == requested.dwAspect && v.Format.lindex == requested.lindex &&
            (v.Format.tymed & requested.tymed) != 0).ToArray();
        if (matches.Length == 0) throw new COMException("format-unavailable", unchecked((int)0x80040064));
        medium = Duplicate(matches[0].Format, matches[0].Medium);
    }
    public int QueryGetData(ref FORMATETC format)
    {
        var requested = format;
        return !disposed && values.Any(v => v.Format.cfFormat == requested.cfFormat && v.Format.dwAspect == requested.dwAspect &&
            v.Format.lindex == requested.lindex && (v.Format.tymed & requested.tymed) != 0) ? 0 : unchecked((int)0x80040064);
    }
    public IEnumFORMATETC EnumFormatEtc(DATADIR direction) => direction == DATADIR.DATADIR_GET
        ? new Formats(values.Select(v => v.Format).ToArray()) : throw new COMException("direction", unchecked((int)0x80004001));
    public int GetCanonicalFormatEtc(ref FORMATETC input, out FORMATETC output) { output = input; output.ptd = IntPtr.Zero; return 0x00040130; }
    public void GetDataHere(ref FORMATETC format, ref STGMEDIUM medium) => throw new COMException("unsupported", unchecked((int)0x80004001));
    public void SetData(ref FORMATETC format, ref STGMEDIUM medium, bool release) => throw new COMException("read-only", unchecked((int)0x80004001));
    public int DAdvise(ref FORMATETC format, ADVF flags, IAdviseSink sink, out int connection) { connection = 0; return unchecked((int)0x80040003); }
    public void DUnadvise(int connection) => throw new COMException("unsupported", unchecked((int)0x80040003));
    public int EnumDAdvise(out IEnumSTATDATA? enumerator) { enumerator = null; return unchecked((int)0x80040003); }
    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    sealed class Formats(FORMATETC[] formats) : IEnumFORMATETC
    {
        int index;
        public int Next(int count, FORMATETC[] target, int[] fetched)
        {
            if (count < 0 || count > target.Length) return unchecked((int)0x80070057);
            var n = Math.Min(count, formats.Length - index);
            Array.Copy(formats, index, target, 0, n); index += n;
            if (fetched.Length > 0) fetched[0] = n;
            return n == count ? 0 : 1;
        }
        public int Skip(int count) { if (count < 0) return unchecked((int)0x80070057); var old = index; index = Math.Min(formats.Length, index + count); return index - old == count ? 0 : 1; }
        public int Reset() { index = 0; return 0; }
        public void Clone(out IEnumFORMATETC clone) { clone = new Formats(formats) { index = index }; }
    }
    [DllImport("ole32.dll")] static extern int OleGetClipboard(out ComData data);
    [DllImport("ole32.dll")] static extern int OleSetClipboard(ComData data);
    [DllImport("ole32.dll")] static extern int OleFlushClipboard();
    [DllImport("ole32.dll")] static extern void ReleaseStgMedium(ref STGMEDIUM medium);
    [DllImport("ole32.dll")] static extern IntPtr OleDuplicateData(IntPtr handle, ushort format, uint flags);
    [DllImport("kernel32.dll")] static extern UIntPtr GlobalSize(IntPtr handle);
}
