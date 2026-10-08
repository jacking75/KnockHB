using System.Text.Json;
using System.Text.Json.Serialization;
namespace RemoteAssist.Protocol;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RunbookMessage:DataMessage
{
    [JsonPropertyName("bookId"),JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)]public string? BookId {get;init;}
    [JsonPropertyName("bookVersion"),JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)]public int? BookVersion {get;init;}
    [JsonPropertyName("hash"),JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)]public string? Hash {get;init;}
    [JsonPropertyName("args"),JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)]public Dictionary<string,string>? Args {get;init;}
    [JsonPropertyName("books"),JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)]public RunbookDefinition[]? Books {get;init;}
    [JsonPropertyName("result"),JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)]public RunbookResult? Result {get;init;}
    public static RunbookMessage Parse(ReadOnlyMemory<byte> bytes)
    {
        using var document=ProtocolJson.Parse(bytes,30720);var m=JsonSerializer.Deserialize<RunbookMessage>(bytes.Span)??throw new InvalidDataException("Runbook 본문 누락이다.");
        if(m.SessionId is not {Length:>0 and <=64}||m.Generation is <1 or >9007199254740991L||m.PermissionGeneration is <1 or >9007199254740991L||m.Sequence is <1 or >9007199254740991L||!Guid.TryParseExact(m.Id,"N",out var id)||id==Guid.Empty||id.ToString("N")!=m.Id)throw new InvalidDataException("Runbook 바인딩 오류다.");
        if(m.T=="runbook-request")
        {if(m.BookId is not ("network-summary" or "clear-dns-cache")||m.BookVersion!=1||m.Hash is not {Length:64}||m.Hash.Any(c=>c is not (>= '0' and <= '9' or >= 'a' and <= 'f'))||m.Args is null||m.Args.Count!=1||!m.Args.TryGetValue("detail",out var detail)||detail is not ("summary" or "counts"))throw new InvalidDataException("등록 Runbook 인자 오류다.");}
        else if(m.BookId!=null||m.BookVersion!=null||m.Hash!=null||m.Args!=null)throw new InvalidDataException("Runbook 추가 실행 필드다.");
        if(m.T=="runbook-catalog"){if(m.Books is null||m.Books.Length is <1 or >16)throw new InvalidDataException("Runbook 목록 오류다.");foreach(var book in m.Books)book.Validate();}else if(m.Books!=null)throw new InvalidDataException("추가 Runbook 목록이다.");
        if(m.T=="runbook-result"){(m.Result??throw new InvalidDataException("Runbook 결과 누락이다.")).Validate();}else if(m.Result!=null)throw new InvalidDataException("추가 Runbook 결과다.");
        if(m.T is not ("runbook-request" or "runbook-cancel" or "runbook-catalog" or "runbook-result"))throw new InvalidDataException("Runbook 명령 오류다.");return m;
    }
}
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RunbookDefinition([property:JsonPropertyName("bookId")]string BookId,[property:JsonPropertyName("bookVersion")]int BookVersion,[property:JsonPropertyName("hash")]string Hash,[property:JsonPropertyName("title")]string Title,[property:JsonPropertyName("impact")]string Impact,[property:JsonPropertyName("requiresAdmin")]bool RequiresAdmin,[property:JsonPropertyName("source")]string Source)
{public void Validate(){if(BookId is not ("network-summary" or "clear-dns-cache")||BookVersion!=1||Hash is not {Length:64}||Hash.Any(c=>c is not (>= '0' and <= '9' or >= 'a' and <= 'f'))||Title is not {Length:>0 and <=128}||Impact is not {Length:<=1024}||Source is not {Length:>0 and <=8192}||!FeatureMessage.ValidUnicode(Source)||RequiresAdmin!=(BookId=="clear-dns-cache"))throw new InvalidDataException("Runbook 정의 오류다.");}}
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RunbookResult([property:JsonPropertyName("state")]string State,[property:JsonPropertyName("exitCode")]int? ExitCode,[property:JsonPropertyName("stdout")]string Stdout,[property:JsonPropertyName("stderr")]string Stderr,[property:JsonPropertyName("remainingImpact")]string RemainingImpact)
{public void Validate(){if(State is not ("denied" or "completed" or "failed" or "cancelled" or "timed-out")||Stdout is not {Length:<=4096}||Stderr is not {Length:<=4096}||RemainingImpact is not {Length:<=1024}||!FeatureMessage.ValidUnicode(Stdout)||!FeatureMessage.ValidUnicode(Stderr)||System.Text.Encoding.UTF8.GetByteCount(Stdout+Stderr)>16384)throw new InvalidDataException("Runbook 결과 상한 오류다.");}}
