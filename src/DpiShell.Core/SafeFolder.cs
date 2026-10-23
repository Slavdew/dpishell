using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace DpiShell.Core;

/// <summary>
/// Проверки закрытой папки программы (<see cref="AppPaths.Harden"/>): ссылки (junction, symlink, жёсткие) и чужие владельцы.
/// Ссылки не разыменовываются: права и удаление применяются к самой записи, а не к тому, на что она указывает.
/// </summary>
static class SafeFolder
{
    static readonly SecurityIdentifier SystemSid = new(WellKnownSidType.LocalSystemSid, null);
    static readonly SecurityIdentifier Admins = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    static readonly SecurityIdentifier TrustedInstaller = new("S-1-5-80-956008885-3418522649-1831038044-1975194877-2023452543");

    /// <summary>Группы, которым в закрытой папке не место: любой пользователь машины.</summary>
    static readonly SecurityIdentifier[] Broad =
    [
        new(WellKnownSidType.BuiltinUsersSid, null), new(WellKnownSidType.AuthenticatedUserSid, null),
        new(WellKnownSidType.WorldSid, null), new(WellKnownSidType.InteractiveSid, null),
    ];

    /// <summary>Атрибуты самой записи (ссылка не разыменовывается); записи нет — null.</summary>
    public static FileAttributes? Attributes(string path)
    {
        try { return File.GetAttributes(path); }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException) { return null; }
    }

    /// <summary>Удалить ссылку — только её саму: у junction и symlink на папку Directory.Delete без рекурсии цель не трогает.</summary>
    public static void DeleteLink(string path, FileAttributes a)
    {
        if ((a & FileAttributes.Directory) != 0) Directory.Delete(path, recursive: false);
        else File.Delete(path);
    }

    /// <summary>Владелец — система, администраторы, TrustedInstaller или этот же процесс (установщик с правами, служба, тест).</summary>
    public static bool TrustedOwner(string path)
    {
        var a = Attributes(path) ?? throw new FileNotFoundException(path);
        FileSystemSecurity sec = (a & FileAttributes.Directory) != 0
            ? new DirectoryInfo(path).GetAccessControl(AccessControlSections.Owner)
            : new FileInfo(path).GetAccessControl(AccessControlSections.Owner);
        if (sec.GetOwner(typeof(SecurityIdentifier)) is not SecurityIdentifier owner) return false;
        using var me = WindowsIdentity.GetCurrent();
        return owner == SystemSid || owner == Admins || owner == TrustedInstaller || owner == me.User;
    }

    /// <summary>Нет разрешений «всем пользователям» (Пользователи, Прошедшие проверку, Все, Интерактивные), в том числе унаследованных.</summary>
    public static bool AclClosed(string dir)
    {
        var rules = new DirectoryInfo(dir).GetAccessControl().GetAccessRules(true, true, typeof(SecurityIdentifier));
        foreach (FileSystemAccessRule r in rules)
            if (r.AccessControlType == AccessControlType.Allow && r.IdentityReference is SecurityIdentifier sid && Broad.Contains(sid))
                return false;
        return true;
    }

    /// <summary>Права корня: полный доступ системе и администраторам, наследование от %ProgramData% отключено.</summary>
    public static DirectorySecurity ClosedAcl(bool setOwner)
    {
        var acl = new DirectorySecurity();
        acl.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var sid in new[] { SystemSid, Admins })
            acl.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        if (setOwner) acl.SetOwner(Admins);
        return acl;
    }

    /// <summary>
    /// Обойти папку, не заходя по ссылкам: ссылки и жёсткие ссылки на файлы — удалить; остальному — наследуемые права и
    /// владелец «Администраторы» (иначе владелец-чужой сохраняет право сменить права обратно). Запись, к которой нет доступа
    /// (чужой запрет), — удаляется; не удалилась — исключение: папку нельзя считать закрытой.
    /// </summary>
    public static void Sanitize(string dir, bool setOwner, List<string> removed)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(dir).ToList())
        {
            if (Attributes(entry) is not { } a) continue;
            if ((a & FileAttributes.ReparsePoint) != 0)
            {
                DeleteLink(entry, a);
                removed.Add(entry);
                continue;
            }
            bool isDir = (a & FileAttributes.Directory) != 0;
            if (!isDir && LinkCount(entry) > 1)
            {
                // Права на жёсткую ссылку меняют сам файл, где бы он ни лежал, — убираем только это имя.
                File.SetAttributes(entry, FileAttributes.Normal);
                File.Delete(entry);
                removed.Add(entry);
                continue;
            }
            try
            {
                // Права ставятся по хендлу самой записи; запись успела стать ссылкой или жёсткой ссылкой — убираем её, как выше.
                if (!ResetToInherited(entry, isDir, setOwner))
                {
                    if (Attributes(entry) is { } now)
                    {
                        if ((now & FileAttributes.ReparsePoint) != 0) DeleteLink(entry, now);
                        else { File.SetAttributes(entry, FileAttributes.Normal); File.Delete(entry); }
                    }
                    removed.Add(entry);
                    continue;
                }
            }
            catch (UnauthorizedAccessException)
            {
                if (isDir) Directory.Delete(entry, recursive: true);
                else { File.SetAttributes(entry, FileAttributes.Normal); File.Delete(entry); }
                removed.Add(entry);
                continue;
            }
            if (isDir) Sanitize(entry, setOwner, removed);
        }
    }

    /// <summary>
    /// Убрать ссылки до смены прав корня: Windows распространяет наследуемые права вниз по дереву, и через жёсткую ссылку
    /// они легли бы на сам файл, где бы он ни лежал. Права не меняются; папку без доступа пропускаем — её разберёт <see cref="Sanitize"/>.
    /// </summary>
    public static void RemoveLinks(string dir, List<string> removed)
    {
        List<string> entries;
        try { entries = Directory.EnumerateFileSystemEntries(dir).ToList(); }
        catch (UnauthorizedAccessException) { return; }
        foreach (var entry in entries)
        {
            if (Attributes(entry) is not { } a) continue;
            if ((a & FileAttributes.ReparsePoint) != 0) { DeleteLink(entry, a); removed.Add(entry); }
            else if ((a & FileAttributes.Directory) != 0) RemoveLinks(entry, removed);
            else if (LinkCount(entry) > 1)
            {
                File.SetAttributes(entry, FileAttributes.Normal);
                File.Delete(entry);
                removed.Add(entry);
            }
        }
    }

    /// <summary>
    /// Наследуемые права (и владелец «Администраторы») — по хендлу, открытому без разыменования ссылки (FILE_FLAG_OPEN_REPARSE_POINT):
    /// проверка «не ссылка» и смена прав относятся к одной и той же записи, подменить её junction между ними нельзя.
    /// Запись оказалась ссылкой или файлом с несколькими именами — false, права не меняются. Нет доступа — <see cref="UnauthorizedAccessException"/>.
    /// </summary>
    static bool ResetToInherited(string path, bool isDir, bool setOwner)
    {
        using var h = CreateFileW(path, ReadControl | WriteDac | (setOwner ? WriteOwner : 0), FileShare.ReadWrite | FileShare.Delete, IntPtr.Zero,
            FileMode.Open, FileFlagOpenReparsePoint | FileFlagBackupSemantics, IntPtr.Zero);
        if (h.IsInvalid) throw Fail(Marshal.GetLastWin32Error(), path);
        if (!GetFileInformationByHandle(h, out var info)) throw Fail(Marshal.GetLastWin32Error(), path);
        if ((info.FileAttributes & (uint)FileAttributes.ReparsePoint) != 0) return false;
        if (((info.FileAttributes & (uint)FileAttributes.Directory) != 0) != isDir) return false;
        if (!isDir && info.NumberOfLinks > 1) return false;

        // Пустой DACL без защиты: остаются только права, унаследованные от родителя. Не NULL — NULL DACL значит «всем всё».
        var dacl = new byte[new RawAcl(GenericAcl.AclRevision, 0).BinaryLength];
        new RawAcl(GenericAcl.AclRevision, 0).GetBinaryForm(dacl, 0);
        var owner = new byte[Admins.BinaryLength];
        Admins.GetBinaryForm(owner, 0);
        var gd = GCHandle.Alloc(dacl, GCHandleType.Pinned);
        var go = GCHandle.Alloc(owner, GCHandleType.Pinned);
        try
        {
            uint what = DaclSecurityInformation | UnprotectedDaclSecurityInformation | (setOwner ? OwnerSecurityInformation : 0);
            var err = SetSecurityInfo(h, SeFileObject, what, setOwner ? go.AddrOfPinnedObject() : IntPtr.Zero, IntPtr.Zero, gd.AddrOfPinnedObject(), IntPtr.Zero);
            if (err != 0) throw Fail((int)err, path);
        }
        finally { gd.Free(); go.Free(); }
        return true;
    }

    static Exception Fail(int err, string path) => err == 5
        ? new UnauthorizedAccessException(path)
        : new IOException($"{path}: {new System.ComponentModel.Win32Exception(err).Message}", err);

    /// <summary>Нет ссылок и записей с чужим владельцем (по ссылкам не заходим).</summary>
    public static bool TreeIsClean(string dir)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(dir))
        {
            if (Attributes(entry) is not { } a) continue;
            if ((a & FileAttributes.ReparsePoint) != 0 || !TrustedOwner(entry)) return false;
            if ((a & FileAttributes.Directory) != 0 && !TreeIsClean(entry)) return false;
        }
        return true;
    }

    /// <summary>
    /// Стереть папку целиком, не заходя по ссылкам (правило владельца об удалении): ссылка (junction, symlink) удаляется сама — то, на что она
    /// указывает, не трогается; у жёсткой ссылки удаляется только это имя. Нет папки — ничего.
    /// </summary>
    public static void DeleteTree(string dir)
    {
        if (Attributes(dir) is not { } a) return;
        if ((a & FileAttributes.ReparsePoint) != 0) { DeleteLink(dir, a); return; }
        if ((a & FileAttributes.Directory) == 0) { File.SetAttributes(dir, FileAttributes.Normal); File.Delete(dir); return; }
        foreach (var entry in Directory.EnumerateFileSystemEntries(dir).ToList())
        {
            if (Attributes(entry) is not { } e) continue;
            if ((e & FileAttributes.ReparsePoint) != 0) DeleteLink(entry, e);
            else if ((e & FileAttributes.Directory) != 0) DeleteTree(entry);
            else { if ((e & FileAttributes.ReadOnly) != 0) File.SetAttributes(entry, FileAttributes.Normal); File.Delete(entry); }
        }
        if ((a & FileAttributes.ReadOnly) != 0) File.SetAttributes(dir, a & ~FileAttributes.ReadOnly);
        Directory.Delete(dir, recursive: false);
    }

    /// <summary>Сколько имён у файла (больше одного — жёсткая ссылка). Не открылся — 1: решит дальнейшая смена прав.</summary>
    internal static uint LinkCount(string file)
    {
        // Только чтение атрибутов: такой доступ даёт и содержимое папки, даже если права самого файла его запрещают.
        using var h = CreateFileW(file, FileReadAttributes, FileShare.ReadWrite | FileShare.Delete, IntPtr.Zero, FileMode.Open,
            FileFlagOpenReparsePoint | FileFlagBackupSemantics, IntPtr.Zero);
        if (h.IsInvalid) return 1;
        return GetFileInformationByHandle(h, out var info) ? info.NumberOfLinks : 1;
    }

    const uint FileReadAttributes = 0x80, ReadControl = 0x00020000, WriteDac = 0x00040000, WriteOwner = 0x00080000;
    const uint OwnerSecurityInformation = 0x1, DaclSecurityInformation = 0x4, UnprotectedDaclSecurityInformation = 0x20000000;
    const int SeFileObject = 1;
    const uint FileFlagOpenReparsePoint = 0x00200000, FileFlagBackupSemantics = 0x02000000;

    [StructLayout(LayoutKind.Sequential)]
    struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime, LastAccessTime, LastWriteTime;
        public uint VolumeSerialNumber, FileSizeHigh, FileSizeLow, NumberOfLinks, FileIndexHigh, FileIndexLow;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern SafeFileHandle CreateFileW(string name, uint access, FileShare share, IntPtr security, FileMode mode, uint flags, IntPtr template);

    [DllImport("advapi32.dll")]
    static extern uint SetSecurityInfo(SafeFileHandle handle, int objectType, uint securityInfo, IntPtr owner, IntPtr group, IntPtr dacl, IntPtr sacl);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GetFileInformationByHandle(SafeFileHandle h, out ByHandleFileInformation info);
}
