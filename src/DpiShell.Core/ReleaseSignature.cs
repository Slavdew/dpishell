using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;

namespace DpiShell.Core;

/// <summary>
/// Проверка подписей ed25519 наших выпусков и манифеста данных (release.json.sig, data.json.sig): base64 подписи
/// над <b>точными байтами</b> файла — без канонизации JSON. Подписывает CI ключом из секрета RELEASE_SIGNING_KEY
/// (<c>openssl pkeyutl -sign -rawin</c>); проверочный ключ зашит в программу и лежит в <c>packaging/keys/release-ed25519.pem</c>.
/// Менять ключ — только вместе с секретом (docs/dev/building.md «Подписи»). Библиотека — BouncyCastle.Cryptography (docs/vetting.md).
/// </summary>
public static class ReleaseSignature
{
    /// <summary>Проверочный ключ выпусков (SubjectPublicKeyInfo ed25519). Тот же — в packaging/keys/release-ed25519.pem.</summary>
    public const string PublicKeyPem =
        "-----BEGIN PUBLIC KEY-----\n" +
        "MCowBQYDK2VwAyEAmlIZJmnDKpEMj6cVMUws6CA/u+hiJ40NL5snqIr8idM=\n" +
        "-----END PUBLIC KEY-----\n";

    /// <summary>Расширение файла подписи рядом с подписанным: <c>release.json</c> → <c>release.json.sig</c>.</summary>
    public const string Extension = ".sig";

    /// <summary>Подпись ed25519 — 64 байта; файл подписи длиннее 1 КБ не читаем.</summary>
    public const int MaxSignatureFileBytes = 1024;

    // DER SubjectPublicKeyInfo для ed25519 (RFC 8410): SEQUENCE { SEQUENCE { OID 1.3.101.112 } BIT STRING (32 байта) }.
    // Объявлен до ReleaseKey: статические поля инициализируются по порядку.
    static readonly byte[] SpkiPrefix = [0x30, 0x2a, 0x30, 0x05, 0x06, 0x03, 0x2b, 0x65, 0x70, 0x03, 0x21, 0x00];

    /// <summary>Сырой открытый ключ выпусков (32 байта).</summary>
    public static byte[] ReleaseKey { get; } = ParsePublicKey(PublicKeyPem);

    /// <summary>Открытый ключ ed25519 из PEM (<c>openssl pkey -pubout</c>) — 32 байта. Не тот формат — исключение.</summary>
    public static byte[] ParsePublicKey(string pem)
    {
        var body = string.Concat(pem.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith("-----", StringComparison.Ordinal)));
        byte[] der;
        try { der = Convert.FromBase64String(body); }
        catch (FormatException) { throw new InvalidDataException("открытый ключ — не base64"); }
        if (der.Length != SpkiPrefix.Length + 32 || !der.AsSpan(0, SpkiPrefix.Length).SequenceEqual(SpkiPrefix))
            throw new InvalidDataException("открытый ключ — не ed25519");
        return der[SpkiPrefix.Length..];
    }

    /// <summary>
    /// Подпись <paramref name="signature"/> (содержимое .sig: base64, пробелы и перевод строки по краям допускаются)
    /// верна для <paramref name="data"/> и ключа (по умолчанию — ключ выпусков). Любой сбой разбора — false.
    /// </summary>
    public static bool Verify(ReadOnlySpan<byte> data, string? signature, byte[]? publicKey = null)
    {
        if (string.IsNullOrWhiteSpace(signature)) return false;
        byte[] sig;
        try { sig = Convert.FromBase64String(signature.Trim()); }
        catch (FormatException) { return false; }
        if (sig.Length != Ed25519PublicKeyParameters.KeySize * 2) return false;
        var key = publicKey ?? ReleaseKey;
        if (key.Length != Ed25519PublicKeyParameters.KeySize) return false;
        try
        {
            var v = new Ed25519Signer();
            v.Init(false, new Ed25519PublicKeyParameters(key));
            v.BlockUpdate(data);
            return v.VerifySignature(sig);
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException) { return false; }
    }

    /// <summary>То же для байтов подписи как они пришли из файла (UTF-8/ASCII).</summary>
    public static bool Verify(ReadOnlySpan<byte> data, ReadOnlySpan<byte> signatureFile, byte[]? publicKey = null) =>
        signatureFile.Length <= MaxSignatureFileBytes && Verify(data, System.Text.Encoding.ASCII.GetString(signatureFile), publicKey);
}
