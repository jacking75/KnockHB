using System.Security.Cryptography;
using System.Text;
using RemoteAssist.Protocol;
namespace RemoteAssist.Host.Core.Runbooks;

public static class RunbookCatalog
{
    public static RunbookDefinition[] Definitions()=>new[]{Definition("network-summary"),Definition("clear-dns-cache")};
    public static RunbookDefinition Definition(string id)
    {
        if(id is not ("network-summary" or "clear-dns-cache"))throw new InvalidDataException("미등록 Runbook이다.");
        using var resource=typeof(RunbookCatalog).Assembly.GetManifestResourceStream("RemoteAssist.Runbooks."+id+".ps1")??throw new IOException("설치 Runbook resource 누락이다.");
        using var reader=new StreamReader(resource,Encoding.UTF8,true);var source=reader.ReadToEnd().Replace("\r\n","\n");
        return new(id,1,Hash(source),id=="network-summary"?"네트워크 어댑터 개수 조회":"DNS 캐시 초기화",id=="network-summary"?"읽기 전용이다. 이름·IP·MAC을 전송하지 않는다.":"DNS 캐시를 비운다. 취소해도 이미 비운 캐시는 복원되지 않는다. 다음 DNS 조회에서 다시 채워진다.",id=="clear-dns-cache",source);
    }
    public static RunbookDefinition Demand(RunbookMessage request)
    {
        _=RunbookMessage.Parse(ProtocolJson.Encode(request));if(request.T!="runbook-request")throw new InvalidDataException("실행 요청이 아니다.");
        var book=Definition(request.BookId!);if(request.BookVersion!=book.BookVersion||request.Hash!=book.Hash)throw new UnauthorizedAccessException("승인한 Runbook hash/version이 아니다.");return book;
    }
    public static string Hash(string source)=>Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(source.Replace("\r\n","\n"))));
}
