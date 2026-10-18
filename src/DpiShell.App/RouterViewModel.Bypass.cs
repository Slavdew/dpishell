using DpiShell.Core.Router;

namespace DpiShell.App;

/// <summary>
/// Где «живёт» обход (router-api §6). Роутер ответил «включён» — окно запоминает его (адрес и MAC, <see cref="WindowSettings.RouterBypass"/>),
/// «выключен» — забывает. При старте окна это решает, оставлять ли обход на ПК, пока роутер не отвечает (обновление, перезагрузка):
/// тот же роутер — обход ПК не нужен; другая сеть (ноутбук в гостях) — обход ПК не трогаем.
/// </summary>
public sealed partial class RouterViewModel
{
    /// <summary>MAC устройства в своей сети по ARP; null — не узнать. В тестах — подмена.</summary>
    public Func<string, string?> MacOf { get; set; } = LanIdentity.Mac;

    /// <summary>Обход в последний раз видели включённым на роутере.</summary>
    public bool BypassRemembered => _settings.RouterBypass is not null;

    string? _macTried;

    /// <summary>По каждому ответу роутера: «включён» — запомнить роутер, «выключен» (не во время обновления) — забыть.</summary>
    void RememberBypass()
    {
        if (!Found || _detection is not { } d || _status is null && d.Status is null) return;
        if (RouterEnabled)
        {
            var mark = _settings.RouterBypass;
            if (mark?.Address == d.Address && (mark.Mac is not null || _macTried == d.Address)) return;
            if (mark?.Address != d.Address)
            {
                _settings.RouterBypass = new RouterBypassMark(d.Address, null);
                TrySave();
            }
            _macTried = d.Address;
            _ = RememberMacAsync(d.Address);
        }
        else if (!Updating && _settings.RouterBypass is not null) ForgetBypass();
    }

    async Task RememberMacAsync(string address)
    {
        string? mac;
        try { mac = await Task.Run(() => MacOf(address)); }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { return; }
        if (mac is null || _settings.RouterBypass?.Address != address) return;
        _settings.RouterBypass = _settings.RouterBypass with { Mac = mac };
        TrySave();
    }

    /// <summary>Человек выбрал обход на ПК (роутер не отвечал) или DpiShell с роутера удалён — обход больше не «живёт» на роутере.</summary>
    public void ForgetBypass()
    {
        if (_settings.RouterBypass is null) return;
        _settings.RouterBypass = null;
        TrySave();
    }

    /// <summary>
    /// Обход должен работать на роутере, а роутер сейчас не отвечает: в последний раз он был включён, и это тот же роутер —
    /// тот же адрес и тот же MAC. MAC не узнать — не уверены (может быть чужая сеть с тем же адресом шлюза): false.
    /// </summary>
    public async Task<bool> BypassExpectedButAbsentAsync()
    {
        if (Found || _settings.RouterBypass is not { Mac: { } mac } mark) return false;
        string? address;
        try { address = AddressAuto ? Gateway() : CustomAddress.Trim(); }
        catch (Exception e) when (e is InvalidOperationException or System.Net.NetworkInformation.NetworkInformationException) { return false; }
        if (address != mark.Address) return false;
        try { return await Task.Run(() => MacOf(address)) == mac; }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { return false; }
    }
}
