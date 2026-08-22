using System.Security.Cryptography;
using System.Text;

namespace CoreX.Hub.Application.Catalog;

/// <summary>
/// Derives a stable identifier from a natural key so that seeding the same
/// <c>hub.config.yaml</c> into two fresh databases produces identical rows.
/// This is what makes the seed deterministic rather than merely idempotent.
/// </summary>
public static class DeterministicGuid
{
    public static Guid Create(string kind, params string[] naturalKeyParts)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        ArgumentNullException.ThrowIfNull(naturalKeyParts);

        var material = string.Join('\u001f', naturalKeyParts.Prepend(kind));
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(material));

        var bytes = new byte[16];
        Array.Copy(hash, bytes, 16);

        // Stamp RFC 4122 version 8 (custom) and the IETF variant so the value is a
        // well formed UUID rather than an arbitrary 128 bit blob.
        bytes[7] = (byte)((bytes[7] & 0x0F) | 0x80);
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);

        return new Guid(bytes);
    }
}
