using System.Security.Cryptography;
using System.Text;

namespace DeficitSnatcher.Api.Settings;

/// <summary>
/// Encrypts what's kept in the database that would hurt if the database leaked: the checkout profile (address, card),
/// the settings (notification tokens) and each site's cookies. AES-GCM with the key from Secrets__Key (32 random bytes,
/// base64: openssl rand -base64 32); unset, a key derived from the JWT signing key. Never change or lose it once set:
/// what was encrypted with it can't be read without it.
/// </summary>
public sealed class SecretBox
{
    private readonly byte[] _key;

    public SecretBox(IConfiguration config)
    {
        var configured = config["Secrets:Key"];
        if (!string.IsNullOrWhiteSpace(configured))
        {
            _key = Convert.FromBase64String(configured);
            if (_key.Length != 32)
                throw new InvalidOperationException("Secrets:Key must be 32 bytes, base64 (openssl rand -base64 32).");
        }
        else
        {
            var jwtKey = config["Jwt:SigningKey"] ?? throw new InvalidOperationException("Jwt:SigningKey is not configured.");
            _key = SHA256.HashData(Encoding.UTF8.GetBytes("deficitsnatcher-secrets:" + jwtKey));
        }
    }

    public string Encrypt(string plain)
    {
        var data = Encoding.UTF8.GetBytes(plain);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var tag = new byte[16];
        var cipher = new byte[data.Length];
        using var aes = new AesGcm(_key, 16);
        aes.Encrypt(nonce, data, cipher, tag);
        return Convert.ToBase64String([.. nonce, .. tag, .. cipher]);
    }

    public string Decrypt(string sealedText)
    {
        var all = Convert.FromBase64String(sealedText);
        var nonce = all.AsSpan(0, 12);
        var tag = all.AsSpan(12, 16);
        var cipher = all.AsSpan(28);
        var plain = new byte[cipher.Length];
        using var aes = new AesGcm(_key, 16);
        aes.Decrypt(nonce, cipher, tag, plain);
        return Encoding.UTF8.GetString(plain);
    }
}
