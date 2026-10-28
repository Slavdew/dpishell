# Games: app profiles and general game mode

[Русский](games.md)

Site optimization works by site name: at the start of a connection the browser openly states where it is going (`www.youtube.com`), and the program confuses the provider's filter only on those connections. Games have no name in their packets — their own protocol, their own ports, often UDP. That is why games have separate settings on the **"Games & apps"** tab.

## What the tab has

| | What it does | When to turn on |
|---|---|---|
| **App profiles** | precise rules for a specific game: its ports, addresses, and better a packet signature. Each profile has its own switch; several can be on at once | the game does not work and there is a profile for it (recorded by the program, added from a Flowseal build, or received as a line from someone else) |
| **General game mode** | all ports 1024–65535 to addresses from the IP list — like Flowseal's "Game Filter" | there is no profile, but you need to try; as a temporary measure |

Profiles and "General" are independent: with "General" on, profiles keep working and match first (they come earlier in the winws command line).

Profiles for specific games are not built into the program: you add them — by recording the game's connections (“Record a game…”), from another build (“Add profile…”, below) or from a profile line (“Import…”).

### Profile strategy

By default a profile uses **the game part of the PC strategy** — the same as general game mode: the strategy's `game_tcp`/`game_udp` role, or one derived from its main role if there is none. Change the PC strategy and it changes too.

A profile with its own parameters — from a Flowseal build or a profile line — always uses them: they are tuned for the game, and catalog strategies are no substitute. Instead of a choice it shows **"From profile"**, with the parameters in the tooltip. Other profiles (for example, made with "Record a game…") have no own parameters: they use the game part of the PC strategy, and the **"Different strategies"** box above the list gives each of them its own choice — "Default" or the game part of any strategy in the list. Clear the box and such profiles use "Default"; the choices are kept.

## Pitfalls

### General mode

- **All game traffic goes through the program.** Not only the game's: everything going to ports 1024–65535 passes through interception, even if it ends up unchanged. Hence latency and CPU load — with torrents and calls up to 100% of one core and noticeably lower speed.
- **Fake packets are sent blindly** — including where nothing is blocked. Games measure ping to regions with short packets; if those are distorted or delayed, the game picks the wrong region (e.g. Asia instead of Europe), and ping grows by 200–500 ms or more.
- **The IP list is coarse.** It contains whole hosting providers (AWS, Cloudflare): it also affects things that should be left alone — launchers, voice chats, other games.
- **It does not work without the IP list:** the mode turns on only with the list loaded.
- If things got worse with the mode on — turn it off. The Flowseal author keeps Game Filter off by default for the same reasons.

### App profiles

- **A profile from someone else's build may not fit.** It was made for a different provider and a different game version; after a game update the rule may stop matching.
- **A profile without a signature catches everything on its ports.** If a profile has only ports and addresses, all game traffic on those ports goes through the program — milder than general mode, but not free. With a signature (packet length and its first bytes) one packet is intercepted, the rest bypasses the program.
- **Several profiles can be on at once**, but if two profiles catch the same ports, the first in the list wins.
- **Selection does not test games.** Whether a profile works can only be seen in the game itself.

## A profile from another build (“Add profile…”)

There is no common format for game profiles. Flowseal builds (`zapret-discord-youtube`) and their forks keep settings in three places — the “Add profile…” window takes the same three pieces of text (empty fields show a format hint). At least one of the first two is required. “Paste from clipboard” recognizes a `dpishell-game:1:…` profile line itself and opens it in “Import profile”.

### 1. Interception filter

Where to look: in game-specific forks — a file in the `windivert.filter\` folder (e.g. `windivert_part.customgame.txt`); in any build — the `--wf-raw-part=` parameter in the `.bat` file (it points to that file). Open the file in Notepad and copy the whole line:

```
outbound and tcp.DstPort = 12001 and tcp.PayloadLength = 79 and tcp.Payload32[0] = 0x0000044d
```

This is the signature: "TCP to port 12001, 79 bytes long, starts with `00 00 04 4D`". The program accepts such lines only when built from understood parts — direction, port, length, packet start; anything else is rejected with a note of what did not fit.

A complex filter that does not break down into these parts is accepted as a custom filter — the result of adding says so. Required: direction `outbound`, protocol and destination port (`tcp.DstPort`/`udp.DstPort`): without them the filter would route all of the PC's traffic through the program. A custom filter is at your own risk: one that is too broad slows down all internet on the computer.

### 2. Processing parameters

Where to look: the `.bat` file that starts the build (`general.bat`, `general (ALT).bat`, etc.). Open it in Notepad (right click → "Edit", **do not** run it). You need the piece from `--new` to the next `--new` that starts with the game's port:

```
--filter-tcp=12001 --dpi-desync=fake --dpi-desync-any-protocol=1 --dpi-desync-repeats=8 --dpi-desync-fooling=ts
```

The program takes the ports and `--dpi-desync…` parameters from the same allow list as strategies. File paths, `--ipset`, `--hostlist`, `--wf-…` and launching other programs are not accepted. Fake packets (`.bin`) — only from the program's own set: foreign files are not loaded.

Signs of a game piece: ports in `--filter-tcp`/`--filter-udp` other than 80/443, no `--hostlist`, often `--dpi-desync-any-protocol=1`. Variables like `%GameFilter…%` in Flowseal `.bat` files are the general game mode, not a game profile: no need to carry them over — here that is "General game mode".

### 3. Subnet list (optional)

Where to look: the build's `lists\` folder, the file `--ipset=` points to in the same `.bat` piece. Copy its contents — one subnet per line (`3.120.0.0/14`). Service and home networks (`192.168.x.x`, `10.x.x.x`, etc.) are dropped. If the profile has a signature, the list is usually not needed.

### Sharing a profile

"⋯" → **"Export"** on a profile copies it as one `dpishell-game:1:…` line: title, ports, signature or own filter, subnets and own parameters. Whether the profile is on and which strategy it has are settings of your PC and are not included. The recipient presses **"Import…"** on the tab (or "Paste from clipboard" in the "New profile" window), sees what is inside, and only then adds it. The line is checked as strictly as input from a build: an extra field, a disallowed parameter, a foreign `.bin` file, a service or too wide subnet — refused with the reason.

"⋯" → "Copy for Flowseal" gives three pieces of text for a Flowseal build: the filter — into a `windivert.filter\*.txt` file referenced by `--wf-raw-part=@…`, the parameters — as a separate piece after `--new`, the subnets — into `lists\`.

## If the game still does not work

1. Turn the game's profile and "General" off and check that the game starts and sites open without them.
2. Turn on a profile for the game; if needed — "Different strategies" and another strategy for the profile.
3. "General game mode" — last, and watch ping and region in the game itself.
4. Two optimization programs do not work at the same time: turn DpiShell off before starting another build (and vice versa).
