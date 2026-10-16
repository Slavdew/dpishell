namespace DpiShell.Core;

/// <summary>Где что лежит. Корень — %ProgramData%\DpiShell: служба читает файлы без профиля пользователя.</summary>
public sealed class AppPaths
{
    public string Root { get; }
    public string Core => Path.Combine(Root, "core");
    public string Lists => Path.Combine(Root, "lists");
    public string Fake => Path.Combine(Root, "fake");
    public string Logs => Path.Combine(Root, "logs");
    public string Results => Path.Combine(Root, "results");
    public string StateFile => Path.Combine(Root, "state.json");
    public string Winws => Path.Combine(Core, "winws.exe");

    /// <summary>Данные, приехавшие вместе с программой (data\ рядом с exe).</summary>
    public string Seed { get; }

    public AppPaths(string? root = null, string? seed = null)
    {
        Root = root ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "DpiShell");
        Seed = seed ?? Path.Combine(AppContext.BaseDirectory, "data");
    }

    public void EnsureDirectories()
    {
        foreach (var d in new[] { Root, Core, Lists, Fake, Logs, Results })
            Directory.CreateDirectory(d);
    }
}
