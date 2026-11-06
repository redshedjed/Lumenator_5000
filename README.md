# Lumenator 5000

A light show for [YARG](https://yarg.in): it *interprets* what YARG sends, so each lighting cue in the
song's VENUE track picks a **look** from `rig.json`, and YARG's beat, keyframe and drum triggers drive
it, out to real DMX lights and a browser visualizer. The lighting half of the 5000 series, alongside the
Chartinator 5000 for charting. Not an official YARG project.

Started from Neal Manson's [YARG_sACN_to_DMX](https://github.com/Nyxyxylyth/YARG_sACN_to_DMX), which
copies YARG's Stage Kit lights straight onto 2, 4 or 8 PARs (its README is in
[README.original.md](README.original.md)); the sACN receiver, DMX output and PAR profile began there.

- **The chart says what and when.** Cues, keyframes, blackouts, strobe and fog come from the song.
- **`rig.json` says how it looks on your lights.** Fixtures, grouping, colours, intensity curves and
  randomness. Save it while the program runs and it reloads.

With no DMX hardware, the **visualizer** (http://localhost:8080/) is the output: it draws each
fixture from the exact DMX universe that would be sent. **`--sim`** stands in for YARG: it sends a
looping demo song as real sACN, so you can tune without the game.

## Run it

Needs the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).

```
dotnet run -- --sim          # demo song, no YARG needed; open http://localhost:8080/
dotnet run                   # listen for YARG
dotnet test tests/Lumenator.Tests
```

Options: `--rig path/to/rig.json` (default: `rig.json` in the current folder), `--port 8080`,
`--no-visualizer`, `--reaper` (below). The visualizer's **input** menu switches between YARG, REAPER and the
demo song while it runs, and remembers the choice in `rig.json`.

To build it as a standalone program that doesn't need .NET installed (put `rig.json` beside the `.exe`):

```
dotnet publish -c Release -r win-x64 --self-contained -p:PublishSingleFile=true
```

In YARG: Settings > All Settings > Lighting > **Enable DMX output**, universe 1 (`universe` in
`rig.json`). YARG sends multicast on the same PC, which this picks up.

### Move YARG's game channels (recommended)

YARG's default DMX channels overlap. Its fog and strobe lists (6, 14, 22, 30... and 7, 15, 23...)
run over the beatline (14), bonus (15), drums (22), post-processing (23) and bass (30) channels. With
fog on, the drum channel reads as every pad hit at once. The interpreter ignores values YARG never
sends on beat, keyframe and bonus, but it can't tell for drums. In YARG's advanced DMX settings, move
the game channels to free ones, for example cue 101, beatline 102, bonus 103, keyframe 104, drums 105,
post-processing 106, guitar 107, bass 108, keys 109, and put the same numbers under `channels` in
`rig.json`. The visualizer warns when fog is on with the default layout.

## Preview a chart from REAPER

The **REAPER** input takes the show from a chart you're editing instead of from YARG, so you can see your
lights as you work, including edits you haven't saved. Pick it from the **input** menu at the top of the
visualizer (or start with `--reaper`), which walks you through what it needs and shows what's done:

1. **Chart script.** The **Set up** button copies a small read-only script (built into Lumenator) to REAPER's
   Scripts folder and adds a line to `__startup.lua`, so it starts with REAPER; restart REAPER after. It idles
   unless the open project has a VENUE track. **Remove** takes it out again and leaves the rest of
   `__startup.lua` alone. Or run [reaper/lumenator_export.lua](reaper/lumenator_export.lua) yourself from
   REAPER's Actions list.
2. **Position.** In REAPER: Options > Preferences > Control/OSC/web > Add. Control surface mode **OSC**,
   pattern config Default, a **Mode** that only sends, device IP `127.0.0.1`, device port `9000`. Lumenator only
   reads this setting: REAPER owns its settings file. Avoid a mode with a local port: REAPER would then accept
   commands from the network with no password, and the menu warns if it's set that way.
3. Press play in REAPER.

It's an editor's preview, not the whole show:

- **Scrub anywhere, and the lights wait.** They don't work out what was happening at the cursor: they stay
  dark until playback reaches the next cue, and keyframes before that do nothing. A strobe, fog or screen
  effect event still takes effect when it's reached.
- **Stop goes dark, pause holds the lights, and a loop going round keeps the look running.**
- A song starts dark too: where YARG would show the `band` look before the first cue, this waits for it.
- After a scrub, a `step` look can start on a different colour than in a play-through, because its colour
  comes from the keyframes counted so far. The timing is right; judge exact colours from the top.
- It shows Lumenator's reading of your chart, not YARG's, so check the result in the game. The `stagekit`
  look stays dark, because YARG makes those patterns. Notes are the Expert gems as charted, on `PART DRUMS`,
  `GUITAR`, `BASS` and `KEYS`.
- It follows YARG's reading of the events: `strobe_slow` and `strobe_fast` are the only strobes a chart can
  name (any other name in `[lighting (...)]`, `strobe_medium` included, is the `Default` cue), and any cue or
  keyframe ends a strobe.

