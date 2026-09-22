# The .als format — what a real library revealed

Verified on Live 10/11/12, over a sample of 427 sets from `E:\Music\Ableton Projects`.

## The container

An `.als` is **gzip over XML**, not tar. Magic `1F 8B`. Decompressing gives a single XML document:
a typical set of 100 KB on disk unfolds into ~2.9 MB of text, roughly 25×.

With a thousand and more sets, pulling that into memory whole is out of the question — the parsing
is streaming, `XmlReader` straight over `GZipStream`. That works out at around 150 ms per set.

The root carries the version the set was last saved with:

```xml
<Ableton MajorVersion="5" MinorVersion="12.0_12300" Creator="Ableton Live 12.3.5" ...>
```

## Tracks and tempo

The types are `AudioTrack`, `MidiTrack`, `GroupTrack`, `ReturnTrack`, `PreHearTrack` and `MainTrack`.
**Careful:** in Live 12 the master track is called `MainTrack`, while in older sets it is
`MasterTrack`. Both names have to be read.

A track name is `<Name><EffectiveName Value="..."/>`, and the tempo is `<Tempo><Manual Value="111"/>`
inside the master track (a `Tempo` node occurs in clips too, so the one in the master is the one
wanted).

## Plugins

| Node | What it gives |
|---|---|
| `VstPluginInfo` | `PlugName`, `UniqueId` and **the full path to the .dll** |
| `Vst3PluginInfo` | `Name` and a `Uid` of four fields; no path |
| `AuPluginInfo` | macOS |
| `MxDevice*` | Max for Live; the `.amxd` file arrives through a nested `FileRef` |

The path to the `.dll` on VST2 is the one case where the set itself shows where a plugin was loaded
from. For VST3 only the name and the Uid are left.

A trap: inside `Vst3PluginInfo` there is an empty `<Name Value="" />` in the preset block, while the
real name is a direct child of the plugin node. The parsing has to account for depth, or the names
are lost.

## Which plugins are installed on the machine

`%APPDATA%\Ableton\Live <version>\Preferences\PluginScanDb.txt` is Live's own database, rewritten on
every start. Plain text, three tables:

```
Logging plugins information about plugin modules start
ModuleId,Path,Arch,Processor,ScanState,Fingerprint
889,"C:\Program Files\Common Files\VST3\Serum2.vst3",3,1,ok,"1258800:6a4ca126"

Logging plugins information about all plugins start
PluginId,ModuleId,DevIdentifier,Name,Vendor,Version,SdkVersion,Flags,ScanState,SubCategories,IsEnabled
1698,889,"device:vst3:instr:56534558-…","Serum 2","Xfer Records","2.1.5","VST 3.7.12",1,1,"Instrument|Synth",1
```

There is no need to scan the folders oneself: that would mean parsing VST binaries, and Live has
already done it — with exactly the folder settings it has been given.

There is also a `PluginScanner.txt`, but that is a **log** rather than a database: it holds only the
plugins that were rescanned, while the rest are marked "plugin already scanned" with no detail. The
full list exists only in `PluginScanDb.txt`.

**Identification.** By identifier, not by name:

| Format | In Live's database | In the set |
|---|---|---|
| VST3 | `device:vst3:instr:ed57bd72-5c60-467e-a64d-d2f400758b6f` | `<Uid><Fields.0..3/>` — the same 16 bytes as four ints, most significant byte first |
| VST2 | `device:vst:instr:2017543218?n=…` | `<UniqueId Value="2017543218"/>` |

Verified on FabFilter Pro-Q 4: the Fields "-313016974, 1549813374, -1504849164, 7703407" give exactly
the `ed57bd72-5c60-467e-a64d-d2f400758b6f` from the database.

**Traps.**

* A `.vst3` on newer plugins is a **bundle folder** rather than a file: `File.Exists` on an installed
  Serum 2 honestly returns false, so `Directory.Exists` has to be checked too.
* One plugin gets into the database several times when Live sees several of its files (a Debug and a
  Release of one build) — count them as one.
* One and the same plugin is registered both as VST2 and as VST3 with different identifiers. A set
  saved with the VST2 version will still open with a hole when only the VST3 is installed, so this is
  a state of its own rather than "installed".

## The project key

The overall key (Live 12) sits directly in `LiveSet`:

