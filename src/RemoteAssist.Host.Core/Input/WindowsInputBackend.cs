using System.Runtime.InteropServices;
using RemoteAssist.Host.Core.Windows;

namespace RemoteAssist.Host.Core.Input;

public sealed class WindowsInputBackend : IInputBackend
{
    public int Send(IReadOnlyList<InputStroke> strokes)
    {
        if(IntPtr.Size!=8)throw new PlatformNotSupportedException("Windows x64 입력 backend다.");
        if(strokes.Count>8192)throw new ArgumentOutOfRangeException(nameof(strokes));
        if(!InputDesktop.IsDefault())return 0;
        var left=GetSystemMetrics(76);var top=GetSystemMetrics(77);var width=GetSystemMetrics(78);var height=GetSystemMetrics(79);
        if(width<2||height<2)return 0;
        var inputs=new NativeInput[strokes.Count];
        for(var i=0;i<inputs.Length;i++)
        {
            var s=strokes[i];
            inputs[i]=s.Kind switch
            {
                StrokeKind.Move=>new() {Mouse=new() {X=Absolute(s.X,left,width),Y=Absolute(s.Y,top,height),Flags=0xC001}},
                StrokeKind.Button=>new() {Mouse=new() {Flags=(s.Button,s.Up) switch {("left",false)=>2,("left",true)=>4,("right",false)=>8,("right",true)=>16,("middle",false)=>32,("middle",true)=>64,_=>throw new ArgumentException("입력 버튼 오류다.")}}},
                StrokeKind.Wheel=>new() {Mouse=new() {Flags=0x800,Data=unchecked((uint)s.Delta)}},
                StrokeKind.Key=>new() {Type=1,Keyboard=new() {VirtualKey=s.Key,Flags=(s.Up?2u:0u)|(Extended(s.Key)?1u:0u)}},
                StrokeKind.Unicode=>new() {Type=1,Keyboard=new() {Scan=s.Character,Flags=s.Up?6u:4u}},
                _=>throw new ArgumentException("입력 종류 오류다.")
            };
        }
        if(!InputDesktop.IsDefault())return 0;
        return (int)SendInput((uint)inputs.Length,inputs,Marshal.SizeOf<NativeInput>());
    }
    static int Absolute(int pixel,int origin,int size)
    {if(pixel<origin||pixel>=origin+(long)size)throw new ArgumentException("가상 화면 밖 입력이다.");return (int)Math.Round((pixel-(double)origin)*65535/(size-1));}
    static bool Extended(ushort vk)=>vk is 0x21 or 0x22 or 0x23 or 0x24 or 0x25 or 0x26 or 0x27 or 0x28 or 0x2D or 0x2E or 0x5B or 0x5C or 0xA3 or 0xA5 or 0x6F;
    [StructLayout(LayoutKind.Sequential)]struct MouseInput {public int X,Y;public uint Data,Flags,Time;public nuint Extra;}
    [StructLayout(LayoutKind.Sequential)]struct KeyboardInput {public ushort VirtualKey,Scan;public uint Flags,Time;public nuint Extra;}
    [StructLayout(LayoutKind.Explicit,Size=40)]struct NativeInput {[FieldOffset(0)]public uint Type;[FieldOffset(8)]public MouseInput Mouse;[FieldOffset(8)]public KeyboardInput Keyboard;}
    [DllImport("user32.dll")]static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll",SetLastError=true)]static extern uint SendInput(uint count,NativeInput[] inputs,int bytes);
}
