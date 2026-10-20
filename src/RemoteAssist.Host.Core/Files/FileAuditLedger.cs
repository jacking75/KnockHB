using RemoteAssist.Protocol;

namespace RemoteAssist.Host.Core.Files;

/// <summary>Service-owned metadata survives helper death. Stop calls End on its audit worker, after killing jobs.</summary>
public sealed class FileAuditLedger
{
    readonly object gate=new();
    readonly Dictionary<string,FileAuditNotice> active=new(StringComparer.Ordinal);
    readonly HashSet<string> finished=new(StringComparer.Ordinal);
    readonly Queue<string> history=new();
    bool ended;

    public void Record(FileAuditNotice notice,Action<FileAuditNotice> persist)
    {
        notice.Validate();
        lock(gate)
        {
            if(ended)throw new InvalidOperationException("종료한 파일 감사 수명이다.");
            if(notice.Phase=="started")
            {
                if(active.Count>=16||active.ContainsKey(notice.Id)||finished.Contains(notice.Id))throw new InvalidDataException("파일 시작 감사 중복 또는 상한이다.");
                persist(notice);active.Add(notice.Id,notice);
            }
            else
            {
                if(!active.TryGetValue(notice.Id,out var start)||start with {Phase=notice.Phase,Outcome=notice.Outcome}!=notice)throw new InvalidDataException("파일 종료 감사 바인딩 오류다.");
                persist(notice);active.Remove(notice.Id);Remember(notice.Id);
            }
        }
    }
    public void End(Action<FileAuditNotice> persist)
    {
        lock(gate)
        {
            if(ended)return;ended=true;List<Exception>? errors=null;
            foreach(var notice in active.Values)
                try{persist(notice with {Phase="finished",Outcome="stopped"});}catch(Exception ex){(errors??=[]).Add(ex);}
            active.Clear();finished.Clear();history.Clear();
            if(errors!=null)throw new AggregateException("중단 파일 감사 저장 실패다.",errors);
        }
    }
    void Remember(string id){finished.Add(id);history.Enqueue(id);if(history.Count>256)finished.Remove(history.Dequeue());}
}
