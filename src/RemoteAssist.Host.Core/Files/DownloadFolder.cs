using System.Runtime.InteropServices;

namespace RemoteAssist.Host.Core.Files;

/// <summary>지원자가 보낸 파일을 받는 폴더. 대상 사용자의 실제 다운로드 폴더(폴더 리디렉션 포함) 아래 RemoteAssist다.</summary>
public static class DownloadFolder
{
    public static string Resolve()
    {
        var downloadsId = new Guid("374DE290-123F-4565-9164-39C4925E467B");
        Marshal.ThrowExceptionForHR(SHGetKnownFolderPath(ref downloadsId, 0, IntPtr.Zero, out var path));
        string downloads;
        try { downloads = Marshal.PtrToStringUni(path) ?? throw new IOException("다운로드 폴더 경로 누락이다."); }
        finally { Marshal.FreeCoTaskMem(path); }
        var folder = Path.GetFullPath(Path.Combine(downloads, "RemoteAssist"));
        Directory.CreateDirectory(folder);
        return folder;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern int SHGetKnownFolderPath(ref Guid folder, uint flags, IntPtr token, out IntPtr path);
}
