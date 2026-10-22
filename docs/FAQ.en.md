# DpiShell: FAQ

[Русский](FAQ.md) · [README](../README.md#english) · [Windows](windows/README.md) · [Router](router/README.md) (the detailed guides are in Russian)

Results depend on the ISP, the network and the device: "usually" and "on most" below mean exactly that.

- [A site does not open](#a-site-does-not-open)
- [A game or program cannot connect](#a-game-or-program-cannot-connect)
- [Router: install, update, watchdog, emergency button](#router)
- [Updates](#updates)
- [What to send if something does not work](#what-to-send-if-something-does-not-work)
- [Router security](#router-security-for-reference)
- [How to restore the previous state](#how-to-restore-the-previous-state)
- [Version compatibility](#version-compatibility)

## A site does not open

1. **Check the site.** On "Home", "Check a site: with and without optimization": enter the address. The app checks both ways and shows where it opens. On the router page the same is "Site does not open".
2. **Find a strategy.** YouTube, Discord or another blocked site does not open: "Find strategy" (on the router, "Find strategy" on its page). It usually takes a few minutes and sites may drop out meanwhile. A finished search turns on the best strategy by itself. Did not help: "Search deeper" (appears after the result, up to 15 minutes).
3. **Exclusions.** The site worked without DpiShell but not with it (bank, government site): "Add to exclusions" in the same "Check a site", or "Sites" > "Exclusions". Optimization leaves such sites alone. Still nothing: remove the site from exclusions.
4. **Optimization list.** Optimization applies to sites in the lists (YouTube, Discord, "My sites"). If your site is not there, add it in "Sites" > "My sites". On the router in "All sites" mode, optimization covers everything except exclusions.
5. **An address does not open even with optimization** (GitHub downloads, Telegram Web, Discord start-up): "Network" > "Fix addresses". The ISP spoofs DNS answers: "Network" > DNS. Both can be undone, see [How to restore the previous state](#how-to-restore-the-previous-state).
6. **A VPN or a similar program is on** (Flowseal, another zapret, GoodbyeDPI): two optimizations together break sites. "Diagnostics" shows what interferes.

Still no luck: [what to send](#what-to-send-if-something-does-not-work).

## A game or program cannot connect

1. "Games & apps" > "Set up for a game": "Start", launch the game, get to the point that fails, "Done". The app picks parameters only for the game's addresses and ports and asks whether it helped. If not, it tries others, then restores the previous state.
2. Games with anti-cheat (Vanguard, FACEIT, Easy Anti-Cheat) may not work with the capture driver needed for recording; advanced users have a driver-free recording (TCP only).
3. Game mode and "All addresses" are for programs that do not reveal a site name. Both are off by default. A game, VPN or remote desktop started to misbehave: turn them off in "Games & apps" > "IP lists" > "Off" (this also turns off game mode); for "All addresses for 10 minutes" use "Revert now".
4. The game runs worse than without DpiShell: turn optimization off while playing. The result for each game cannot be guaranteed.

## Router

**Install.** A router with OpenWrt 25.x (`apk` packages, fw4 firewall) is required. In the PC app: "Router" > "Install on router…", the router address and the administrator password (usually the same as for its settings page). The app checks whether the router is suitable and installs the packages. If it is not (low memory, no `kmod-nft-queue` for the kernel, OpenWrt 24.10 or older), the reason is shown and the install does not proceed. Installing OpenWrt itself is the user's responsibility. Manual steps: [docs/router/README.md](router/README.md) (Russian).

**Turning on.** After "Turn on" from the PC app the router waits 3 minutes for "Yes, keep". No answer: optimization goes back to how it was.

**Update.** The router checks for a new version once a day; its page then shows an "Update available" banner with an "Update" button. From the PC app: "Router" > "Update"; if the router has no access to GitHub, the computer passes the packages. Update the router and the app together, see [compatibility](#version-compatibility).

**Watchdog.** Every 5 minutes the router checks ordinary sites (VK, RBC, Gosuslugi). If they do not open with optimization but open without it, optimization is turned off, the page shows the reason, and the router searches for a strategy once by itself. If none is found, optimization stays off until you turn it on or start a search. If there is no internet even without optimization, it is most likely an ISP outage and optimization stays as it was.

**Emergency button.** The router page does not open and sites do not work: on most routers a 2-second press of reset turns optimization off without a reboot; the WPS button, if present, usually turns it off with one press. Yours may behave differently. **Do not hold reset for 5 seconds or longer: that is usually a factory reset.** If the router rebooted, it comes up already without optimization. Turn it on again from the router page or the PC app.

## Updates

- **PC app:** "About & updates" > "Check for updates" > "Update to …". The file is verified against the release signature; administrator rights are usually not needed (if the helper is updated, Windows asks once). After the update "Restore version …" is available. No button (for example, the signature did not match): download the archive from the [releases page](https://github.com/Slavdew/dpishell/releases), unpack over the program, leave the `data` folder alone.
- **Lists and data:** checked daily, installed with a button on the same tab.
- **Router:** see above. Keep the app and the router on the same release.

## What to send if something does not work

1. "Diagnostics" > **"Save report"**: one text file you can read before sending. Addresses and names of the PC and network, proxy addresses, passwords and tokens are hidden in it; your own sites and exclusions are shown as a count. On the router page: "Diagnostics" > "Save report" (your addresses are hidden).
2. If an error has "Details" or "Copy", attach that text: it contains versions and the action, with personal data hidden as in the report.
3. Describe what you did and what you saw, a window screenshot helps. Where: [issues](https://github.com/Slavdew/dpishell/issues) (in the app: "Report a problem").

Do not send raw logs from the logs folder: addresses are not hidden in them.

## Router security (for reference)

DpiShell does not change the router's security settings itself; below is what is worth a look. What to do about it is up to the router's owner. See the OpenWrt and firmware documentation for details.

- **Administrator (root) password:** set, not default, not empty.
- **Access to the admin page and SSH:** from the home network only; closed from the internet (WAN).
- **SSH key login** instead of a password.
- **HTTPS for the admin page.** If the page has HTTPS (a redirect from HTTP or port 443), the PC app switches to it by itself and remembers the certificate at the first sign-in. If the certificate changes, the app checks it over SSH or asks "Trust new". If a router that used to answer over HTTPS now answers only over HTTP, the app asks before sending the password in clear text. Without HTTPS the sign-in goes over HTTP and the password crosses the home network unencrypted: a risk where there are foreign devices (guest Wi-Fi, a dormitory).
- **Wi-Fi:** WPA2/WPA3 with your own password; turn WPS off if unused; keep guests on a separate network.
- **UPnP** lets programs open ports by themselves; turn it off if not needed.
- **Updates:** keep the firmware and packages up to date.
- **DNS:** DNS rebind protection is usually on by default in OpenWrt; check it was not turned off.

## How to restore the previous state

First the way from the app. If there is no app, no internet, or the program does not open, use the second way: it needs neither the app nor internet access.

**Simplest.** "Settings" > "DpiShell changes" > "Undo all": restores from the journal what DpiShell changed on the PC (service, DNS, system proxy, hosts, TCP timestamps, program profiles, autostart, DNS and optimization on the router). Values from before DpiShell come back; your later edits are left alone. The report shows what was restored, what was already as before, what was skipped and what failed. A failed item stays in the list: remove the cause (for example, close the app that locked hosts) and click "Undo all" again.

**One item.** In the same list an item has "Undo": only it is restored, the rest stays. No button means a reason is written next to the item (for example, a later item of the same kind exists, or the optimization service needs the item; then use "Undo all").

**Settings from a friend.** "Undo import" is in the import window right after the import and on the import item in "DpiShell changes". Sites, strategies, profiles, hosts and DNS return to what they were before the import; the service and other changes stay. A helper of this version is required (an old one can be updated in "About & updates"); otherwise an import can only be undone together with everything else, with "Undo all".

**Remove everything with one button.** "Settings" > "Cleanup and removal" > "Remove DpiShell from this PC…": step by step it undoes the changes, removes the service and the helper with the program data, then shows which folders are left to delete by hand. If a step fails the app stops there; repeat with the same button.

Commands below run as administrator (on the PC: Command Prompt or PowerShell "Run as administrator"; on the router: `ssh root@192.168.1.1`, your address may differ). If Windows will not start normally or the network does not work at all, boot into Safe Mode with Networking: DpiShell services usually do not start there.

### PC

| Feature | From the app | Without the app and without internet |
|---|---|---|
| **Optimization on the PC** | "Home" > "Turn off" | `sc stop DpiShell`; to keep it from starting after a reboot: `sc config DpiShell start= demand` |
| **PC DNS** ("Network" > DNS) | "Network" > DNS > "Revert" | Settings > "Network & Internet" > adapter > "DNS server assignment" > "Automatic (DHCP)". Or `netsh interface ip set dnsservers name="Ethernet" source=dhcp` and the same for `ipv6` (adapter name from `netsh interface show interface`), then `ipconfig /flushdns`. If you had your own addresses before DpiShell, set them back |
| **hosts** ("Fix addresses", "My lines", foreign lines commented out) | "Network" > "Fix addresses" > "Revert" at the top of the section | Open `C:\Windows\System32\drivers\etc\hosts` in Notepad as administrator. Delete the lines from `# DpiShell begin` to `# DpiShell end` and from `# DpiShell user begin` to `# DpiShell user end`. For lines like `# DpiShell off 2026-10-22: <line>` remove the prefix `# DpiShell off date: `. Copies are in `%ProgramData%\DpiShell\hosts` (`hosts.original.bak` is the file as it was before the first edit); then `ipconfig /flushdns` |
| **Proxy** (your list, "Turn on selected") | "Network" > "Proxy" > "Restore previous" or "Clear" | Settings > "Network & Internet" > "Proxy" > turn off. By command: for the user `reg add "HKCU\Software\Microsoft\Windows\CurrentVersion\Internet Settings" /v ProxyEnable /t REG_DWORD /d 0 /f`; for the machine the same with `HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Internet Settings`; for services `netsh winhttp reset proxy`. For the whole machine DpiShell also sets the policy `ProxySettingsPerUser=0` in `HKLM\SOFTWARE\Policies\Microsoft\Windows\CurrentVersion\Internet Settings`; if it was not there before, delete the value |
| **Joint mode** | "Router" > "Joint mode" > "Off" (or "Stop waiting for router") | Turn optimization off on the PC (first row): the computer then works separately from the router. A link on the router that is not renewed lasts 24 hours and expires by itself |
| **Game mode, IP lists, "All addresses"** | "Games & apps" > "IP lists" > "Off" (also turns off game mode); "All addresses for 10 minutes": "Revert now", they also end by themselves after 10 minutes | Turn optimization off (first row): there is no separate command for the modes |
| **Game profile** | "Games & apps" > the profile's "Revert" | Turn optimization off (first row); delete the profile later in the app |
| **Per-site settings** | "Sites" > "Per-site settings" > "Revert" | Turn optimization off (first row) |
| **Imported settings** | "Undo import" (in the import window or on the item in "DpiShell changes"; a helper of this version is required), otherwise "Settings" > "DpiShell changes" > "Undo all" (restores everything at once, including the service and DNS), or remove what was imported by hand: sites, exclusions, strategies on their tabs, "My lines" in hosts, DNS | For hosts and DNS see the rows above; sites, exclusions and strategies cannot be edited without the app |
| **TCP timestamps** (DpiShell turns them on if they were off) | restored by "Undo all" and "Remove optimization service" | `netsh int tcp set global timestamps=disabled`, only if they were off before DpiShell |
| **Service, driver, helper** | "Settings" > "Cleanup and removal": "Remove optimization service", "Remove helper" | `sc stop DpiShell`, `sc delete DpiShell`, `sc stop WinDivert`, `sc delete WinDivert`; helper: `sc stop DpiShellHelper`, `sc delete DpiShellHelper` |
| **Full removal** | "Settings" > "Cleanup and removal" > "Remove DpiShell from this PC…" (step by step: "Undo all", the service, the helper with program data). Or by hand: "Settings" > "DpiShell changes" > "Undo all"; then "Cleanup and removal". The program folder and `%LocalAppData%\DpiShell` remain: close the program (tray icon > "Exit") | the `sc` commands above, then delete the folders: the program's, `%LocalAppData%\DpiShell`, `%ProgramData%\DpiShell`. Autostart: Task Manager > "Startup". The app does not delete the program folder or `%LocalAppData%DpiShell`. Details: [docs/windows/README.md](windows/README.md#удаление-полностью) (Russian) |

### Router

| Feature | From the page or the app | Without the page and without the app |
|---|---|---|
| **Optimization on the router** | "Turn off" on the router page or on the app's "Router" tab | The button on the router: reset for 2 seconds or WPS (above). Over SSH: `ubus call dpishell disable` or `/usr/libexec/dpishell/off button` |
| **Turning on was not confirmed** | "Yes, keep" within 3 minutes | Nothing: after 3 minutes the router reverts by itself |
| **Watchdog turned optimization off** | "Turn on" (or "Find strategy") | Nothing needed: it is off. The reason is on the router page |
| **Network DNS** | "DNS" on the router page (in the app, the "Router" tab) > "Revert" | `ubus call dpishell dns_restore`. If that does not respond: `uci -q delete dhcp.@dnsmasq[0].noresolv`, `uci -q delete dhcp.@dnsmasq[0].server`, `uci commit dhcp`, `/etc/init.d/https-dns-proxy stop`, `/etc/init.d/dnsmasq restart` (the earlier values, if any, are in `/etc/dpishell/dns-backup.json`). For one PC right away, set DNS by hand (the "PC DNS" row above) |
| **Joint mode** | on the PC: "Router" > "Joint mode" > "Off" | Turn optimization off on the PC; the link on the router expires after 24 hours without renewal |
| **Per-site settings** ("advanced mode") | "Revert" on the banner | `ubus call dpishell advanced_revert` |
| **Imported settings** | import the `dpishell-before-import-*` file the page saved before the import, with "Replace mine" | Not possible without the page; turn optimization off (first row) |
| **Network acceleration** ("Fix" button) | restored when DpiShell is removed | On removal (last row) |
| **Router update** | there is no update rollback; if the new version gets in the way, turn optimization off, or remove DpiShell keeping settings and install the needed version from the `router-vX.Y.Z` feed on the [releases page](https://github.com/Slavdew/dpishell/releases) | `apk del luci-app-dpishell dpishell` (keeps settings), then install again: [docs/router/README.md](router/README.md#установка-из-фида) (Russian) |
| **Full removal** | "Diagnostics" > "DpiShell removal" (or "Uninstall DpiShell…" in the app): "Keep settings" or "Remove completely", type the word shown | `apk del luci-app-dpishell dpishell` does the same cleanup keeping settings. Completely, with settings: `cp /usr/libexec/dpishell/uninstall.sh /tmp/u.sh && sh /tmp/u.sh --full` |

Removal puts network acceleration, DNS and the schedule back. If you changed them yourself later, those edits may be overwritten. Kernel modules and other packages stay.

The router does not respond at all (neither the page nor SSH): OpenWrt has a failsafe mode and a factory reset, the method depends on the model: [openwrt.org/docs/guide-user/troubleshooting/failsafe_and_factory_reset](https://openwrt.org/docs/guide-user/troubleshooting/failsafe_and_factory_reset). A factory reset wipes all router settings, not only DpiShell's.

## Version compatibility

Versions of one release are guaranteed to work together: the PC app, the helper and the router packages. If you leave one of them without updates for long (for example the PC app) while updating the other (the router), full operation is not guaranteed: update both. Compatibility is kept where possible: an older app should be able to update a newer router and vice versa.
