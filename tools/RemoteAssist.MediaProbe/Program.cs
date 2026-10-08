using System.Diagnostics;
using System.Buffers.Binary;
using System.Reflection;
using System.Text.Json;
using RemoteAssist.Host.Core.Media;
using RemoteAssist.Host.Core.Signaling;
using SIPSorcery.Net;
using SIPSorcery.Sys;
using SIPSorceryMedia.Abstractions;
using SIPSorceryMedia.FFmpeg;

// Synthetic pixels, actual ICE/DTLS-SRTP and SRTCP. No pairing or desktop capture.
try
{
    var encoderName=args.Length==0?"h264_nvenc":args.Length==2&&args[0]=="--encoder"?args[1]:throw new ArgumentException("--encoder 허용 후보만 지정한다.");
    if(!FFmpegH264Encoder.AllowedEncoders.Contains(encoderName))throw new ArgumentException("허용하지 않는 인코더다.");
    var ready=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var pli=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var fir=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var bound=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var binding=new RtcpFeedbackBinding();var requests=new KeyframeRequests();requests.Consume();
    using var sender=new RTCPeerConnection(new RTCConfiguration(),0,new PortRange(27002,27049));
    using var receiver=new RTCPeerConnection(new RTCConfiguration(),0,new PortRange(27050,27099));
    var format=new VideoFormat(VideoCodecsEnum.H264,102,90000,"packetization-mode=1");
    sender.addTrack(new MediaStreamTrack(format,MediaStreamStatusEnum.SendOnly));
    receiver.addTrack(new MediaStreamTrack(format,MediaStreamStatusEnum.RecvOnly));
    sender.onicecandidate+=candidate=> {if(candidate!=null)receiver.addIceCandidate(new RTCIceCandidateInit {candidate=IceCandidateWire.FromNative(candidate.candidate),sdpMid=candidate.sdpMid,sdpMLineIndex=candidate.sdpMLineIndex});};
    receiver.onicecandidate+=candidate=> {if(candidate!=null)sender.addIceCandidate(new RTCIceCandidateInit {candidate=IceCandidateWire.FromNative(candidate.candidate),sdpMid=candidate.sdpMid,sdpMLineIndex=candidate.sdpMLineIndex});};
    sender.onconnectionstatechange+=state=> {if(state==RTCPeerConnectionState.connected)ready.TrySetResult();};
    var received=0;var timestamps=new List<uint>();
    receiver.OnVideoFrameReceived+=(_,timestamp,bytes,_)=> {Interlocked.Increment(ref received);lock(timestamps)timestamps.Add(timestamp);};
    sender.OnReceiveReport+=(_,media,report)=>
    {
        if(binding.Observe(media,report,sender.VideoLocalTrack,sender.VideoRemoteTrack))bound.TrySetResult();
        requests.Observe(media,report);
        if(report.Feedback?.Header.PayloadFeedbackMessageType==PSFBFeedbackTypesEnum.PLI)pli.TrySetResult();
        if(report.Feedback?.Header.PayloadFeedbackMessageType==PSFBFeedbackTypesEnum.FIR)fir.TrySetResult();
    };
    var offer=sender.createOffer(null);await sender.setLocalDescription(offer);
    if(receiver.setRemoteDescription(offer)!=SetDescriptionResultEnum.OK)throw new Exception("offer 거부");
    var answer=receiver.createAnswer(null);await receiver.setLocalDescription(answer);
    if(sender.setRemoteDescription(answer)!=SetDescriptionResultEnum.OK)throw new Exception("answer 거부");
    await ready.Task.WaitAsync(TimeSpan.FromSeconds(15));
    using var encoder=new FFmpegH264Encoder(encoderName);encoder.Configure(1920,1080,30,4000);
    var frame=new byte[1920*1080*4];
    for(var i=0;i<10;i++)
    {
        frame[i*4]=(byte)i;var encoded=encoder.Encode(frame,i==0);if(!encoded.IsEmpty)sender.SendVideo(3000,encoded.ToArray());
        await Task.Delay(35);
    }
    // Deliberately use an SSRC absent from SDP, like a recvonly browser's RTCP-only source.
    var receiverSsrc=receiver.VideoLocalTrack.Ssrc^0xA5A5A5A5;var mediaSsrc=sender.VideoLocalTrack.Ssrc;
    receiver.SendRtcpFeedback(SDPMediaTypesEnum.video,new RTCPFeedback(receiverSsrc,mediaSsrc,PSFBFeedbackTypesEnum.PLI));
    var direct=false;
    try{await pli.Task.WaitAsync(TimeSpan.FromMilliseconds(500));direct=true;}catch(TimeoutException){}
    // A receiver report authenticated by DTLS-SRTCP contains the known media SSRC.
    var reports=new List<ReceptionReportSample> {new(mediaSsrc,0,0,0,0,0,0)};
    receiver.SendRtcpReport(SDPMediaTypesEnum.video,new RTCPCompoundPacket(new RTCPReceiverReport(receiverSsrc,reports),new RTCPSDesReport(receiverSsrc,"probe")));
    await bound.Task.WaitAsync(TimeSpan.FromSeconds(3));
    receiver.SendRtcpFeedback(SDPMediaTypesEnum.video,new RTCPFeedback(receiverSsrc,mediaSsrc,PSFBFeedbackTypesEnum.PLI));
    await pli.Task.WaitAsync(TimeSpan.FromSeconds(3));
    var pliRequested=requests.Consume();
    using var freshPli=new FFmpegVideoEncoder();var pliFrame=encoder.Encode(frame,pliRequested);
    var pliRecovery=freshPli.DecodeVideo(pliFrame.ToArray(),VideoPixelFormatsEnum.Bgra,VideoCodecsEnum.H264).Any();
    // RFC 5104 FIR has an 8-byte FCI. Send raw report bytes through the native SRTCP protection path.
    var rawFir=new byte[20];rawFir[0]=0x84;rawFir[1]=206;BinaryPrimitives.WriteUInt16BigEndian(rawFir.AsSpan(2),4);
    BinaryPrimitives.WriteUInt32BigEndian(rawFir.AsSpan(4),receiverSsrc);BinaryPrimitives.WriteUInt32BigEndian(rawFir.AsSpan(12),mediaSsrc);rawFir[16]=1;
    // Test harness only: the pinned SDK public FIR serializer omits FCI. Its private byte overload still protects SRTCP.
    var nativeSend=typeof(MediaStream).GetMethod("SendRtcpReport",BindingFlags.Instance|BindingFlags.NonPublic,null,[typeof(byte[])],null)??throw new MissingMethodException("고정 SDK raw RTCP 시험 경로 누락");
    nativeSend.Invoke(receiver.VideoStream,[rawFir]);await fir.Task.WaitAsync(TimeSpan.FromSeconds(3));
    var firRequested=requests.Consume();using var freshFir=new FFmpegVideoEncoder();var firFrame=encoder.Encode(frame,firRequested);
    var firRecovery=freshFir.DecodeVideo(firFrame.ToArray(),VideoPixelFormatsEnum.Bgra,VideoCodecsEnum.H264).Any();
    Console.WriteLine(JsonSerializer.Serialize(new {kind="native-media",encoder=encoderName,connected=true,received,firstTimestamp=timestamps.FirstOrDefault(),lastTimestamp=timestamps.LastOrDefault(),directPli=direct,bound=bound.Task.IsCompletedSuccessfully,pliRequested,pliRecovery,firRequested,firRecovery}));
    sender.Close("probe finished");receiver.Close("probe finished");
    return received==10&&timestamps.SequenceEqual(Enumerable.Range(0,10).Select(i=>(uint)i*3000))&&pliRequested&&pliRecovery&&firRequested&&firRecovery?0:1;
}
catch(Exception ex){Console.Error.WriteLine(ex);return 1;}