## rig.json

- `input`: where the show comes from: `yarg` (default), `reaper` or `sim`.
- `fixtures`: name, DMX `address`, `profile`, and optional `x`/`y` for the visualizer (0-1).
  Listed left to right; "halves" and "alternate" go by this order.
- `profiles`: channel offsets per fixture type (`dimmer`, `red`, `green`, `blue`, optional `white`,
  and `fixed` channels held at a value). `par7` is the original's 7-channel PAR.
- `palette`: colour names. `red`/`green`/`blue`/`yellow`/`orange`/`purple` are also the fret and drum
  colours for `frets`.
- `looks`: what the lights do while a cue is up.
- `cues`: YARG cue to look; `"*"` covers every other cue.
- `post_processing`: what each YARG screen effect does to the whole rig (below).
- `verse_chorus_looks`: off (the default), `Verse` and `Chorus` change nothing and the last cue runs on, as on
  YARG's Stage Kit, though they still end a strobe. On, they get the looks `cues` gives them.
- `strobe_per_beat`: YARG's strobe cues become a white software strobe over any look, locked to the
  tempo (charts use Slow = 8th notes, 2 per beat, and Fast = 16th notes, 4 per beat; 0 to ignore).
- `output`: `ftdi` (USB-DMX cable), `sacn_universe` (also send the result as sACN, e.g. to QLC+ or
  a network DMX node), `fps`, `visualizer_port`, `fog_channel` (a fog machine's DMX channel, 255
  while YARG's fog is on).

### Looks

| type | does | uses |
|---|---|---|
| `solid` | colours by pattern | `colors`, `pattern` |
| `pulse` | colours by pattern, brightness kicked on each trigger, fading to `floor` | `trigger`, `decay`, `floor`, `accent` (downbeat louder), `pattern: chase` steps a light per trigger |
| `flash` | first colour flashes on each trigger over the second at `floor` | `trigger`, `decay` |
| `step` | colours by pattern, moving on a step per trigger with a crossfade; on keyframes, next steps on, previous steps back, first goes back to the start | `colors`, `pattern`, `trigger`, `decay` (crossfade seconds), `beat_pulse` (dip between beats, 0-1) |
| `toggle` | colours by pattern, on when the cue starts, flipped off / on by each trigger | `trigger`, `decay` (crossfade) |
| `chase` | a soft spot per colour, evenly spaced, travelling round the rig | `beats` (per lap) or `rate_hz`, `width` (share of the rig), `bounce` (back and forth), `reverse` |
| `layers` | other looks drawn on top of each other | `looks` |
| `frets` | the rig as one instrument's fretboard, left to right (drums: red yellow blue green, cymbals with their pad; guitar / bass / keys: green to orange). A held note lights its section and fades once let go; an open note (a kick on drums) washes the whole rig | `instrument`, `decay`, `floor` (unplayed sections), palette `orange` / `purple` (kick / open) |
| `chaos` | a new random draw per trigger: `density` of the lights, random colours (random hues if `colors` is empty) | `trigger`, `density`, `decay`, `floor` |
| `police` | two sides take turns, `flashes` each, side colours `colors[0]` / `colors[1]` | `pattern` (halves / alternate), `trigger: rate` + `rate_hz`, or any trigger = paced to the beat |
| `wave` | `colors` (a rainbow if empty) crossfading round in a loop, rolling across the lights left to right | `beats` (per cycle) or `rate_hz` (cycles per second, e.g. 0.04), `spread` (cycles across the rig) |
| `stagekit` | YARG's own Stage Kit pattern, positions folded onto your fixtures (i, i+n...) | palette red/green/blue/yellow |
| `blackout` | all off | |

Triggers: `beat`, `downbeat`, `keyframe`, `snare`, `kick`, `tom`, `cymbal`, `drums` (any hit),
`bonus`, `guitar`, `bass`, `keys`, `cue` (once, as the cue starts), `rate` (a clock at `rate_hz`) or
`beats` (every `beats` beats, locked to the song: 0.25 = 16th notes). Keyframes come from the chart
(VENUE notes 48-50), so they land exactly where they were charted; the drum triggers come from YARG's
drum channel.

Every look takes `fade`: seconds to crossfade in from the previous look (default 0.3; flares and
stomp use 0, blackout_slow 2).

In the default `rig.json`, manual cues (and `Verse` and `Chorus`, when they're on) step on keyframes, loops are spots
rolling round the rig on the beat, `Stomp` is a white hit per keyframe, flares are one white hit,
blackouts fade, `Searchlights` are two spots crossing. A song with no lighting cues (`NoCue`), and any
cue left out, gets `band`: the rig plays the drums with the guitar's notes on top.

`post_processing`: what YARG's screen effects do to the whole rig while they're on, keyed by YARG's
names (below). A plain colour is a full tint (`"BlackAndWhite": "white"`); otherwise an object with
any of `tint` + `strength`, `contrast`, `lift`, `mirror`, `invert`, `posterize`, `polarize` (a list of
colours), `trails` (seconds), `flicker`, `choppy` (updates per second), `scanline` and `fringe`
(seconds). Effects left out change nothing.

### Screen effects

YARG's effects and the value it sends for each. A `.mid` chart still names them with Rock Band's
text events (YARG translates them), so the last column is what to put on the VENUE track.

| YARG effect (`rig.json` key) | DMX | Chart event |
|---|---|---|
| Default | 0 | `[ProFilm_a.pp]` |
| Bloom | 4 | `[bloom.pp]` |
| Bright | 14 | `[bright.pp]` |
| Contrast | 24 | `[film_contrast.pp]` |
| Mirror | 34 | `[ProFilm_mirror_a.pp]` |
| PhotoNegative | 44 | `[photo_negative.pp]` |
| Posterize | 54 | `[posterize.pp]` |
| BlackAndWhite | 64 | `[film_b+w.pp]` |
| SepiaTone | 74 | `[film_sepia_ink.pp]` |
| SilverTone | 84 | `[film_silvertone.pp]` |
| ChoppyBlackAndWhite | 94 | `[photocopy.pp]` |
| PhotoNegativeRedAndBlack | 104 | `[horror_movie_special.pp]` |
| PolarizedBlackAndWhite | 114 | `[contrast_a.pp]` |
| PolarizedRedAndBlue | 124 | `[ProFilm_psychedelic_blue_red.pp]` |
| DesaturatedRed | 134 | `[ProFilm_b.pp]` |
| DesaturatedBlue | 144 | `[desat_blue.pp]` |
| ContrastRed | 154 | `[film_contrast_red.pp]` |
| ContrastGreen | 164 | `[film_contrast_green.pp]` |
| ContrastBlue | 174 | `[film_contrast_blue.pp]` |
| GrainyFilm | 184 | `[film_16mm.pp]` |
| GrainyChromaticAbberation | 194 | `[shitty_tv.pp]` |
| Scanlines | 204 | `[video_a.pp]` |
| ScanlinesBlackAndWhite | 214 | `[video_bw.pp]` |
| ScanlinesBlue | 224 | `[film_blue_filter.pp]` |
| ScanlinesSecurity | 234 | `[video_security.pp]` |
| Trails | 244 | `[clean_trails.pp]` |
| TrailsLong | 252 | `[video_trails.pp]` |
| TrailsDesaturated | 253 | `[desat_posterize_trails.pp]` |
| TrailsFlickery | 254 | `[flicker_trails.pp]` |
| TrailsSpacey | 255 | `[space_woosh.pp]` |

## What YARG sends

sACN on one universe (multicast, about 44 frames/s): 8 positions × red/green/blue/yellow Stage Kit
LEDs, fog, strobe (off/slow/medium/fast/fastest), and the game channels: cue (a value per cue),
beatline (1 bar line, 11 beat), keyframe (3 next, 13 previous, 23 first), bonus effect (2), post-
processing, and bitmasks of the notes on drums / guitar / bass / keys. Triggers are pulses held for
YARG's `DMXPulseDuration` (60 ms by default), so two of the same within one pulse count as one.
Spotlights, sing-alongs and camera cuts aren't in YARG's sACN output (only its UDP data stream).

### The data stream (recommended)

YARG can also broadcast a UDP **data stream** (turn on Data Stream in YARG's settings; it goes to
port 36107, about 88 packets/s). The bridge always listens for it, and while it's arriving it drives
everything except the Stage Kit LEDs (which only sACN has, for the `stagekit` look); when it stops,
sACN takes over again within a second. The header shows "YARG data stream" while it's in charge.

Why it's better: every value has its own field, so YARG's fog/strobe writes can't clobber the game
channels (no channel setup at all); the tempo comes from the chart instead of being measured;
beats, keyframes and bonus effects are one-off events instead of 60 ms pulses, and quick notes
between packets are combined, so close hits aren't merged. It also carries pause, the screen (menu,
song, score), spotlight and sing-along performers, camera cuts and vocal pitch, kept in the state for
looks to use.

Its quirks: it's undocumented (the layout, datagram version 3, is read from YARG's
`DataStreamController`, so a YARG update could change it; other versions are ignored and sACN carries
on). Verse and Chorus arrive as a song section, apart from the lighting cue, so with `verse_chorus_looks`
on, going back to what either one still holds doesn't show (Frenzy, Verse, Frenzy stays on Verse). A song with
no lighting cues shows NoCue (the `band` look) until its first cue.

## Status

- Tested: cues arriving from real YARG; the sACN receiver, decoder, every look, the visualizer stream
  and live reload, with the simulator. Tests: `dotnet test tests/Lumenator.Tests`.
- Not tested: the FTDI cable output (written from the FTDI D2XX and Open DMX docs; it needs FTDI's
  driver, which provides `ftd2xx.dll`).

## Credits

Neal Manson's [YARG_sACN_to_DMX](https://github.com/Nyxyxylyth/YARG_sACN_to_DMX) (MIT), for the
idea, the Stage Kit-to-PAR mapping and the PAR profile; this project started as a fork of it and keeps
its history and license (see [LICENSE.txt](LICENSE.txt)).
