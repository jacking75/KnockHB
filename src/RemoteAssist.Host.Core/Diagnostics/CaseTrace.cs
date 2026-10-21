using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace RemoteAssist.Host.Core.Diagnostics;

/// <summary>Metadata-only observation for an explicitly owned development installation.
/// Never grants permissions or changes the result of a product operation.</summary>
public static class CaseTrace
{
    static readonly object Gate = new();
    static readonly Lazy<(string Path,string Id)?> Destination = new(Resolve);
    static long sequence;
    static (string,string)? Resolve()
    {
        try
        {
            var installation=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),"RemoteAssist-Dev");
            if(!string.Equals(Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory),installation,StringComparison.OrdinalIgnoreCase))return null;
            var marker=Path.Combine(installation,"dev-owner.json");
            if(!File.Exists(marker)||new FileInfo(marker).Length>8192)return null;
            using var document=JsonDocument.Parse(File.ReadAllBytes(marker));
            var owner=document.RootElement;var id=owner.GetProperty("id").GetString();
            if(id is null||!Regex.IsMatch(id,"^[a-f0-9]{32}$")||owner.GetProperty("root").GetString()!=installation)return null;
            var directory=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),"RemoteAssist-E2E-Product-"+id,"trace");
            if(!Directory.Exists(directory))return null;
            for(var p=new DirectoryInfo(directory);p is not null;p=p.Parent)
                if((p.Attributes&FileAttributes.ReparsePoint)!=0)return null;
            using var process=Process.GetCurrentProcess();
            return (Path.Combine(directory,$"trace-{process.Id}-{process.StartTime.ToUniversalTime().Ticks}.ndjson"),id);
        }
        catch{return null;}
    }
    public static void Write(string kind,string? sessionId=null,long generation=0,object? metadata=null)
    {
        var destination=Destination.Value;if(destination is null)return;
        try
        {
            lock(Gate)
            {
                var (path,id)=destination.Value;
                if(sequence>=16384)return;
                var bytes=JsonSerializer.SerializeToUtf8Bytes(new {runId=id,sequence=++sequence,
                    pid=Environment.ProcessId,tick=Stopwatch.GetTimestamp(),frequency=Stopwatch.Frequency,
                    kind,sessionId,generation,metadata});
                if(bytes.Length>8192)return;
                using var stream=new FileStream(path,FileMode.Append,FileAccess.Write,FileShare.ReadWrite|FileShare.Delete);
                if(stream.Length>1048576)return;
                stream.Write(bytes);stream.WriteByte(10);stream.Flush(true);
            }
        }
        catch { /* Missing sequence/completion evidence causes test failure. */ }
    }
}
