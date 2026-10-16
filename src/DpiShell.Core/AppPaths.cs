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

    /// <summary>
    /// Доступ к папке программы — только системе и администраторам (по SID, на любом языке Windows).
    /// По умолчанию в %ProgramData% пользователи могут читать и писать: это раскрывало свои адреса и позволяло
    /// подменить winws.exe, который служба запускает от имени системы после перезагрузки (без нашей проверки хешей).
    /// Наследование от %ProgramData% отключается; вложенные папки и файлы получают те же права.
    /// </summary>
    public void Harden()
    {
        EnsureDirectories();
        var system = new System.Security.Principal.SecurityIdentifier(System.Security.Principal.WellKnownSidType.LocalSystemSid, null);
        var admins = new System.Security.Principal.SecurityIdentifier(System.Security.Principal.WellKnownSidType.BuiltinAdministratorsSid, null);
        var acl = new System.Security.AccessControl.DirectorySecurity();
        acl.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var sid in new[] { system, admins })
            acl.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(sid,
                System.Security.AccessControl.FileSystemRights.FullControl,
                System.Security.AccessControl.InheritanceFlags.ContainerInherit | System.Security.AccessControl.InheritanceFlags.ObjectInherit,
                System.Security.AccessControl.PropagationFlags.None, System.Security.AccessControl.AccessControlType.Allow));
        if (IsElevated()) acl.SetOwner(admins);
        new DirectoryInfo(Root).SetAccessControl(acl);
        // Файлы и папки, созданные до этого, могли получить явные права — сбрасываем на наследуемые.
        foreach (var f in Directory.EnumerateFileSystemEntries(Root, "*", SearchOption.AllDirectories))
        {
            if (Directory.Exists(f))
            {
                var ds = new System.Security.AccessControl.DirectorySecurity();
                ds.SetAccessRuleProtection(false, false);
                new DirectoryInfo(f).SetAccessControl(ds);
            }
            else
            {
                var fs = new System.Security.AccessControl.FileSecurity();
                fs.SetAccessRuleProtection(false, false);
                new FileInfo(f).SetAccessControl(fs);
            }
        }
    }

    static bool IsElevated()
    {
        using var id = System.Security.Principal.WindowsIdentity.GetCurrent();
        return new System.Security.Principal.WindowsPrincipal(id).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
    }

    /// <summary>Закрыта ли папка от обычных пользователей (для «Диагностики» и тестов).</summary>
    public bool IsHardened()
    {
        if (!Directory.Exists(Root)) return false;
        var users = new System.Security.Principal.SecurityIdentifier(System.Security.Principal.WellKnownSidType.BuiltinUsersSid, null);
        var auth = new System.Security.Principal.SecurityIdentifier(System.Security.Principal.WellKnownSidType.AuthenticatedUserSid, null);
        var everyone = new System.Security.Principal.SecurityIdentifier(System.Security.Principal.WellKnownSidType.WorldSid, null);
        var rules = new DirectoryInfo(Root).GetAccessControl().GetAccessRules(true, true, typeof(System.Security.Principal.SecurityIdentifier));
        foreach (System.Security.AccessControl.FileSystemAccessRule r in rules)
            if (r.IdentityReference is System.Security.Principal.SecurityIdentifier sid && (sid == users || sid == auth || sid == everyone)
                && r.AccessControlType == System.Security.AccessControl.AccessControlType.Allow)
                return false;
        return true;
    }
}
