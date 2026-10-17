using System.Text;
using DpiShell.Core;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using Org.BouncyCastle.Security;

namespace DpiShell.Tests;

/// <summary>Тестовый ключ ed25519: подпись в формате CI (base64 над точными байтами файла).</summary>
public sealed class TestSigner
{
    readonly Ed25519PrivateKeyParameters _key = new(new SecureRandom());

    /// <summary>Сырой открытый ключ (32 байта) — подставляется вместо ключа выпусков.</summary>
    public byte[] PublicKey => _key.GeneratePublicKey().GetEncoded();

    public string Sign(byte[] data)
    {
        var s = new Ed25519Signer();
        s.Init(true, _key);
        s.BlockUpdate(data, 0, data.Length);
        return Convert.ToBase64String(s.GenerateSignature());
    }
}

public class ReleaseSignatureTests
{
    [Fact]
    public void Signed_bytes_verify_changed_byte_and_other_key_fail()
    {
        var signer = new TestSigner();
        var data = Encoding.UTF8.GetBytes("{\"schema\":1,\"version\":\"1.2.3\"}\n");
        var sig = signer.Sign(data);
        Assert.True(ReleaseSignature.Verify(data, sig, signer.PublicKey));
        Assert.True(ReleaseSignature.Verify(data, sig + "\n", signer.PublicKey));

        var changed = (byte[])data.Clone();
        changed[5] ^= 1;
        Assert.False(ReleaseSignature.Verify(changed, sig, signer.PublicKey));
        // Без канонизации: лишний перевод строки — уже другой файл.
        Assert.False(ReleaseSignature.Verify([.. data, (byte)'\n'], sig, signer.PublicKey));
        Assert.False(ReleaseSignature.Verify(data, sig, new TestSigner().PublicKey));
        Assert.False(ReleaseSignature.Verify(data, sig));   // ключ выпусков — не тестовый
    }

    [Fact]
    public void Broken_signature_text_is_false_not_exception()
    {
        var signer = new TestSigner();
        var data = "x"u8.ToArray();
        Assert.False(ReleaseSignature.Verify(data, (string?)null, signer.PublicKey));
        Assert.False(ReleaseSignature.Verify(data, "не base64", signer.PublicKey));
        Assert.False(ReleaseSignature.Verify(data, Convert.ToBase64String(new byte[10]), signer.PublicKey));
        Assert.False(ReleaseSignature.Verify(data, Convert.ToBase64String(new byte[64]), signer.PublicKey));
        Assert.False(ReleaseSignature.Verify(data, signer.Sign(data), new byte[5]));
    }

    /// <summary>Подпись, сделанная так же, как в CI: <c>openssl pkeyutl -sign -inkey t.pem -rawin -in m.json | base64 -w0</c> (OpenSSL 3.5).</summary>
    [Fact]
    public void Openssl_signature_verifies()
    {
        var key = ReleaseSignature.ParsePublicKey("-----BEGIN PUBLIC KEY-----\nMCowBQYDK2VwAyEAdwIHr1wcKsQZiYvKDUYV2v3zQW9agUHweuKpWX+EY50=\n-----END PUBLIC KEY-----\n");
        var data = Encoding.UTF8.GetBytes("{\"schema\":1,\"version\":\"1.2.3\"}\n");
        const string sig = "CVyqVzdp9Cm1bMRTJ1yswsl1EvSjG9Kigon/1gzdRc4dI1vsbDLLwsJ8C365PC1m+pc9z31i7pWK534XKEFMCg==";
        Assert.True(ReleaseSignature.Verify(data, sig, key));
        Assert.False(ReleaseSignature.Verify(data[..^1], sig, key));
    }

    /// <summary>Ключ в коде = ключ в packaging/keys (его сверяет CI с секретом RELEASE_SIGNING_KEY).</summary>
    [Fact]
    public void Embedded_key_matches_repository_file()
    {
        var file = Path.Combine(Path.GetDirectoryName(Repo.Data)!, "packaging", "keys", "release-ed25519.pem");
        Assert.Equal(ReleaseSignature.ParsePublicKey(File.ReadAllText(file)), ReleaseSignature.ReleaseKey);
        Assert.Equal(32, ReleaseSignature.ReleaseKey.Length);
        Assert.Throws<InvalidDataException>(() => ReleaseSignature.ParsePublicKey("-----BEGIN PUBLIC KEY-----\nAAAA\n-----END PUBLIC KEY-----"));
    }
}
