namespace RemoteAssist.Host.Core.Media;

public sealed record EncoderProbeResult(int Emitted,int Decoded,double Throughput,bool RecoveryDecoded)
{
    public bool Passed => Emitted>0 && Decoded==Emitted && double.IsFinite(Throughput) && Throughput>=30 && RecoveryDecoded;
}
