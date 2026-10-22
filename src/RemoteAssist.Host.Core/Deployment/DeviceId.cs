using System.Security.Cryptography;
using System.Text;

namespace RemoteAssist.Host.Core.Deployment;

/// <summary>The stable 9-digit support ID of this PC. The supporter types it to connect; it never changes.</summary>
public static class DeviceId
{
    static string FilePath => Path.Combine(InstallationState.Data, "device-id.txt");

    public static string LoadOrCreate()
    {
        if (File.Exists(FilePath))
        {
            InstallationState.DemandProtected(FilePath);
            var saved = File.ReadAllText(FilePath, Encoding.ASCII).Trim();
            if (Valid(saved)) return saved;
        }
        var id = string.Create(9, 0, (span, _) => { for (var i = 0; i < span.Length; i++) span[i] = (char)('0' + RandomNumberGenerator.GetInt32(10)); });
        InstallationState.Atomic(FilePath, Encoding.ASCII.GetBytes(id));
        return id;
    }

    public static bool Valid(string? value) => value is { Length: 9 } && value.All(char.IsAsciiDigit);
}
