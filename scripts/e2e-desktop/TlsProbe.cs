using System.Diagnostics;
using System.IO;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using System.Text.Json;
using RemoteAssist.Host.Core.Signaling;

namespace RemoteAssist.E2e;
internal static class TlsProbe
{
    public static int Run(string[] args)
    {
        try
        {
            if (args.Length is not (4 or 5) || args[0] != "--tls-check" || args[3] != "192.168.0.61" || (args.Length == 5 && args[4] != "--probe")) return 40;
            using var watchdog = new System.Threading.Timer(_ => Environment.Exit(124), null, 9000, Timeout.Infinite);
            var bytes = File.ReadAllBytes(args[1]);
            if (!Convert.ToHexString(SHA256.HashData(bytes)).Equals(args[2], StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("certificate-hash");
            using var cert = X509CertificateLoader.LoadCertificate(bytes);
            if (!cert.MatchesHostname(args[3], allowWildcards:false, allowCommonName:false) || !SignalingTrust.Validate(cert, SslPolicyErrors.None, cert)) throw new InvalidDataException("certificate-identity-or-validity");
            bool probed = args.Length == 5;
            if (probed)
            {
                using var deadline = new CancellationTokenSource(7000);
                using var tcp = new TcpClient();
                tcp.ConnectAsync(args[3],25001,deadline.Token).AsTask().GetAwaiter().GetResult();
                using var tls = new SslStream(tcp.GetStream(),false,(_,peer,_,errors)=>SignalingTrust.Validate(peer,errors,cert));
                tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions {TargetHost=args[3]},deadline.Token).GetAwaiter().GetResult();
            }
            using var rsa=cert.GetRSAPublicKey();using var ec=cert.GetECDsaPublicKey();
            var spki=rsa is not null?rsa.ExportSubjectPublicKeyInfo():ec is not null?ec.ExportSubjectPublicKeyInfo():throw new InvalidDataException("unsupported-key");
            Console.WriteLine(JsonSerializer.Serialize(new {status="completed",chainValid=true,storeMutated=false,endpointProbed=probed,hostnameValid=true,peerCertificateMatched=probed,certificateSha256=args[2].ToLowerInvariant(),spkiSha256=Convert.ToBase64String(SHA256.HashData(spki)),sid=WindowsIdentity.GetCurrent().User!.Value,session=Process.GetCurrentProcess().SessionId,tick=Stopwatch.GetTimestamp(),frequency=Stopwatch.Frequency,trustMode="exact-leaf-custom-root"}));
            return 0;
        }
        catch(Exception error){Console.WriteLine(JsonSerializer.Serialize(new {status="failed",error=error.GetType().Name}));return 10;}
    }
}
