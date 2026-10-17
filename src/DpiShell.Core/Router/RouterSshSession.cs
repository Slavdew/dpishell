using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace DpiShell.Core.Router;

// SSH к роутеру (router-api.md §5.5): сценарии через stdin, загрузка файлов, проброс -R к прокси ПК.
// Сценарии (RouterSsh) работают через интерфейс ISshSession — в тестах его заменяет подделка, настоящая — SSH.NET (docs/vetting.md «SSH.NET»).

/// <summary>Куда подключаться: адрес роутера, порт SSH, пользователь (у OpenWrt — root).</summary>
public sealed record RouterSshTarget(string Host, int Port = 22, string User = "root")
{
    /// <summary>Ключ для хранилища отпечатков: «адрес:порт».</summary>
    public string HostKeyId => $"{Host.Trim().ToLowerInvariant()}:{Port}";
}

/// <summary>Итог команды: код выхода (null — оборвалась без кода), stdout и stderr целиком.</summary>
public sealed record SshRunResult(int? ExitCode, string Stdout, string Stderr);

/// <summary>Сеанс SSH к роутеру. Всё, что нужно сценариям, — и ничего больше.</summary>
public interface ISshSession : IAsyncDisposable
{
    RouterSshTarget Target { get; }

    /// <summary>Выполнить команду; stdin (если есть) передаётся целиком и закрывается. Каждая строка stdout — в onLine по мере прихода.</summary>
    Task<SshRunResult> RunAsync(string command, Stream? stdin = null, Action<string>? onLine = null, CancellationToken ct = default);

    /// <summary>Положить файл на роутер (SFTP; без sftp-server на роутере — потоком в <c>cat</c>). Папку создаёт вызывающий.</summary>
    Task UploadAsync(Stream content, string remotePath, CancellationToken ct = default);

    /// <summary><c>ssh -R 127.0.0.1:remotePort:127.0.0.1:localPort</c> — пока не освобождён результат.</summary>
    Task<IAsyncDisposable> ForwardRemoteAsync(int remotePort, int localPort, CancellationToken ct = default);
}

public interface ISshConnector
{
    /// <summary>Подключиться и войти. Пароль — функцией, открытым живёт только на время входа.</summary>
    Task<ISshSession> ConnectAsync(RouterSshTarget target, Func<string> password, CancellationToken ct = default);
}

/// <summary>Отпечатки ключей роутеров: первое подключение запоминает, смена — ошибка (а не молча, как у ssh -o StrictHostKeyChecking=no).</summary>
public interface IRouterHostKeyStore
{
    string? Get(string hostKeyId);
    void Set(string hostKeyId, string fingerprint);
    /// <summary>Забыть ключ — пользователь подтвердил, что роутер переустановлен.</summary>
    void Remove(string hostKeyId);
}

public enum HostKeyVerdict { New, Same, Changed }

public static class HostKeyCheck
{
    /// <summary>Отпечаток в виде OpenSSH: «ssh-ed25519 SHA256:…».</summary>
    public static string Format(string keyName, string sha256Base64) => $"{keyName} SHA256:{sha256Base64}";

    public static HostKeyVerdict Decide(string? stored, string received) =>
        stored is null ? HostKeyVerdict.New : string.Equals(stored, received, StringComparison.Ordinal) ? HostKeyVerdict.Same : HostKeyVerdict.Changed;

    public const string ChangedText =
        "Ключ роутера изменился (переустановили прошивку?). Если вы сами переустанавливали OpenWrt — подтвердите новый ключ; если нет — кто-то может подменять роутер, не продолжайте.";
}

/// <summary>Хранилище отпечатков в JSON-файле (папка настроек пользователя).</summary>
public sealed class FileHostKeyStore(string path) : IRouterHostKeyStore
{
    readonly Lock _lock = new();

