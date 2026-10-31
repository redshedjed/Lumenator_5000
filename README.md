# YARG DMX interpreter

A fork of [Nyxyxylyth/YARG_sACN_to_DMX](https://github.com/Nyxyxylyth/YARG_sACN_to_DMX). The original
copied YARG's Stage Kit lights straight onto 2, 4 or 8 PARs (its README is in
[README.original.md](README.original.md)). This version *interprets* what YARG sends: each lighting cue
in the song's VENUE track picks a **look** from `rig.json`, and YARG's beat, keyframe and drum
triggers drive it.

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
dotnet test tests/Interpreter.Tests
```

Options: `--rig path/to/rig.json` (default: `rig.json` in the current folder), `--port 8080`,
`--no-visualizer`.

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

## rig.json

- `fixtures`: name, DMX `address`, `profile`, and optional `x`/`y` for the visualizer (0-1).
  Listed left to right; "halves" and "alternate" go by this order.
- `profiles`: channel offsets per fixture type (`dimmer`, `red`, `green`, `blue`, optional `white`,
  and `fixed` channels held at a value). `par7` is the original's 7-channel PAR.
- `palette`: colour names. `red`/`green`/`blue`/`yellow` are also the Stage Kit lamp colours.
- `looks`: what the lights do while a cue is up.
- `cues`: YARG cue to look; `"*"` covers every other cue.
- `strobe_hz`: YARG's strobe cues become a white software strobe over any look (0 to ignore them).
- `output`: `ftdi` (USB-DMX cable), `sacn_universe` (also send the result as sACN, e.g. to QLC+ or
  a network DMX node), `fps`, `visualizer_port`.

### Looks

| type | does | uses |
|---|---|---|
| `stagekit` | YARG's own Stage Kit pattern, positions folded onto your fixtures (i, i+n...) | palette red/green/blue/yellow |
| `solid` | colours by pattern | `colors`, `pattern` |
| `pulse` | colours by pattern, brightness kicked on each trigger, fading to `floor` | `trigger`, `decay`, `floor`, `accent` (downbeat louder), `pattern: chase` steps a light per trigger |
| `flash` | first colour flashes on each trigger over the second at `floor` | `trigger`, `decay` |
| `chaos` | a new random draw per trigger: `density` of the lights, random colours (random hues if `colors` is empty) | `trigger`, `density`, `decay`, `floor` |
| `police` | two sides take turns, `flashes` each, side colours `colors[0]` / `colors[1]` | `pattern` (halves / alternate), `trigger: rate` + `rate_hz`, or any trigger = paced to the beat |
| `blackout` | all off | |

Triggers: `beat`, `downbeat`, `keyframe`, `snare`, `kick`, `cymbal`, `bonus`, `guitar`, `bass`,
`keys`, or `rate` (a clock at `rate_hz`). Keyframes come from the chart (VENUE notes 48-50), so they
land exactly where they were charted; the drum triggers come from YARG's drum channel.

The default `rig.json` maps `Intro` to a green/purple pulse, the blackouts to black, `Stomp` /
`Dischord` to a white flash per keyframe, `Frenzy` to chaos, `Searchlights` to red/blue police
lights, and everything else to the Stage Kit pattern.

## What YARG sends

sACN on one universe (multicast, about 44 frames/s): 8 positions × red/green/blue/yellow Stage Kit
LEDs, fog, strobe (off/slow/medium/fast/fastest), and the game channels: cue (a value per cue),
beatline (1 bar line, 11 beat), keyframe (3 next, 13 previous, 23 first), bonus effect (2), post-
processing, and bitmasks of the notes on drums / guitar / bass / keys. Triggers are pulses held for
YARG's `DMXPulseDuration` (60 ms by default), so two of the same within one pulse count as one.
Spotlights, sing-alongs and camera cuts aren't in YARG's sACN output (only its UDP data stream).

## Status

- Tested: the sACN receiver, decoder, every look, the visualizer stream and live reload, with the
  simulator. Tests: `dotnet test tests/Interpreter.Tests`.
- Not tested: real YARG output, and the FTDI cable output (written from the FTDI D2XX and Open DMX
  docs; it needs FTDI's driver, which provides `ftd2xx.dll`).

## Credits

Neal Manson's [YARG_sACN_to_DMX](https://github.com/Nyxyxylyth/YARG_sACN_to_DMX) (MIT), for the
idea, the Stage Kit-to-PAR mapping and the PAR profile.
