using System.Buffers.Binary;

namespace RemoteAssist.Protocol;

public readonly record struct FileChunkHeader(Guid TransferId,uint Sequence,int Length)
{
    public const int Size=32;
    public const int MaxPayload=16384;
    public byte[] Encode(ReadOnlySpan<byte> payload)
    {
        if(TransferId==Guid.Empty||Length!=payload.Length||Length<0||Length>MaxPayload)throw new InvalidDataException("파일 청크 크기/ID 오류다.");
        var packet=new byte[Size+Length];"RAFT"u8.CopyTo(packet);BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(4),1);
        TransferId.TryWriteBytes(packet.AsSpan(8,16),bigEndian:true,out _);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(24),Sequence);BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(28),(uint)Length);
        payload.CopyTo(packet.AsSpan(Size));return packet;
    }
    public static FileChunkHeader Decode(ReadOnlySpan<byte> packet)
    {
        if(packet.Length<Size||!packet[..4].SequenceEqual("RAFT"u8)||BinaryPrimitives.ReadUInt16LittleEndian(packet[4..])!=1||BinaryPrimitives.ReadUInt16LittleEndian(packet[6..])!=0)
            throw new InvalidDataException("파일 청크 header 오류다.");
        var length=BinaryPrimitives.ReadUInt32LittleEndian(packet[28..]);var id=new Guid(packet.Slice(8,16),bigEndian:true);
        if(length>MaxPayload||packet.Length!=Size+length||id==Guid.Empty)throw new InvalidDataException("파일 청크 길이/ID 오류다.");
        return new(id,BinaryPrimitives.ReadUInt32LittleEndian(packet[24..]),(int)length);
    }
}