    Dictionary<string, string> Load()
    {
        try { return File.Exists(path) ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path)) ?? [] : []; }
        catch (Exception e) when (e is JsonException or IOException) { return []; }
    }

    void Save(Dictionary<string, string> d)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(d, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(tmp, path, true);
    }

    public string? Get(string hostKeyId) { lock (_lock) return Load().GetValueOrDefault(hostKeyId); }
    public void Set(string hostKeyId, string fingerprint) { lock (_lock) { var d = Load(); d[hostKeyId] = fingerprint; Save(d); } }
    public void Remove(string hostKeyId) { lock (_lock) { var d = Load(); if (d.Remove(hostKeyId)) Save(d); } }
}

public enum RouterSshFailure
{
    /// <summary>Роутер не ответил по SSH (выключен SSH, не тот адрес, таймаут).</summary>
    Unreachable,
    /// <summary>Неверный пароль.</summary>
    AuthFailed,
    /// <summary>Отпечаток ключа не совпал с запомненным.</summary>
    HostKeyChanged,
    /// <summary>Команда или сценарий не выполнились.</summary>
    Failed,
    /// <summary>Ответ сценария не по договору.</summary>
    Protocol,
}

public sealed class RouterSshException(RouterSshFailure failure, string message, Exception? inner = null) : Exception(message, inner)
{
    public RouterSshFailure Failure { get; } = failure;
}

/// <summary>Настоящее подключение — SSH.NET. Пароль и аутентификация «password» + «keyboard-interactive» (dropbear OpenWrt умеет оба); пустой пароль свежей OpenWrt — «none».</summary>
public sealed class SshNetConnector(IRouterHostKeyStore hostKeys) : ISshConnector
{
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(10);

    public async Task<ISshSession> ConnectAsync(RouterSshTarget target, Func<string> password, CancellationToken ct = default)
    {
        var pw = password();
        var auth = new List<AuthenticationMethod>();
        if (pw.Length == 0) auth.Add(new NoneAuthenticationMethod(target.User));
        auth.Add(new PasswordAuthenticationMethod(target.User, pw));
        var kbd = new KeyboardInteractiveAuthenticationMethod(target.User);
        kbd.AuthenticationPrompt += (_, e) => { foreach (var p in e.Prompts) p.Response = pw; };
        auth.Add(kbd);
        var info = new ConnectionInfo(target.Host, target.Port, target.User, auth.ToArray()) { Timeout = Timeout };

        var stored = hostKeys.Get(target.HostKeyId);
        string? received = null;
        var ssh = new SshClient(info);
        ssh.HostKeyReceived += (_, e) =>
        {
            received = HostKeyCheck.Format(e.HostKeyName, e.FingerPrintSHA256);
            e.CanTrust = HostKeyCheck.Decide(stored, received) != HostKeyVerdict.Changed;
        };
        try
        {
            await ssh.ConnectAsync(ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            ssh.Dispose();
            foreach (var a in auth) (a as IDisposable)?.Dispose();
            if (received is not null && HostKeyCheck.Decide(stored, received) == HostKeyVerdict.Changed)
                throw new RouterSshException(RouterSshFailure.HostKeyChanged, HostKeyCheck.ChangedText, e);
            throw e switch
            {
                SshAuthenticationException => new RouterSshException(RouterSshFailure.AuthFailed, "Неверный пароль роутера (SSH)", e),
                SocketException or SshOperationTimeoutException or SshConnectionException or TimeoutException =>
                    new RouterSshException(RouterSshFailure.Unreachable, $"Роутер {target.Host} не отвечает по SSH (порт {target.Port})", e),
                _ => new RouterSshException(RouterSshFailure.Failed, "Не удалось подключиться к роутеру по SSH: " + e.Message, e),
            };
        }
        // Первое подключение — запомнить ключ (вход прошёл: это тот роутер, к которому пользователь знает пароль).
        if (stored is null && received is not null) hostKeys.Set(target.HostKeyId, received);
        return new SshNetSession(target, ssh, info, stored ?? received, auth);
    }
}

sealed class SshNetSession(RouterSshTarget target, SshClient ssh, ConnectionInfo info, string? hostKey, List<AuthenticationMethod> auth) : ISshSession
{
    public RouterSshTarget Target => target;

