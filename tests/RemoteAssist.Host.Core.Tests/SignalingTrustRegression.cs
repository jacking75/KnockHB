using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using RemoteAssist.Host.Core.Signaling;

internal static class SignalingTrustRegression
{
    public static Task Run()
    {
        using var key=RSA.Create(2048);
        X509Certificate2 Make(DateTimeOffset start,DateTimeOffset end)
        {
            var r=new CertificateRequest("CN=TLS regression",key,HashAlgorithmName.SHA256,RSASignaturePadding.Pkcs1);
            var san=new SubjectAlternativeNameBuilder();san.AddIpAddress(System.Net.IPAddress.Parse("192.168.0.61"));r.CertificateExtensions.Add(san.Build());
            r.CertificateExtensions.Add(new X509BasicConstraintsExtension(true,false,0,true));
            r.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection {new Oid("1.3.6.1.5.5.7.3.1")},true));
            return r.CreateSelfSigned(start,end);
        }
        using var cert=Make(DateTimeOffset.UtcNow.AddMinutes(-1),DateTimeOffset.UtcNow.AddHours(1));
        using var other=Make(DateTimeOffset.UtcNow.AddMinutes(-2),DateTimeOffset.UtcNow.AddHours(1));
        using var expired=Make(DateTimeOffset.UtcNow.AddHours(-2),DateTimeOffset.UtcNow.AddHours(-1));
        if(!SignalingTrust.Validate(cert,SslPolicyErrors.RemoteCertificateChainErrors,cert) ||
           SignalingTrust.Validate(cert,SslPolicyErrors.RemoteCertificateNameMismatch,cert) ||
           SignalingTrust.Validate(null,SslPolicyErrors.RemoteCertificateNotAvailable,cert) ||
           SignalingTrust.Validate(other,SslPolicyErrors.RemoteCertificateChainErrors,cert) ||
           SignalingTrust.Validate(expired,SslPolicyErrors.RemoteCertificateChainErrors,expired) ||
           !cert.MatchesHostname("192.168.0.61",false,false) || cert.MatchesHostname("192.168.0.74",false,false))
            throw new InvalidOperationException("Pinned TLS validation regression.");
        Console.WriteLine("PASS pinned TLS: correct leaf, name mismatch, missing peer, changed leaf, expiry, IP SAN");
        return Task.CompletedTask;
    }
}
