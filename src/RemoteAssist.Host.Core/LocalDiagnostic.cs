using System.Text.Json;

namespace RemoteAssist.Host.Core;

public static class LocalDiagnostic
{
    public static void Failure(string component,Exception error)
    {
        try
        {
            var root=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"RemoteAssist");Directory.CreateDirectory(root);
            var path=Path.Combine(root,"diagnostic.ndjson");
            if(File.Exists(path)&&new FileInfo(path).Length>2*1024*1024)return;
            File.AppendAllText(path,JsonSerializer.Serialize(new {utc=DateTimeOffset.UtcNow,pid=Environment.ProcessId,component,
                error=error.GetType().Name,hresult=$"0x{error.HResult:X8}",nativeError=(error as System.ComponentModel.Win32Exception)?.NativeErrorCode,stack=error.StackTrace})+Environment.NewLine);
        }
        catch { /* Diagnostics must not reopen a failed session. No exception messages or bodies. */ }
    }
}
