using System.Runtime.InteropServices;

namespace DpiShell.Core;

/// <summary>
/// Прокси WinHTTP по умолчанию (его берут службы и часть программ; то же, что <c>netsh winhttp show/set proxy</c>).
/// Реальная запись требует прав администратора — её делает помощник.
/// </summary>
public interface IWinHttpProxy
{
    WinHttpInfo Get();
    /// <summary>Поставить постоянный прокси (или прямое соединение для <see cref="WinHttpInfo.Direct"/>). false — Windows отказала.</summary>
    bool Set(WinHttpInfo info);
}

/// <summary>
/// WinHttpGet/SetDefaultProxyConfiguration — документированный API (в отличие от двоичного значения WinHttpSettings в реестре, формат
/// которого Microsoft не описывает и который меняется между версиями); сам он сохраняет значение в реестр, как это делает netsh.
/// Ограничение API: только постоянный прокси и исключения (автонастройка и проверка подлинности не поддерживаются) — этого нам и достаточно.
/// </summary>
public sealed class WindowsWinHttpProxy : IWinHttpProxy
{
    const uint DefaultProxy = 0, NamedProxy = 3;

    [StructLayout(LayoutKind.Sequential)]
    struct ProxyInfo
    {
        public uint AccessType;
        public IntPtr Proxy;
        public IntPtr Bypass;
    }

    [DllImport("winhttp.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool WinHttpGetDefaultProxyConfiguration(ref ProxyInfo info);

    [DllImport("winhttp.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool WinHttpSetDefaultProxyConfiguration(ref ProxyInfo info);

    [DllImport("kernel32.dll")]
    static extern IntPtr GlobalFree(IntPtr mem);

    public WinHttpInfo Get()
    {
        SystemProxyGuard.Check("чтение WinHTTP");
        var info = new ProxyInfo();
        try
        {
            if (!WinHttpGetDefaultProxyConfiguration(ref info)) return WinHttpInfo.Direct;
            var proxy = info.Proxy == IntPtr.Zero ? null : Marshal.PtrToStringUni(info.Proxy);
            var bypass = info.Bypass == IntPtr.Zero ? null : Marshal.PtrToStringUni(info.Bypass);
            return string.IsNullOrEmpty(proxy) ? WinHttpInfo.Direct : new(proxy, string.IsNullOrEmpty(bypass) ? null : bypass);
        }
        finally
        {
            if (info.Proxy != IntPtr.Zero) GlobalFree(info.Proxy);
            if (info.Bypass != IntPtr.Zero) GlobalFree(info.Bypass);
        }
    }

    public bool Set(WinHttpInfo value)
    {
        SystemProxyGuard.Check("запись WinHTTP");
        var info = new ProxyInfo { AccessType = value.IsDirect ? DefaultProxy : NamedProxy };
        try
        {
            if (!value.IsDirect)
            {
                info.Proxy = Marshal.StringToHGlobalUni(value.Proxy);
                if (value.Bypass is not null) info.Bypass = Marshal.StringToHGlobalUni(value.Bypass);
            }
            return WinHttpSetDefaultProxyConfiguration(ref info);
        }
        finally
        {
            if (info.Proxy != IntPtr.Zero) Marshal.FreeHGlobal(info.Proxy);
            if (info.Bypass != IntPtr.Zero) Marshal.FreeHGlobal(info.Bypass);
        }
    }
}

public sealed class MemoryWinHttpProxy : IWinHttpProxy
{
    public WinHttpInfo Value { get; set; } = WinHttpInfo.Direct;
    public bool Refuse { get; set; }
    public WinHttpInfo Get() => Value;
    public bool Set(WinHttpInfo info)
    {
        if (Refuse) return false;
        Value = info;
        return true;
    }
}

/// <summary>Сообщить системе, что прокси изменён: WinINET перечитает настройки без перезапуска программ.</summary>
public interface IProxyNotifier
{
    void Changed();
}

public sealed class NullProxyNotifier : IProxyNotifier
{
    public int Calls { get; private set; }
    public void Changed() => Calls++;
}

/// <summary>InternetSetOption(NULL, INTERNET_OPTION_SETTINGS_CHANGED), затем INTERNET_OPTION_REFRESH.</summary>
public sealed class WininetNotifier : IProxyNotifier
{
    const int SettingsChanged = 39, Refresh = 37;

    [DllImport("wininet.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool InternetSetOptionW(IntPtr handle, int option, IntPtr buffer, int length);

    public void Changed()
    {
        SystemProxyGuard.Check("InternetSetOption");
        try
        {
            InternetSetOptionW(IntPtr.Zero, SettingsChanged, IntPtr.Zero, 0);
            InternetSetOptionW(IntPtr.Zero, Refresh, IntPtr.Zero, 0);
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException) { }
    }
}
