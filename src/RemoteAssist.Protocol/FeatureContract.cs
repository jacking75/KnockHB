namespace RemoteAssist.Protocol;

public static class FeatureContract
{
    static readonly string[] names=["screen","control","admin","file","clipboard","diagnostics","runbook","chat"];
    public static IReadOnlyList<string> Names {get;}=Array.AsReadOnly(names);
    public static string[] Permissions(int mask)
    {
        if(mask<=0||(mask&~255)!=0||(mask&2)!=0&&(mask&1)==0||(mask&4)!=0&&(mask&3)!=3)throw new InvalidDataException("권한 조합 오류다.");
        return names.Where((_,i)=>(mask&(1<<i))!=0).ToArray();
    }
    public static int ParsePermissions(string[]? names)
    {
        if(names is null||names.Length==0||names.Length>8)throw new InvalidDataException("승인 권한 목록 오류다.");
        var mask=0;foreach(var name in names){var index=Array.IndexOf(FeatureContract.names,name);if(index<0||(mask&(1<<index))!=0)throw new InvalidDataException("미지 또는 중복 권한이다.");mask|=1<<index;}
        if(!names.SequenceEqual(Permissions(mask)))throw new InvalidDataException("권한 목록 순서 오류다.");return mask;
    }
    public static string[] Channels(int mask)
    {
        _=Permissions(mask);var result=new List<string>{"control"};
        if((mask&2)!=0){result.Add("input");result.Add("pointer");}
        if((mask&8)!=0)result.Add("file");
        if((mask&(16|128))!=0)result.Add("chat");
        if((mask&32)!=0)result.Add("telemetry");if((mask&64)!=0)result.Add("runbook");return result.ToArray();
    }
}
