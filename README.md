# DpiShell

Простая оболочка над [zapret](https://github.com/bol-van/zapret) (bol-van) для оптимизации соединения с YouTube, Discord и другими сайтами. Нажали «Включить» — в большинстве сетей этого достаточно; если сайты не открылись — «Подобрать»: программа сама проверяет стратегии и включает лучшую. Без VPN, без телеметрии; всё заимствованное закреплено хешами и проверено.

Два самостоятельных продукта в одном репозитории, с независимыми лентами релизов:

| | Что | Документация | Релизы |
|---|---|---|---|
| **Windows-приложение** | окно без прав администратора + служба-помощник; подбор стратегий, IP-списки, игровой режим, «Починить адреса», DNS в один клик, управление роутером | [docs/windows/README.md](docs/windows/README.md) | `windows-vX.Y.Z` (portable-архив) |
| **Пакет для роутера с OpenWrt** | `dpishell` + `luci-app-dpishell`: оптимизация для всей сети, подбор с самого роутера, страница `/dpi`, DNS для всей сети, API для приложения | [docs/router/README.md](docs/router/README.md) | `router-vX.Y.Z` (пакеты и офлайн-набор) |

Они видят друг друга по [договору](docs/dev/router-api.md); каждый работает и сам по себе. Установка самой OpenWrt — под ответственность пользователя.

## Как выглядит

Окно Windows-приложения в светлой и тёмной теме. Адреса, имена программ и сети на снимках выдуманы и размыты: снимки сделаны в демо-режиме на подставных данных.

| | Светлая | Тёмная |
|---|---|---|
| **Главная** — включить, что открывается, что мешает | <img src="docs/screenshots/home-light.png" width="420" alt="Главная, светлая тема"> | <img src="docs/screenshots/home-dark.png" width="420" alt="Главная, тёмная тема"> |
| **Подбор** — результат проверки стратегий | <img src="docs/screenshots/select-light.png" width="420" alt="Подбор стратегии, светлая тема"> | <img src="docs/screenshots/select-dark.png" width="420" alt="Подбор стратегии, тёмная тема"> |
| **Роутер** — состояние и действия | <img src="docs/screenshots/router-light.png" width="420" alt="Роутер, светлая тема"> | <img src="docs/screenshots/router-dark.png" width="420" alt="Роутер, тёмная тема"> |
| **Игры и программы** — настройка под игру | <img src="docs/screenshots/apps-light.png" width="420" alt="Игры и программы, светлая тема"> | <img src="docs/screenshots/apps-dark.png" width="420" alt="Игры и программы, тёмная тема"> |
| **DNS** — защищённый DNS этого компьютера | <img src="docs/screenshots/dns-light.png" width="420" alt="DNS, светлая тема"> | <img src="docs/screenshots/dns-dark.png" width="420" alt="DNS, тёмная тема"> |
| **Информация и обновления** — версии, списки | <img src="docs/screenshots/about-light.png" width="420" alt="Информация и обновления, светлая тема"> | <img src="docs/screenshots/about-dark.png" width="420" alt="Информация и обновления, тёмная тема"> |

Остальные вкладки («Сайты», «Починить адреса», «Диагностика», «Настройки») — в [docs/screenshots/](docs/screenshots/).

## Скачать (бета)

1. Откройте [страницу последнего выпуска](https://github.com/Slavdew/dpishell/releases/latest) и скачайте `DpiShell-<версия>-portable.zip`.
2. Распакуйте в любую папку и запустите `DpiShell.exe`. Программа не подписана — Windows SmartScreen может предупредить: «Подробнее» → «Выполнить в любом случае». Если такой кнопки нет — запуск, возможно, ограничивает Smart App Control или политика организации.
3. Нажмите «Включить». Подробно — [docs/windows/README.md](docs/windows/README.md).

Это бета: если что-то не работает, [напишите](https://github.com/Slavdew/dpishell/issues), что видели (лучше со снимком экрана).
## Разработчикам

- [Устройство обоих продуктов](docs/dev/architecture.md) — данные, подбор, безопасность, что планируется.
- [Сборка, тесты, публикация](docs/dev/building.md).
- [Договор с роутером](docs/dev/router-api.md), [протокол окно ↔ помощник](docs/dev/helper-protocol.md).
- [Проверка заимствованного](docs/vetting.md) — источники, хеши, лицензии.
- Исследования: [какие бывают движки оптимизации соединения (обход DPI) под Windows и почему zapret v1](docs/research/bypass-engines.md), [DpiShell на роутере](docs/research/router-openwrt.md) и [заметки по nfqws на OpenWrt](docs/research/router-nfqws.md), [функции Flowseal: игровой режим, IP-списки, hosts](docs/research/flowseal-features.md), [DNS и подмена ответов](docs/research/dns.md).
- [Тексты кнопок «(i)»](docs/info-texts.md) — часть продукта, их читает тест.

Папки: `src/` (C#/.NET 10, WPF), `tests/`, `data/` (общие каталог стратегий, цели, списки, манифесты), `packaging/`, `router/`, `vendor/` (резервная копия архива ядра).

## Лицензия

DpiShell — [MIT](LICENSE). Сторонние компоненты и их лицензии (zapret — MIT, Flowseal — MIT, WinDivert — LGPLv3/GPLv2, cygwin — LGPLv3, .NET — MIT) — `packaging/THIRD-PARTY-NOTICES.txt`.
