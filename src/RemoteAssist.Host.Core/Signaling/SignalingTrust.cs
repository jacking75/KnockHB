using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace RemoteAssist.Host.Core.Signaling;

/// <summary>Explicit per-connection certificate trust; never modifies OS stores.</summary>
public static class SignalingTrust
{
    public static X509Certificate2? Load(string? encoded)
    {
        if (encoded is null) return null;
        if (encoded.Length is < 1 or > 16384) throw new ArgumentException("Certificate size limit.");
        return X509CertificateLoader.LoadCertificate(Convert.FromBase64String(encoded));
    }

    public static bool Validate(X509Certificate? certificate, SslPolicyErrors errors, X509Certificate2 trusted)
    {
        if (certificate is null || (errors & ~SslPolicyErrors.RemoteCertificateChainErrors) != 0) return false;
        using var peer = X509CertificateLoader.LoadCertificate(certificate.GetRawCertData());
        if (!CryptographicOperations.FixedTimeEquals(peer.RawData, trusted.RawData)) return false;
        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(trusted);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck; // Exact, short-lived, explicitly configured leaf.
        chain.ChainPolicy.VerificationFlags = X509VerificationFlags.NoFlag;
        chain.ChainPolicy.DisableCertificateDownloads = true;
        chain.ChainPolicy.ApplicationPolicy.Add(new Oid("1.3.6.1.5.5.7.3.1"));
        return chain.Build(peer);
    }
}
