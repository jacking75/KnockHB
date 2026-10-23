using System.Security.Cryptography;
using RemoteAssist.Host.Core.Files;
using RemoteAssist.Host.Core.Security;
using RemoteAssist.Protocol;

static class FileRegression
{
    static void Check(bool condition,string reason){if(!condition)throw new Exception(reason);}
    static void Reject(Action action){try{action();}catch{return;}throw new Exception("expected rejection did not occur");}
    public static Task Run()
    {
        foreach(var name in new[]{"..","../x","x:y","CON.txt","CON .txt","LPT1","COM¹.log","x.","x ","C:\\x","a/b","a\0b"})Check(!FileMessage.SafeName(name),"unsafe filename "+name);
        foreach(var path in new[]{@"C:\work\..\escape",@"\\server\share",@"C:\folder:stream",@"\\?\C:\folder"})Reject(()=>WindowsFileSandbox.LocalPath(path));
        var directory=Path.Combine(Path.GetTempPath(),"remoteassist-files-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);
        try
        {
            var permissions=new PermissionGate();var grant=permissions.Grant("sid","peer");
            foreach(var size in new[]{0,FileChunkHeader.MaxPayload+3})
            {
                var payload=Enumerable.Range(0,size).Select(i=>(byte)i).ToArray();var name="received-"+size+".dat";
                var offer=new FileMessage {T="file-offer",SessionId="sid",Generation=1,PermissionGeneration=grant.Generation,Sequence=1,Id=Guid.NewGuid().ToString("N"),Name=name,Size=size,Hash=Convert.ToHexStringLower(SHA256.HashData(payload))};
                using(var folder=new WindowsFileSandbox(directory))
                using(var candidate=folder.Create(name))
                using(var incoming=new IncomingFile(offer,permissions,grant.Generation,candidate.Stream,candidate.Commit,candidate.Dispose))
                {
                    uint sequence=0;for(var offset=0;offset<size;offset+=FileChunkHeader.MaxPayload){var length=Math.Min(FileChunkHeader.MaxPayload,size-offset);incoming.Chunk(new FileChunkHeader(Guid.ParseExact(offer.Id,"N"),sequence++,length).Encode(payload.AsSpan(offset,length)));}
                    incoming.Complete(offer with {T="file-complete",Name=null,Sequence=2});
                    Reject(()=>incoming.Complete(offer with {T="file-complete",Name=null,Sequence=3}));
                }
                Check(File.ReadAllBytes(Path.Combine(directory,name)).SequenceEqual(payload),"native hard-link commit lost payload");
                Check(Directory.GetFiles(directory,"*.part").Length==0,"committed temporary link survived");
                using(var folder=new WindowsFileSandbox(directory))
                using(var collision=folder.Create(name)){collision.Stream.WriteByte(99);Reject(collision.Commit);}
                Check(File.ReadAllBytes(Path.Combine(directory,name)).SequenceEqual(payload),"collision overwrote accepted file");
            }
            using(var folder=new WindowsFileSandbox(directory))
            {
                var faultOffer=new FileMessage {T="file-offer",SessionId="sid",Generation=1,PermissionGeneration=grant.Generation,Sequence=1,Id=Guid.NewGuid().ToString("N"),Name="disk-full.dat",Size=1,Hash=FeatureMessage.TextHash("x")};
                var aborted=false;using(var incoming=new IncomingFile(faultOffer,permissions,grant.Generation,new FullDisk(),()=>throw new Exception("failed storage committed"),()=>aborted=true))
                {Reject(()=>incoming.Chunk(new FileChunkHeader(Guid.ParseExact(faultOffer.Id,"N"),0,1).Encode([9])));Check(aborted,"I/O failure retained candidate");}
                using(var candidate=folder.Create("foreign.dat"))
                    Reject(()=>new IncomingFile(faultOffer with {SessionId="other"},permissions,grant.Generation,candidate.Stream,candidate.Commit,candidate.Dispose));
                using(var abandoned=folder.Create("cancelled.dat"))abandoned.Stream.WriteByte(9);
                Check(!File.Exists(Path.Combine(directory,"cancelled.dat"))&&Directory.GetFiles(directory,"*.part").Length==0,"cancel left candidate");
                var offer=new FileMessage {T="file-offer",SessionId="sid",Generation=1,PermissionGeneration=grant.Generation,Sequence=1,Id=Guid.NewGuid().ToString("N"),Name="hash.dat",Size=1,Hash=FeatureMessage.TextHash("x")};
                using(var candidate=folder.Create("hash.dat"))
                using(var incoming=new IncomingFile(offer,permissions,grant.Generation,candidate.Stream,candidate.Commit,candidate.Dispose))
                {
                    incoming.Chunk(new FileChunkHeader(Guid.ParseExact(offer.Id,"N"),0,1).Encode([9]));
                    Reject(()=>incoming.Complete(offer with {T="file-complete",Name=null,Sequence=2}));
                }
                Check(!File.Exists(Path.Combine(directory,"hash.dat")),"tampered hash committed");
                using(var candidate=folder.Create("duplicate.dat"))
                using(var incoming=new IncomingFile(offer with {Name="duplicate.dat"},permissions,grant.Generation,candidate.Stream,candidate.Commit,candidate.Dispose))
                {
                    var packet=new FileChunkHeader(Guid.ParseExact(offer.Id,"N"),0,1).Encode([9]);incoming.Chunk(packet);Reject(()=>incoming.Chunk(packet));
                    Reject(()=>incoming.Complete(offer with {T="file-complete",Name=null,Sequence=2}));
                }
                using(var candidate=folder.Create("missing.dat"))
                using(var incoming=new IncomingFile(offer with {Name="missing.dat"},permissions,grant.Generation,candidate.Stream,candidate.Commit,candidate.Dispose))
                {
                    Reject(()=>incoming.Chunk(new FileChunkHeader(Guid.ParseExact(offer.Id,"N"),1,1).Encode([9])));
                    Reject(()=>incoming.Chunk(new FileChunkHeader(Guid.ParseExact(offer.Id,"N"),0,1).Encode([9])));
                }
                using(var candidate=folder.Create("revoked.dat"))
                using(var incoming=new IncomingFile(offer with {Name="revoked.dat"},permissions,grant.Generation,candidate.Stream,candidate.Commit,candidate.Dispose))
                {permissions.End();Reject(()=>incoming.Chunk(new FileChunkHeader(Guid.ParseExact(offer.Id,"N"),0,1).Encode([9])));}
                Check(!File.Exists(Path.Combine(directory,"revoked.dat")),"file committed after the session ended");
                using var source=folder.Read("received-16387.dat");Check(source.Length==16387,"native input read failed");
                Reject(()=>new FileStream(Path.Combine(directory,"received-16387.dat"),FileMode.Open,FileAccess.Write,FileShare.ReadWrite));
            }
        }
        finally{Directory.Delete(directory,true);}
        return Task.CompletedTask;
    }
    sealed class FullDisk:MemoryStream {public override void Write(byte[] buffer,int offset,int count)=>throw new IOException("injected storage full");public override void Write(ReadOnlySpan<byte> buffer)=>throw new IOException("injected storage full");}
}
