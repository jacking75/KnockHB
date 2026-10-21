namespace RemoteAssist.E2e;

internal static class OcrPolicy
{
    internal static bool Uac(string text)=>text.Contains("사용자 계정 컨트롤",StringComparison.Ordinal)||text.Contains("User Account Control",StringComparison.OrdinalIgnoreCase);
    internal static bool Security(string text)
    {
        var groups=new[]{new[]{"잠금","Lock"},new[]{"로그아웃","Sign out"},new[]{"작업 관리자","Task Manager"},new[]{"사용자 전환","Switch user"}};
        return !Uac(text)&&groups.Count(g=>g.Any(s=>text.Contains(s,StringComparison.OrdinalIgnoreCase)))>=2;
    }
    internal static bool Cancel(string text)=>text is "아니요" or "아니오" or "No" or "취소" or "Cancel";
}
