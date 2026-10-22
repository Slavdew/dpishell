using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace DpiShell.Core.Router;

/// <summary>Сертификат HTTPS роутера сменился: новый отпечаток и запись хранилища (<see cref="RouterCert.PinId"/>), где лежит прежний.</summary>
public sealed record RouterCertChange(string PinId, string Host, string Fingerprint);

/// <summary>
/// Закрепление сертификата HTTPS роутера (самоподписанный сертификат uhttpd): как ключ SSH — отпечаток запоминается при первом входе
/// в тот же router-hostkeys.json (запись «https://адрес:порт»), смена — отказ, пока окно не примет новый (по SSH или с согласия пользователя).
/// Проверку целиком не отключаем: без хранилища принимается только сертификат, которому доверяет Windows.
/// </summary>
public static class RouterCert
{
    /// <summary>Запись хранилища: «https://адрес:порт» (у SSH — «адрес:порт», не пересекаются).</summary>
    public static string PinId(string host, int port) => $"https://{host.Trim().ToLowerInvariant()}:{port}";

    /// <summary>Отпечаток — SHA-256 сертификата в DER, как у <c>sha256sum /etc/uhttpd.crt</c>.</summary>
    public static string Fingerprint(byte[] der) => "x509 SHA256:" + Convert.ToHexStringLower(SHA256.HashData(der));

    public static string Fingerprint(X509Certificate cert) => Fingerprint(cert.GetRawCertData());

    /// <summary>Отпечаток из sha256sum файла DER (64 hex) — в том же виде.</summary>
    public static string? FromSha256Hex(string hex)
    {
        hex = hex.Trim().ToLowerInvariant();
        return hex.Length == 64 && hex.All(Uri.IsHexDigit) ? "x509 SHA256:" + hex : null;
    }

    /// <summary>Отпечаток из PEM (первый сертификат); не PEM — null.</summary>
    public static string? FromPem(string pem)
    {
        try
        {
            using var c = X509Certificate2.CreateFromPem(pem);
            return Fingerprint(c);
        }
        catch (Exception e) when (e is CryptographicException or ArgumentException) { return null; }
    }

    /// <summary>
    /// Решение для одного рукопожатия TLS. stored — закреплённый отпечаток (null — первый вход), received — пришедший.
    /// Без хранилища (<paramref name="canPin"/> false) — только то, чему доверяет Windows.
    /// </summary>
    public static bool Accept(string? stored, string received, SslPolicyErrors errors, bool canPin) =>
        stored is null ? errors == SslPolicyErrors.None || canPin
            : string.Equals(stored, received, StringComparison.Ordinal);
}
