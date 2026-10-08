using System.Text;
using RemoteAssist.Host.Core.Files;
using RemoteAssist.Protocol;
using SIPSorcery.Net;

namespace RemoteAssist.Host.Core.Media;

internal sealed class FileDataTransport(RTCDataChannel channel):IFileTransferTransport
{
    public bool IsOpen=>channel.readyState==RTCDataChannelState.open;
    public ulong BufferedAmount=>channel.bufferedAmount;
    public void Send(FileMessage message)=>channel.send(Encoding.UTF8.GetString(ProtocolJson.Encode(message)));
    public void Send(byte[] packet)=>channel.send(packet);
    public void Attach(FileTransferSession session,Action<string> fail)
    {
        channel.onclose+=()=>fail("파일 채널이 종료됐다.");
        channel.onerror+=_=>fail("파일 채널 전송 실패다.");
        channel.onmessage+=(_,kind,bytes)=>
        {
            try
            {
                if(kind==DataChannelPayloadProtocols.WebRTC_String)session.Receive(FileMessage.Parse(bytes));
                else if(kind==DataChannelPayloadProtocols.WebRTC_Binary)session.Receive(bytes);
                else throw new InvalidDataException("파일 채널 타입 오류다.");
            }
            catch{fail("파일 채널 계약 오류다.");}
        };
    }
}
