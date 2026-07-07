using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;

namespace FastDOM.Infrastructure.Security;

/// <summary>
/// Stores sensitive data using DPAPI (Windows Data Protection API).
/// Encrypted blob is scoped to the current Windows user session.
/// Never stores raw tokens in plaintext.
/// </summary>
[SupportedOSPlatform("windows")]
public class SecureStorage
{
    private readonly ILogger<SecureStorage> _logger;
    private readonly string _storageDir;

    public SecureStorage(ILogger<SecureStorage> logger, string? storageDir = null)
    {
        _logger = logger;
        _storageDir = storageDir ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "FastDOM", "secure");
        Directory.CreateDirectory(_storageDir);
    }

    public void Store(string key, string value)
    {
        try
        {
            var data = Encoding.UTF8.GetBytes(value);
            var encrypted = ProtectedData.Protect(data, GetEntropy(key), DataProtectionScope.CurrentUser);
            File.WriteAllBytes(GetFilePath(key), encrypted);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to securely store key '{Key}'", key);
            throw;
        }
    }

    public string? Retrieve(string key)
    {
        var path = GetFilePath(key);
        if (!File.Exists(path)) return null;
        try
        {
            var encrypted = File.ReadAllBytes(path);
            var data = ProtectedData.Unprotect(encrypted, GetEntropy(key), DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(data);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to retrieve key '{Key}' — data may be corrupt or from different user", key);
            return null;
        }
    }

    public void Delete(string key)
    {
        var path = GetFilePath(key);
        if (File.Exists(path)) File.Delete(path);
    }

    public bool Exists(string key) => File.Exists(GetFilePath(key));

    private string GetFilePath(string key) =>
        Path.Combine(_storageDir, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))) + ".bin");

    private static byte[] GetEntropy(string key) =>
        SHA256.HashData(Encoding.UTF8.GetBytes("FastDOM_" + key));
}
