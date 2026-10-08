using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;
using RemoteAssist.Host.Core.Windows;

namespace RemoteAssist.Host.Core.Input;

/// <summary>Only key-up restoration; deliberately has no down, pointer, wheel, capture or command API.</summary>
public sealed class WindowsRecoveryBackend(uint sessionId,string userSid):IInputBackend
{
    public int Send(IReadOnlyList<InputStroke> strokes)
    {
        if(strokes.Count>4353||strokes.Any(s=>!s.Up||s.Kind is not (StrokeKind.Key or StrokeKind.Button or StrokeKind.Unicode)))throw new UnauthorizedAccessException("복원에는 up만 허용한다.");
        using var identity=WindowsIdentity.GetCurrent();
        if(!identity.IsSystem||System.Diagnostics.Process.GetCurrentProcess().SessionId!=sessionId)throw new UnauthorizedAccessException("복원 대상 SYSTEM 세션이 아니다.");
        var result=new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread=new Thread(()=>
        {
            nint desktop=0;
            var original=GetThreadDesktop(GetCurrentThreadId());
            try
            {
                if(!SessionProcess.IsInteractiveConsole(sessionId)||SessionProcess.RecoveryConsoleUserSid(sessionId)!=userSid)throw new UnauthorizedAccessException("복원 대상 사용자가 변경됐다.");
                desktop=OpenInputDesktop(0,false,0x1FF);
                if(desktop==0||!SetThreadDesktop(desktop))throw new Win32Exception(Marshal.GetLastWin32Error());
                // Recheck the session immediately before the only permitted native write.
                if(!SessionProcess.IsInteractiveConsole(sessionId)||SessionProcess.RecoveryConsoleUserSid(sessionId)!=userSid)throw new UnauthorizedAccessException("복원 대상 콘솔/사용자가 종료됐다.");
                result.TrySetResult(WindowsInputBackend.SendRecoveryUps(strokes));
            }
            catch(Exception ex){result.TrySetException(ex);}
            finally{if(desktop!=0){SetThreadDesktop(original);CloseDesktop(desktop);}}
        }) {IsBackground=true,Name="RemoteAssist key-up recovery"};
        thread.Start();return result.Task.GetAwaiter().GetResult();
    }
    [DllImport("user32.dll",SetLastError=true)]static extern nint OpenInputDesktop(uint flags,bool inherit,uint access);
    [DllImport("user32.dll",SetLastError=true)]static extern bool SetThreadDesktop(nint desktop);
    [DllImport("user32.dll")]static extern bool CloseDesktop(nint desktop);
    [DllImport("kernel32.dll")]static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")]static extern nint GetThreadDesktop(uint thread);
}