```xml
<ScaleInformation>
    <Root Value="0" />   <!-- 0 = C ... 11 = B -->
    <Name Value="0" />   <!-- the scale index -->
</ScaleInformation>
<PreferFlatRootNote Value="false" />
```

A node just like it exists **on every clip**, so the one to take is the one lying directly in
`LiveSet`, or the key of a random clip arrives instead.

The order of the scales is not guessed: it is baked into `Ableton Live 12 Suite.exe` together with the
LOM documentation, which lists the "default scale names that can be saved with a set and recalled" —
from `Major` and `Minor` to `Messiaen 7`, 35 in all. The same place confirms the notes: "The root can
be a number between 0 and 11, with 0 corresponding to C".

Sets from Live 9/10/11 have no such node — the key is simply empty.

## An empty set

`Resources\Builtin\Templates\DefaultLiveSet.als` in a Live installation is the very file Live makes a
new project from: 120 BPM, 2 MIDI and 2 audio tracks, 2 returns, no plugins. Copying it is more
reliable than assembling XML by hand: the schema is guaranteed to be the one the installed version
understands.

## The arrangement: clips on the ruler

The ruler's clips lie inside a track at `DeviceChain → MainSequencer → Sample` (for audio) or
`ClipTimeable` (for midi), and then **`ArrangerAutomation → Events`**:

```xml
<AudioClip Id="3" Time="96">
    <CurrentStart Value="96" />        <!-- beats from the start of the set -->
    <CurrentEnd Value="128" />
    <Loop>
        <LoopStart Value="0" />        <!-- with the loop off these are the start/end markers, -->
        <LoopEnd Value="32" />         <!-- while the loop itself moves into HiddenLoop* -->
        <StartRelative Value="0" />
        <LoopOn Value="false" />
    </Loop>
    <Name Value="den 97 trashcore" />
    <Color Value="23" />
    <Disabled Value="false" />
</AudioClip>
```

**A trap:** `AudioClip`/`MidiClip` nodes just like these lie in `ClipSlotList` — those are session
clips and are not on the ruler. The only way to tell them apart is whether we are inside
`ArrangerAutomation`.

The notes of a midi clip:

```xml
<Notes><KeyTracks><KeyTrack Id="19">
    <Notes><MidiNoteEvent Time="0" Duration="32" Velocity="95" /></Notes>
    <MidiKey Value="26" />          <!-- the pitch arrives AFTER the notes themselves -->
</KeyTrack></KeyTracks></Notes>
```

A note's `Time` is from the start of the clip's content, not of the set. The moment on the ruler is
`CurrentStart + (Time − LoopStart − StartRelative) + k·(LoopEnd − LoopStart)`, where k is the loop
repetition number (only with `LoopOn`).

## Colours

The clip and track palette is 70 colours in a 14×5 grid, the same one as in a track's context menu.
The values are not in a Live installation: they are baked into the binary, and the themes
(`Resources/Themes/*.ask`) hold only `Clip1..Clip16` — that is the old 14-colour palette, not this one.

What the installation **does** hold is the `live_to_push2_colors` table in
`Program\Push2\qml\Ableton\Push\Assets\css\dark.css`: all 70 Live indices mapped to Push screen
colours, in exactly five rows of 14. Both the number of colours and the order of hues in a row were
checked against it.

The numbering depends on the version:

| Version | Node | Meaning |
|---|---|---|
| 11, 12 | `Color` | the palette index directly, 0..69 |
| ≤ 10 | `ColorIndex` | on clips, the palette index; on tracks, shifted |

The track shift was checked over 4,300 "a track and its clips" pairs from older sets: in 85% of cases
it is exactly **140** (the rest are clips whose colour was changed by hand), and a separate block shows
**218** (track 282 against clips at 64). If a number falls into no block, the track colour is taken as
the most common colour of its clips — a clip inherits the track colour by default.

## File references — the most important part

### A dependency is only a clip's sample

`FileRef` occurs in a set under all sorts of nodes, but **the only thing Ableton counts as a lost file
is a clip's sample** — the one whose direct parent is `SampleRef`. Everything else is the provenance of
embedded content: Ableton stores a preset or a rack inside the set itself, and the `FileRef` beside it
merely remembers where it was dragged from. No file is needed to open it, and Ableton does not complain
about its absence.

