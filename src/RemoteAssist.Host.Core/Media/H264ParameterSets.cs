namespace RemoteAssist.Host.Core.Media;

/// <summary>초기 시험 출력에 포함된 SPS/PPS를 보존해 새 디코더가 IDR에서 복구할 수 있게 한다.</summary>
public sealed class H264ParameterSets
{
    private byte[]? _sps, _pps;
    public byte[] Process(ReadOnlySpan<byte> packet)
    {
        var units = FindUnits(packet);
        var hasSps=false;var hasPps=false;var idr=false;
        foreach(var (start,header,end) in units)
        {
            switch(packet[header]&31)
            {
                case 7:_sps=packet[start..end].ToArray();hasSps=true;break;
                case 8:_pps=packet[start..end].ToArray();hasPps=true;break;
                case 5:idr=true;break;
            }
        }
        if (!idr) return packet.ToArray();
        if (_sps is null || _pps is null) throw new InvalidDataException("H.264 IDR에 필요한 SPS/PPS가 없다.");
        var sps=hasSps?ReadOnlySpan<byte>.Empty:_sps.AsSpan();var pps=hasPps?ReadOnlySpan<byte>.Empty:_pps.AsSpan();
        var result=new byte[checked(sps.Length+pps.Length+packet.Length)];
        sps.CopyTo(result);pps.CopyTo(result.AsSpan(sps.Length));packet.CopyTo(result.AsSpan(sps.Length+pps.Length));return result;
    }
    public static bool ContainsIdr(ReadOnlySpan<byte> packet)
    {foreach(var (_,header,_) in FindUnits(packet)) if((packet[header]&31)==5)return true;return false;}
    private static List<(int Start,int Header,int End)> FindUnits(ReadOnlySpan<byte> packet)
    {
        var starts=new List<(int Start,int Header)>();
        for(var i=0;i+3<packet.Length;i++)
        {
            if(packet[i]!=0||packet[i+1]!=0)continue;
            var prefix=packet[i+2]==1?3:i+4<packet.Length&&packet[i+2]==0&&packet[i+3]==1?4:0;
            if(prefix==0)continue;starts.Add((i,i+prefix));i+=prefix-1;
        }
        var length=packet.Length;
        return starts.Select((unit,i)=>(unit.Start,unit.Header,i+1<starts.Count?starts[i+1].Start:length)).ToList();
    }
}
