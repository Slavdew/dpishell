using DpiShell.Core.Router;

namespace DpiShell.Tests;

public class RouterHostKeyTests
{
    [Fact]
    public void Decide_NewSameChanged()
    {
        var fp = HostKeyCheck.Format("ssh-ed25519", "ohD8VZEXGWo6Ez8GSEJQ9WpafgLFsOfLOtGGQCQo6Og");
        Assert.Equal("ssh-ed25519 SHA256:ohD8VZEXGWo6Ez8GSEJQ9WpafgLFsOfLOtGGQCQo6Og", fp);
        Assert.Equal(HostKeyVerdict.New, HostKeyCheck.Decide(null, fp));
        Assert.Equal(HostKeyVerdict.Same, HostKeyCheck.Decide(fp, fp));
        Assert.Equal(HostKeyVerdict.Changed, HostKeyCheck.Decide(fp, HostKeyCheck.Format("ssh-ed25519", "другой")));
        Assert.Contains("переустановили прошивку", HostKeyCheck.ChangedText);
    }

    [Fact]
    public void FileStore_RemembersAndForgets()
    {
        using var d = new TempDir();
        var path = d.Sub("router-hosts.json");
        var id = new RouterSshTarget("192.168.1.1").HostKeyId;
        Assert.Equal("192.168.1.1:22", id);
        new FileHostKeyStore(path).Set(id, "ssh-ed25519 SHA256:abc");
        var again = new FileHostKeyStore(path);
        Assert.Equal("ssh-ed25519 SHA256:abc", again.Get(id));
        Assert.Null(again.Get("10.0.0.1:22"));
        again.Remove(id);
        Assert.Null(new FileHostKeyStore(path).Get(id));
    }

    [Fact]
    public void FileStore_BrokenFile_IsEmpty()
    {
        using var d = new TempDir();
        var path = d.Sub("router-hosts.json");
        File.WriteAllText(path, "{не json");
        Assert.Null(new FileHostKeyStore(path).Get("x:22"));
    }

    [Fact]
    public void ShQuote_EscapesSingleQuote()
    {
        Assert.Equal("'/tmp/a b'", Sh.Quote("/tmp/a b"));
        Assert.Equal("'it'\\''s'", Sh.Quote("it's"));
    }
}
