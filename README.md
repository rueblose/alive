<div align="center">

<img src="docs/img/icon.png" width="96" alt="Alive">

# Alive

**A fast, portable catalog for your Ableton Live projects and plugins.**

One `.exe`. No installer, no external DLLs, no runtime to download.

![version](https://img.shields.io/badge/version-1.1-2ea043)
![platform](https://img.shields.io/badge/platform-Windows%2010%20%7C%2011-0078d4)
![framework](https://img.shields.io/badge/.NET%20Framework-4.6%2B-512BD4)
![license](https://img.shields.io/badge/license-MIT-blue)

[**Download**](../../releases) · [Features](#features) · [Shortcuts](#keyboard-shortcuts) · [Build](#building-from-source) · [Русский](README.ru.md)

<img src="docs/img/home.png" alt="Alive — home view">

</div>

---

## Why

After a few years Ableton leaves you with several hundred `.als` files spread over a dozen
folders, each one wrapped in its own `Project` directory with a `Backup` subfolder full of
near-identical copies. Explorer shows you names and dates. It does not show you the tempo, the
key, which plugins a set needs, whether those plugins are still installed, or which of the nine
files in that folder is the one you actually finished.

Alive reads the sets themselves and answers those questions.

## Features

### Home

The picture at the top of this page. Every project is a tile, and the picture on it is its own
arrangement, drawn from the set — a track is recognisable by its shape long before you read the
name. A star pins one to the front; the play button starts the render sitting next to it.

Above the tiles, a year of work in one strip: the days you opened Live, the streak you are on,
the longest one so far, the hour you usually start, and what the whole library weighs.

### Catalog

<img src="docs/img/sets.png" alt="Sets list with the project inspector">

**Sets are parsed, not just listed.** Alive streams through the `.als` (gzip over XML) and pulls
out tempo, key and scale, Live version, track count, creation date, the size of the whole project
folder, every plugin the set loads and every file it references.

**Versions collapse.** A project usually holds a dozen `.als` files — `v1`, `final`, `final 2`.
The whole folder folds into one row; the counter on the end opens up the individual sets.

**Filters and search.** `F` filters by date, Live version, key root and scale, track and plugin
count, file state and whether a render exists. `Ctrl F` searches by name. Table columns are
configurable — which ones, in what order, how wide.

<img src="docs/img/filters.png" alt="Filters" width="560">

**Tags and notes.** `Ctrl T` attaches your own labels and free text. They bind to the *project
folder*, not the `.als`, so they survive the day `final 2.als` appears next to it.

**Folder watching.** A new set shows up on its own, without `F5`.

### Plugins

<img src="docs/img/plugins.png" alt="Plugin library">

A separate tab (`Ctrl 3`) for the whole plugin library: where each one lives, what it is, how many
sets use it, and whether it is still installed.

The list comes from **Live's own database**, and from every installed version at once — plugins
live in the system, while each Live install is only a snapshot of what it happened to scan. You
can pin it to one specific install, or switch to walking your VST2/VST3 folders instead.

### Rescue

<img src="docs/img/rescue.png" alt="Rescue — testing a set plugin by plugin" width="560">

A set refuses to open? Alive reads Live's own log and names the plugin it died on — `VST3: Going
to restore: X`, the last line before the crash. If the log says nothing, it finds the culprit by
bisecting on a copy.

**The original is never touched.** In a *copy* of the set, only the plugin identifier (`Uid` /
`UniqueId`) is swapped, so Live reports it as "plugin not found" — and everything else, every
node, chain, automation lane and preset blob, stays byte for byte.

### Export

<img src="docs/img/export.png" alt="Export dialog" width="560">

Collect every media file a set uses — your own, from other projects, from the User Library, from
factory packs — into one portable folder or `.zip`. The same four questions Live's *Collect All
and Save* asks, plus a count and a size per category **before** you commit: packs routinely drag
in several gigabytes, and that is worth seeing beforehand rather than after.

The original is never touched here either: only the copy's `.als` gets its paths rewritten. A
sample that could not be found or copied does not corrupt its reference — it stays exactly as it
was, and the loss count goes into the notification.

### Sound and sight

<img src="docs/img/preview.png" alt="Arrangement preview">

**Arrangement preview** (`Ctrl Space`) renders the whole set as one picture, in Live's own clip
colours.

**Render player** (`Space`) plays the bounce sitting next to the set. `Samples`, `Backup` and
Live's own housekeeping folders are skipped, and what is left is ordered by plausibility:
`Render` / `Bounce` first, then the project root. Any file can be pinned as the main preview.

**Vector UI.** Everything is drawn in code and scales with DPI. Horizontal scrolling is
`Shift`+wheel, and the name column stays put.

### Stat

<img src="docs/img/stat.png" alt="Stat — the whole library as a point cloud">

Your entire library as one point cloud. Every project is a dot, and six of its numbers place it:
three axes, plus the dot's size, its fade and its colour. Each of the six is yours to assign —
tempo against track count against the year, or anything else the catalog knows.

### Settings

<img src="docs/img/settings.png" alt="Settings" width="560">

`Ctrl ,` or the gear in the header. Smooth scrolling, a transparency switch
(on Windows 10 the acrylic backdrop is recomputed on every window move, so the window lags behind
the cursor), and where the plugin list comes from — Live's own database, or your VST2/VST3 folders.

**Updates.** Once a day Alive asks GitHub whether a newer release exists, and says so with a dot
on the gear. No popup, no window in your way: the dot waits until you look. Only a release that
moves the major or the minor number lights it — a fix waits to be asked about. **Check for
updates** in the same row asks on the spot and reports anything, fixes included.

That request is the only one the program ever makes. Nothing about you, your library or your
machine goes with it: it is a plain GET for a public page, and GitHub learns from it what any web
server learns from anybody who opens one. The switch beside it turns even that off, and then Alive
touches the network never.

---

## Download

Grab the archive from [Releases](../../releases).

1. Unpack it.
2. Run `Alive.exe`.
3. Point it at your Ableton project folders and press **Scan**.

Requires **.NET Framework 4.6+**, which ships with Windows 10 and 11.

---

## Keyboard shortcuts

| | | | |
|---|---|---|---|
| `F1` | Help | `Enter` | Open set in Live |
| `Ctrl 1` / `Ctrl 2` / `Ctrl 3` | Home / Sets / Plugins | `Shift Enter` | Show in Explorer |
| `F` | Filters | `Space` | Play render |
| `Shift F` | Scan folders | `Ctrl Space` | Arrangement preview |
| `Ctrl F` | Search | `Q` | Pin set |
| `Ctrl ,` | Settings | `Ctrl T` | Tags and notes |
| `F11` / `Ctrl M` | Fullscreen / minimize | `Ctrl R` | Rescue a set |
| `Ctrl Q` | Quit | `F5` | Rescan |
| | | `Ctrl N` | Launch Live |

---

## It will not start / it sees no plugins

1. **"Windows protected your PC"** — the app is not code-signed and SmartScreen complains about
   every one of those. Click **More info → Run anyway**.

2. **Your antivirus flagged it.** Alive collects files from across your disk, writes them into an
   archive, and can write a probe copy into a project folder and hand it to Live — legitimate
   behaviour that heuristic engines sometimes score as a dropper. The build is plain C# compiled
   from the sources in this repository, with no packer and no obfuscation; you can read every line
   of it here and build it yourself with one command. If your scanner quarantines it, add the
   folder to its exclusions and, if you feel like it, send the vendor a false-positive report.

3. **Downloaded, or arrived over a messenger** — right-click `Alive.exe` → **Properties** →
   tick **Unblock** → OK. Without it Windows may close the program silently.

4. **Empty or partial plugin list** — Ableton Live has to have run at least once: the list comes
   from its database, not from your VST folders. If it is still empty, open **Live → Preferences →
   Plug-Ins → Rescan**. In settings you can pin a specific Live install or switch to scanning
   VST2/VST3 folders directly.

5. **Something else is wrong** — Alive writes a log of its last run to `%APPDATA%\Alive\alive.log`:
   Windows version, .NET version, where Live was found and what exactly failed. That file is the
   first thing to send.

---

## Building from source

No heavy IDE required — Alive builds with the C# compiler that ships inside .NET Framework 4.x,
already on your machine.

```cmd
build.cmd
```

Out comes `bin\Alive.exe`. That is the whole thing.

---

## Documentation

[FORMAT.md](FORMAT.md) — the `.als` format, as far as 427 real sets revealed it. Written while
building the parser, and the reason the catalog can read a set without opening Live.

---

## License

[MIT](LICENSE).
