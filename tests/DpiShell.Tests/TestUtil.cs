using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using DpiShell.Core;

namespace DpiShell.Tests;

/// <summary>Временный каталог, удаляется в Dispose.</summary>
public sealed class TempDir : IDisposable
{
    public string Path { get; }

    public TempDir(string? suffix = null)
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "dpishell-tests", Guid.NewGuid().ToString("N")[..8] + (suffix ?? ""));
        Directory.CreateDirectory(Path);
    }

    public string Sub(string name) => System.IO.Path.Combine(Path, name);

    public void Dispose()
    {
        try { if (Directory.Exists(Path)) Directory.Delete(Path, true); } catch { }
    }
}

public static class Repo
{
    /// <summary>Каталог data/ поставки: ищем вверх от выхода тестов корень репо (data/strategies.json + src/), иначе — копия в выходе.</summary>
    public static string Data
    {
        get
        {
            for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            {
                var data = System.IO.Path.Combine(d.FullName, "data");
                if (File.Exists(System.IO.Path.Combine(data, "strategies.json")) && Directory.Exists(System.IO.Path.Combine(d.FullName, "src")))
                    return data;
            }
            var local = System.IO.Path.Combine(AppContext.BaseDirectory, "data");
            if (File.Exists(System.IO.Path.Combine(local, "strategies.json"))) return local;
            throw new DirectoryNotFoundException("Не найден data/strategies.json");
        }
    }

    public static string Strategies => System.IO.Path.Combine(Data, "strategies.json");
}

public static class Native
{
    [DllImport("shell32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern IntPtr CommandLineToArgvW(string lpCmdLine, out int pNumArgs);

    [DllImport("kernel32.dll")]
    static extern IntPtr LocalFree(IntPtr hMem);

    /// <summary>Как Windows разберёт командную строку обратно в аргументы (первый — имя программы).</summary>
    public static string[] ParseCommandLine(string cmd)
    {
        var p = CommandLineToArgvW(cmd, out var n);
        if (p == IntPtr.Zero) throw new InvalidOperationException("CommandLineToArgvW");
        try
        {
            var r = new string[n];
            for (int i = 0; i < n; i++) r[i] = Marshal.PtrToStringUni(Marshal.ReadIntPtr(p, i * IntPtr.Size))!;
            return r;
        }
        finally { LocalFree(p); }
    }
}

/// <summary>
/// Минимальный HTTP-сервер на 127.0.0.1 поверх TcpListener: полный контроль над ответом
/// (врать в Content-Length, рвать соединение, молчать). HttpListener этого не умеет и на 127.0.0.1 хочет прав.
/// </summary>
public sealed class RawHttpServer : IAsyncDisposable
{
    readonly TcpListener _l;
    readonly CancellationTokenSource _cts = new();
    readonly Task _loop;
    int _requests;

    public int Port { get; }
    public int Requests => Volatile.Read(ref _requests);
    public string Url(string path = "/") => $"http://127.0.0.1:{Port}{path}";

    public RawHttpServer(Func<string, NetworkStream, CancellationToken, Task> handler)
    {
        _l = new TcpListener(IPAddress.Loopback, 0);
        _l.Start();
        Port = ((IPEndPoint)_l.LocalEndpoint).Port;
        _loop = Task.Run(async () =>
        {
            while (!_cts.IsCancellationRequested)
            {
                TcpClient c;
                try { c = await _l.AcceptTcpClientAsync(_cts.Token); }
                catch { break; }
                _ = Task.Run(async () =>
                {
                    using (c)
                    {
                        try
                        {
                            var s = c.GetStream();
                            var path = await ReadRequestAsync(s, _cts.Token);
                            Interlocked.Increment(ref _requests);
                            await handler(path, s, _cts.Token);
                            await s.FlushAsync();
                        }
                        catch { }
                    }
                });
            }
        });
    }

    static async Task<string> ReadRequestAsync(Stream s, CancellationToken ct)
    {
        var buf = new List<byte>();
        var one = new byte[1];
        while (true)
        {
            if (await s.ReadAsync(one, ct) == 0) break;
            buf.Add(one[0]);
            int n = buf.Count;
            if (n >= 4 && buf[n - 4] == '\r' && buf[n - 3] == '\n' && buf[n - 2] == '\r' && buf[n - 1] == '\n') break;
        }
        var first = Encoding.ASCII.GetString(buf.ToArray()).Split("\r\n")[0];
        var parts = first.Split(' ');
        return parts.Length > 1 ? parts[1] : "/";
    }

    public static async Task WriteHeadAsync(Stream s, int code, long? contentLength, params string[] headers)
    {
        var sb = new StringBuilder($"HTTP/1.1 {code} X\r\nConnection: close\r\n");
        if (contentLength is { } l) sb.Append($"Content-Length: {l}\r\n");
        foreach (var h in headers) sb.Append(h).Append("\r\n");
        sb.Append("\r\n");
        await s.WriteAsync(Encoding.ASCII.GetBytes(sb.ToString()));
    }

    public static async Task RespondAsync(Stream s, int code, byte[] body, params string[] headers)
    {
        await WriteHeadAsync(s, code, body.Length, headers);
        await s.WriteAsync(body);
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _l.Stop();
        try { await _loop; } catch { }
        _cts.Dispose();
    }

    /// <summary>Порт, на котором точно никто не слушает.</summary>
    public static int ClosedPort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var p = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return p;
    }
}

public static class Make
{
    public static Strategy Strategy(params (string Role, string Args)[] roles) =>
        new("t", "test", "test", roles.ToDictionary(r => r.Role, r => r.Args));

    public static AppPaths Paths(TempDir t, string root = "root") => new(t.Sub(root), Repo.Data);
}
