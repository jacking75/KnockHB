using System.Security.Cryptography;
using RemoteAssist.Host.Core.Security;
using RemoteAssist.Protocol;

namespace RemoteAssist.Host.Core.Files;

public sealed class IncomingFile:IDisposable
{
    readonly FileMessage offer;
    readonly PermissionGate permissions;readonly long generation;
    readonly Stream stream;readonly Action commit,abort;
    readonly object gate=new();
    readonly IncrementalHash hash;
    long received;uint sequence;bool completed,disposed;
    public IncomingFile(FileMessage offer,PermissionGate permissions,long generation,Stream stream,Action commit,Action abort)
    {
        _=FileMessage.Parse(ProtocolJson.Encode(offer));if(offer.T!="file-offer")throw new InvalidDataException("수신 제안이 아니다.");
        if(!permissions.Allows(Capability.File,generation)||offer.PermissionGeneration!=generation||permissions.Current?.SessionId!=offer.SessionId)throw new UnauthorizedAccessException("파일 수신 승인이 없다.");
        ArgumentNullException.ThrowIfNull(stream);ArgumentNullException.ThrowIfNull(commit);ArgumentNullException.ThrowIfNull(abort);
        this.offer=offer;this.permissions=permissions;this.generation=generation;this.stream=stream;this.commit=commit;this.abort=abort;hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    }
    public void Chunk(ReadOnlySpan<byte> packet)
    {
        lock(gate)try
        {
        ObjectDisposedException.ThrowIf(disposed,this);if(completed)throw new InvalidDataException("확정 후 청크를 거부한다.");
        var header=FileChunkHeader.Decode(packet);if(header.TransferId!=Guid.ParseExact(offer.Id,"N")||header.Sequence!=sequence||header.Length<1||received+header.Length>offer.Size)throw new InvalidDataException("파일 청크 순서·ID·크기 오류다.");
        var payload=packet[FileChunkHeader.Size..].ToArray();
        if(!permissions.Execute(Capability.File,generation,()=>stream.Write(payload)))throw new UnauthorizedAccessException("파일 수신 승인이 철회됐다.");
        hash.AppendData(payload);received+=header.Length;sequence++;
        }
        catch{Dispose();throw;}
    }
    public void Complete(FileMessage message)
    {
        lock(gate)try
        {
        ObjectDisposedException.ThrowIf(disposed,this);_=FileMessage.Parse(ProtocolJson.Encode(message));
        if(completed||message.T!="file-complete"||message.Id!=offer.Id||message.SessionId!=offer.SessionId||message.Generation!=offer.Generation||message.PermissionGeneration!=generation||message.Size!=offer.Size||message.Hash!=offer.Hash||received!=offer.Size||Convert.ToHexStringLower(hash.GetHashAndReset())!=offer.Hash)throw new InvalidDataException("파일 완료 크기·hash 오류다.");
        if(!permissions.Execute(Capability.File,generation,()=>{stream.Flush();commit();completed=true;}))throw new UnauthorizedAccessException("파일 확정 승인이 철회됐다.");
        }
        catch{Dispose();throw;}
    }
    public void Dispose(){lock(gate){if(disposed)return;disposed=true;try{abort();}finally{hash.Dispose();}}} // Closes delete-on-close candidate; committed hard link survives.
}
