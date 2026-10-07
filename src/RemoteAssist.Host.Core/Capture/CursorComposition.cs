namespace RemoteAssist.Host.Core.Capture;

public static class CursorComposition
{
    // DXGI pointer position is the top-left of the shape, independent of hotspot.
    public static void Draw(Span<byte> frame,int width,int height,ReadOnlySpan<byte> shape,
        int shapeWidth,int shapeHeight,int pitch,int kind,int left,int top)
    {
        if(width<1||height<1||frame.Length!=checked(width*height*4)||shapeWidth<1||shapeHeight<1||pitch<1||
           (long)pitch*shapeHeight>shape.Length)throw new ArgumentException("잘못된 커서 버퍼다.");
        if(kind is not (1 or 2 or 4))throw new ArgumentException("알 수 없는 DXGI 커서 형식이다.");
        var drawnHeight=kind==1?shapeHeight/2:shapeHeight;
        if(kind==1&&(shapeHeight%2!=0||pitch<(shapeWidth+7)/8)||kind!=1&&pitch<shapeWidth*4)throw new ArgumentException("잘못된 커서 stride다.");
        for(var y=0;y<drawnHeight;y++)for(var x=0;x<shapeWidth;x++)
        {
            var dx=left+x;var dy=top+y;if(dx<0||dx>=width||dy<0||dy>=height)continue;
            var target=(dy*width+dx)*4;
            if(kind==1)
            {
                var mask=0x80>>(x%8);var and=(shape[y*pitch+x/8]&mask)!=0?255:0;
                var xor=(shape[(y+drawnHeight)*pitch+x/8]&mask)!=0?255:0;
                for(var c=0;c<3;c++)frame[target+c]=(byte)((frame[target+c]&and)^xor);
            }
            else
            {
                var source=y*pitch+x*4;var alpha=shape[source+3];
                if(kind==4&&alpha is not (0 or 255))throw new ArgumentException("잘못된 masked cursor alpha다.");
                for(var c=0;c<3;c++)frame[target+c]=kind==4
                    ? alpha==255?(byte)(frame[target+c]^shape[source+c]):shape[source+c]
                    : (byte)((shape[source+c]*alpha+frame[target+c]*(255-alpha)+127)/255);
            }
            frame[target+3]=255;
        }
    }
}
