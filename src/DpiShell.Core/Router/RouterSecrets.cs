using System.Security.Cryptography;
using System.Text;

namespace DpiShell.Core.Router;

/// <summary>
/// Пароль роутера на диске — только зашифрованным DPAPI (CurrentUser): расшифровать может лишь этот пользователь Windows
/// на этом компьютере. Открытый пароль живёт в памяти только на время входа; в логи, отчёты и тексты ошибок не попадает.
/// </summary>
public static class RouterSecrets
{
    // Дополнительная «соль» DPAPI: чужая программа того же пользователя без неё не расшифрует случайно.
    static readonly byte[] Entropy = Encoding.UTF8.GetBytes("DpiShell.Router.v1");

    public static string Protect(string password) =>
        Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(password), Entropy, DataProtectionScope.CurrentUser));

    /// <summary>null — пароля нет или его нельзя расшифровать (другой пользователь/компьютер): тогда спросить заново.</summary>
    public static string? Unprotect(string? protectedPassword)
    {
        if (string.IsNullOrEmpty(protectedPassword)) return null;
        try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(protectedPassword), Entropy, DataProtectionScope.CurrentUser)); }
        catch (Exception e) when (e is CryptographicException or FormatException) { return null; }
    }
}
