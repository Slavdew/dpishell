# Games: game mode and custom profiles

[Русский](games.md)

Site optimization works by site name: at the start of a connection the browser openly states where it is going (`www.youtube.com`), and the program confuses the provider's filter only on those connections. Games have no name in their packets — their own protocol, their own ports, often UDP. That is why games have separate settings on the **"Games and apps"** tab.

## Three options

| | What it does | When to choose |
|---|---|---|
| **Off** | game traffic goes as is | the game works anyway — the default |
| **Custom profiles** | only the profiles you added: precise rules for a specific game | the game does not work and there is a profile for it (recorded by the program or taken from another build) |
| **General** | all ports 1024–65535 to addresses from the IP list — like Flowseal's "Game Filter" | there is no profile, but you need to try; as a temporary measure |

Profiles for specific games are not built into the program: you add them — by recording the game's connections (“Record a game…”) or from another build with “Add profile…” (below).

## Pitfalls

### General mode

- **All game traffic goes through the program.** Not only the game's: everything going to ports 1024–65535 passes through interception, even if it ends up unchanged. Hence latency and CPU load — with torrents and calls up to 100% of one core and noticeably lower speed.
- **Fake packets are sent blindly** — including where nothing is blocked. Games measure ping to regions with short packets; if those are distorted or delayed, the game picks the wrong region (e.g. Asia instead of Europe), and ping grows by 200–500 ms or more.
- **The IP list is coarse.** It contains whole hosting providers (AWS, Cloudflare): it also affects things that should be left alone — launchers, voice chats, other games.
- **It does not work without the IP list:** the mode turns on only with the list loaded.
- If things got worse with the mode on — turn it off. The Flowseal author keeps Game Filter off by default for the same reasons.

### Custom profiles

- **A profile from someone else's build may not fit.** It was made for a different provider and a different game version; after a game update the rule may stop matching.
- **A profile without a signature catches everything on its ports.** If a profile has only ports and addresses, all game traffic on those ports goes through the program — milder than general mode, but not free. With a signature (packet length and its first bytes) one packet is intercepted, the rest bypasses the program.
- **Several profiles can be on at once**, but if two profiles catch the same ports, the first one wins.
- **Selection does not test games.** Whether a profile works can only be seen in the game itself.

## A profile from another build (“Add profile…”)

There is no common format for game profiles. Flowseal builds (`zapret-discord-youtube`) and their forks keep settings in three places — the “Add profile…” window takes the same three pieces of text (empty fields show a format hint). At least one of the first two is required.

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

Signs of a game piece: ports in `--filter-tcp`/`--filter-udp` other than 80/443, no `--hostlist`, often `--dpi-desync-any-protocol=1`. Variables like `%GameFilter…%` in Flowseal `.bat` files are the general game mode, not a game profile: no need to carry them over — here that is the "General" option.

### 3. Subnet list (optional)

Where to look: the build's `lists\` folder, the file `--ipset=` points to in the same `.bat` piece. Copy its contents — one subnet per line (`3.120.0.0/14`). Service and home networks (`192.168.x.x`, `10.x.x.x`, etc.) are dropped. If the profile has a signature, the list is usually not needed.

### Sharing a profile

"Copy" on a profile gives the same three pieces. You can send them to a friend with DpiShell or paste them into a Flowseal build: the filter — into a `windivert.filter\*.txt` file referenced by `--wf-raw-part=@…`, the parameters — as a separate piece after `--new`, the subnets — into `lists\`.

## If the game still does not work

1. Turn game mode off and check that the game starts and sites open without it.
2. Try "Custom profiles" with a profile for the game; if needed — another parameter variant of the profile.
3. "General" — last, and watch ping and region in the game itself.
4. Two optimization programs do not work at the same time: turn DpiShell off before starting another build (and vice versa).