    public async Task<SshRunResult> RunAsync(string command, Stream? stdin = null, Action<string>? onLine = null, CancellationToken ct = default)
    {
        using var cmd = ssh.CreateCommand(command);
        Task exec;
        try { exec = cmd.ExecuteAsync(ct); }
        catch (SshException e) { throw new RouterSshException(RouterSshFailure.Failed, "Связь с роутером по SSH прервалась", e); }
        var stdout = ReadLinesAsync(cmd.OutputStream, onLine, ct);
        var stderr = new StreamReader(cmd.ExtendedOutputStream).ReadToEndAsync(ct);
        if (stdin is not null)
        {
            using var input = cmd.CreateInputStream();
            await stdin.CopyToAsync(input, ct);
        }
        try { await exec; }
        catch (SshException e) { throw new RouterSshException(RouterSshFailure.Failed, "Связь с роутером по SSH прервалась", e); }
        return new SshRunResult(cmd.ExitStatus, await stdout, await stderr);
    }

    static async Task<string> ReadLinesAsync(Stream s, Action<string>? onLine, CancellationToken ct)
    {
        var all = new System.Text.StringBuilder();
        using var r = new StreamReader(s, System.Text.Encoding.UTF8);
        while (await r.ReadLineAsync(ct) is { } line)
        {
            all.Append(line).Append('\n');
            onLine?.Invoke(line);
        }
        return all.ToString();
    }

    public async Task UploadAsync(Stream content, string remotePath, CancellationToken ct = default)
    {
        var start = content.CanSeek ? content.Position : -1;
        using (var sftp = new SftpClient(info))
        {
            // Тот же ключ, что у основного сеанса, — иначе это уже другой сервер.
            sftp.HostKeyReceived += (_, e) => e.CanTrust = HostKeyCheck.Format(e.HostKeyName, e.FingerPrintSHA256) == hostKey;
            bool sftpOk;
            try { await sftp.ConnectAsync(ct); sftpOk = true; }
            // У OpenWrt по умолчанию dropbear без sftp-server (пакет openssh-sftp-server) — тогда потоком в cat.
            catch (SshException) { sftpOk = false; }
            if (sftpOk)
            {
                await sftp.UploadFileAsync(content, remotePath, ct);
                return;
            }
        }
        if (start >= 0) content.Position = start;
        var tmp = remotePath + ".part";
        var r = await RunAsync($"cat > {Sh.Quote(tmp)} && mv -f {Sh.Quote(tmp)} {Sh.Quote(remotePath)}", content, null, ct);
        if (r.ExitCode != 0) throw new RouterSshException(RouterSshFailure.Failed, $"Не удалось записать {remotePath} на роутер: {r.Stderr.Trim()}");
    }

    public Task<IAsyncDisposable> ForwardRemoteAsync(int remotePort, int localPort, CancellationToken ct = default)
    {
        var fp = new ForwardedPortRemote(IPAddress.Loopback, (uint)remotePort, IPAddress.Loopback, (uint)localPort);
        ssh.AddForwardedPort(fp);
        try { fp.Start(); }
        catch (Exception e) when (e is SshException or InvalidOperationException)
        {
            ssh.RemoveForwardedPort(fp);
            fp.Dispose();
            throw new RouterSshException(RouterSshFailure.Failed, $"Роутер не открыл порт {remotePort} для туннеля", e);
        }
        return Task.FromResult<IAsyncDisposable>(new Forward(ssh, fp));
    }

    sealed class Forward(SshClient ssh, ForwardedPortRemote fp) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            try { fp.Stop(); } catch (Exception e) when (e is SshException or ObjectDisposedException or InvalidOperationException) { }
            try { ssh.RemoveForwardedPort(fp); } catch (ObjectDisposedException) { }
            fp.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    public ValueTask DisposeAsync()
    {
        try { ssh.Disconnect(); } catch (Exception e) when (e is SshException or ObjectDisposedException or SocketException) { }
        ssh.Dispose();
        foreach (var a in auth) (a as IDisposable)?.Dispose();
        return ValueTask.CompletedTask;
    }
}

/// <summary>Строки для sh.</summary>
public static class Sh
{
    /// <summary>Одинарные кавычки; внутренняя кавычка — <c>'\''</c>.</summary>
    public static string Quote(string s) => "'" + s.Replace("'", "'\\''") + "'";
}
