using FFmpeg.AutoGen;
using System.Security.Cryptography;

namespace RemoteAssist.Host.Core.Media;

/// <summary>Single capture-thread owner. Returned pixels remain valid until the next Scale call.</summary>
public sealed unsafe class BgraFrameScaler(int maxLongEdge=1920,int maxShortEdge=1080) : IDisposable
{
    SwsContext* context;
    byte[] source=[],destination=[];
    int sourceWidth,sourceHeight,sourceStride;
    bool disposed;
    public int Width {get;private set;}
    public int Height {get;private set;}
    public void SetLimits(int longEdge,int shortEdge){ObjectDisposedException.ThrowIf(disposed,this);_=Fit(2,2,longEdge,shortEdge);if(longEdge==maxLongEdge&&shortEdge==maxShortEdge)return;Release();maxLongEdge=longEdge;maxShortEdge=shortEdge;}
    public static (int Width,int Height) Fit(int width,int height,int maxLongEdge=1920,int maxShortEdge=1080)
    {
        FrameLayout.ValidateDimensions(width,height);
        if(width<2||height<2||maxShortEdge<2||maxShortEdge>2160||maxLongEdge<maxShortEdge||maxLongEdge>3840)throw new ArgumentOutOfRangeException(nameof(width));
        var factor=Math.Min(1d,Math.Min((double)maxLongEdge/Math.Max(width,height),(double)maxShortEdge/Math.Min(width,height)));
        return (Math.Max(2,(int)(width*factor)&~1),Math.Max(2,(int)(height*factor)&~1));
    }
    public ReadOnlyMemory<byte> Scale(ReadOnlyMemory<byte> pixels,int width,int height,int stride)
    {
        ObjectDisposedException.ThrowIf(disposed,this);
        FrameLayout.Validate(pixels,width,height,stride);
        if(width!=sourceWidth||height!=sourceHeight||stride!=sourceStride)
        {
            Release();
            (Width,Height)=Fit(width,height,maxLongEdge,maxShortEdge);
            FFmpegLoader.Initialise();
            context=ffmpeg.sws_getContext(width,height,AVPixelFormat.AV_PIX_FMT_BGRA,Width,Height,AVPixelFormat.AV_PIX_FMT_BGRA,(int)SwsFlags.SWS_FAST_BILINEAR,null,null,null);
            if(context==null)throw new InvalidOperationException("BGRA 축소 context 생성 실패다.");
            source=new byte[checked(stride*height+64)];destination=new byte[checked(Width*Height*4+64)];
            sourceWidth=width;sourceHeight=height;sourceStride=stride;
        }
        pixels.Span[..checked(stride*height)].CopyTo(source);
        fixed(byte* src=source,dst=destination)
        {
            var rows=ffmpeg.sws_scale(context,new byte*[]{src,null,null,null},[stride,0,0,0],0,height,new byte*[]{dst,null,null,null},[Width*4,0,0,0]);
            if(rows!=Height)throw new InvalidOperationException("BGRA 축소 출력 행이 부족하다.");
        }
        return destination.AsMemory(0,checked(Width*Height*4));
    }
    void Release()
    {
        if(context!=null){ffmpeg.sws_freeContext(context);context=null;}
        CryptographicOperations.ZeroMemory(source);CryptographicOperations.ZeroMemory(destination);
        source=[];destination=[];sourceWidth=sourceHeight=sourceStride=0;
    }
    public void Dispose(){if(disposed)return;disposed=true;Release();}
}
