# AmneziaGeo

**English** | [Русский](README.ru.md)

![platform](https://img.shields.io/badge/platform-Windows%20%7C%20Linux%20%7C%20Android-0078D6)
![engine](https://img.shields.io/badge/engine-AmneziaWG-2E7D32)
![license](https://img.shields.io/badge/license-GPL--3.0-blue)

AmneziaGeo is a cross-platform VPN client for Windows, Linux and Android, built on AmneziaWG - a WireGuard fork. Use split tunneling to route selected traffic through your own servers. On Windows, connect to multiple VPN servers at once and choose a server for each routing rule.

**[Download](https://github.com/ytreeeeeeeeeeee/amneziageo/releases) · [Documentation](https://bor-project.github.io/amneziageo/) · [Server](https://github.com/ytreeeeeeeeeeee/amneziageo_server)**

## Features

- Multiple simultaneous VPN connections on Windows, with a main server, reserve servers and per-rule server selection.
- Full and split tunneling by domain, site category, country, IP range or application, with several routing lists active at once.
- DNS-based domain routing and access to networks behind your servers. Application routing is experimental; see application routing for [Windows](https://bor-project.github.io/amneziageo/windows/menu/routing.html#application-routing), [Linux](https://bor-project.github.io/amneziageo/linux/menu/routing.html#application-routing) and [Android](https://bor-project.github.io/amneziageo/android/menu/routing.html#application-routing).
- WebSocket over TCP for networks that block UDP.
- SOCKS5 and HTTP proxies for other devices on the local network.
- Route diagnostics, speed checks and a shared command-line interface.

Windows 7, 10 and 11 (x64 and ARM64), Linux (deb packages for amd64 and arm64), and Android.

Install the packages from the [fork's releases](https://github.com/ytreeeeeeeeeeee/amneziageo/releases).
Configuration, command-line reference and build instructions are on the **[documentation site](https://bor-project.github.io/amneziageo/)**.

## Routing lists

Open **Routing** and use the switch on each list card to turn that list on or off. The selection is shared across VPN
configurations with **Routing** enabled. The main screen's routing switch turns off the whole set and restores the last
available selection when turned back on. With no lists selected, the VPN uses the configuration's routes (`AllowedIPs`).

- If any selected list turns on **Everything through VPN**, the VPN is the default route and explicit direct and block
  rules still apply. With only split lists, the rules select what goes through the VPN. If any list requests **All UDP
  through the tunnel**, the combined flag is on. Its effect depends on the tunnel mode.
- A recognized domain rule takes precedence over an application rule. If domains sharing an IP have conflicting rules
  and the connection's domain is unknown, the application rule applies. An explicit IP block still takes precedence.
- On the first receipt of server presets, all presets marked **Enabled by default** are selected if you have no saved
  selection. Later syncs respect your manual changes. **Server lists only** requires at least one permitted preset.

**Windows** applies a new selection on reconnect. **Android** attempts to reconnect an active VPN automatically.
**Linux** can apply compatible domain and IP changes while connected. Changes to the VPN mode, application rules or the
effective all-UDP policy require a reconnect. Compare **Selected lists** and **Applied lists** in the app or run
`amneziageo routing selection` on Windows or Linux.

### Example with two application lists

Create **Yandex Disk direct** with Yandex Disk under **Direct** and **Everything through VPN** enabled. Create **Discord
via VPN** with Discord under **Through VPN** in split mode. Leave **All UDP through the tunnel** off in both lists. Use
the app entries for your platform.

Enable both lists and wait for them to apply. Yandex Disk goes directly, and Discord and other traffic go through the
VPN where no other rule applies. Turn off **Yandex Disk direct** and apply the change. The applied count drops from two
to one, the mode changes to split, and Discord stays on the VPN while unmatched traffic goes directly.

## Server

**[AmneziaGeo Server](https://github.com/ytreeeeeeeeeeee/amneziageo_server)** is a panel for your own AmneziaWG server on Linux: clients with configurations, QR codes and subscriptions, templates, routing by geo databases and domains through other servers, a resolver for the clients. The client reads the geo sources, routing lists and subscription of such a server by itself; any other AmneziaWG server works as well. Installation and setup are on the **[server documentation site](https://bor-project.github.io/amneziageo_server/)**.

## Support the project

Donations via **TRON (TRC20):** `TNHcrYqUv2pUfW7BEzYJyXfVk9wEJrs4FR`.

## License and credits

[GPL-3.0 or later](LICENSE). Built on the [Amnezia VPN](https://github.com/amnezia-vpn) ecosystem; the AmneziaWG engine retains its authors' license. The [terms of use](TERMS.md), the [privacy policy](PRIVACY.md) and the [third-party components](THIRD-PARTY.md) lie in the root of the repository; the application shows them on its first start and under About.

Free code signing provided by [SignPath.io](https://signpath.io/), certificate by [SignPath Foundation](https://signpath.org/). See the [code signing policy](https://bor-project.github.io/amneziageo/CODE_SIGNING.html) and [privacy policy](https://bor-project.github.io/amneziageo/PRIVACY.html).