| `FileRef` parent | What it is | Count it? |
|---|---|---|
| `SampleRef` | a clip's audio | **yes** |
| `FilePresetRef` | where a device preset came from | no |
| `AbletonDefaultPresetRef` | a device's default preset | no |
| `OriginalFileRef` | the source before conversion/analysis | no |
| `MxPatchRef`, `MxDPatchRef` | internal Max patches | no |

Verified over a sample of 184 sets: on the provenance nodes **94%** (`AbletonDefaultPresetRef`) and
**87%** (`FilePresetRef`) of the paths are "missing" — they lead to somebody else's machine
(`C:/Users/ivani/...`), because the content has long been inside the set. Counting them as losses is
the very bug that shows to the eye: Ableton opens the project without a word while the manager wrote
"2 missing". After narrowing to `SampleRef`, three such sets showed exactly 0 losses, as in Live.

### RelativePathType — the root of a path

The key field on a sample is `RelativePathType`; it names the **root** that `RelativePath` is measured
from:

| Type | Root | Example |
|---|---|---|
| 0 | there is no relative root — the absolute path only | `C:/.../loop.wav` |
| 1 | the project folder, and the path may go upwards | `../../Samples/pack/loop.wav` |
| 3 | inside the project folder | `Samples/Recorded/audio.wav` |
| 5 | a Live Pack root, the name in `LivePackName` | `Audio Effects/Spectral Blur` |
| 6 | the User Library | `Presets/Audio Effects/EQ Eight/x.adv` |
| 7 | `Resources\Builtin` of the installed Live | `Devices/Audio Effects/LFO/...` |

Type 0 does not mean "there is no reference": on samples it is most often a file dragged in from
outside the project folder — Ableton keeps it by absolute path. In the sample, type 0 on samples covers
25 thousand `.wav` and 15 thousand `.mp3`, and most of them are found. An empty placeholder (both paths
empty) is a separate case, and there are plenty of those too.

The absolute path in `Path` is only a hint and cannot be trusted:

* in projects from collaborations it leads into somebody else's profile (`C:/Users/ivani/...`);
* in older sets, to a previous version (`.../Live 11.1.6 Suite/Resources/...`);
* packs may have moved to another drive.

The right order is the root from `RelativePathType` first, and only then the absolute path as a
fallback.

## Where to find the roots on the current machine

`%APPDATA%\Ableton\Live <version>\Preferences\Library.cfg` is ordinary XML and holds the map of packs:

```xml
<LibrarySliceInfo Path="E:\Music\Factory Packs\Creative Extensions"
                  DisplayName="Creative Extensions" UniqueId="..." />
```

and the location of the User Library through `ProjectPath` + `ProjectName`. This is the one trustworthy
source: the paths inside sets go stale, while this file describes the machine's current state.

The same file holds the **Places** of Live's browser — the folders added to its sidebar:

```xml
<UserFolderInfo Id="125114" Path="E:\Music\Samples" DisplayName="Samples E:\" IconName="" />
<PreferredFactoryPacksInstallationPath Value="E:\Music\Factory Packs" />
```

They mix sample folders with project ones (on the development machine, ten Places, half of them
project folders), so the Samples tab offers them and lets the person pick.
`PreferredFactoryPacksInstallationPath` is where Live installs packs.

**Two traps in the packs.** Every pack keeps browser previews in `Ableton Folder Info\Previews` —
`.ogg` files named after the preset (`Hollow Point - 64 Pad Lab.adg.ogg`). They are not samples:
9,491 files in the packs on the development machine are these, and a walk of the library has to skip
that folder or the packs look far bigger than they are. And most of the `.aif` in the packs are not
plain AIFF but AIFC compressed with Ableton's own codec (`able` in the `COMM` chunk — 2,480 of the
first 3,000): only Live plays those. A sample library outside the packs holds ordinary AIFF.

## The result on a real library

A sample of 366 sets, counting clip samples (`SampleRef`) only:

* 281,485 sample references, of which 16,608 are lost (5.9%)
* 211 sets have lost samples
* all 61 connected packs are in place

For comparison: counting every `FileRef` indiscriminately as a potential loss (as it was before the fix)
gave 289 sets "with losses" and 26,970 references — that is, 78 sets were flagged for nothing because of
paths to embedded presets and racks.
