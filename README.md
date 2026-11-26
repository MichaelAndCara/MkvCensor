# MkvCensor

A .NET 8 console app that takes an `.mkv` file, finds profanity in its subtitle track,
mutes the audio during those lines, and writes a censored version of the subtitles —
producing a new "clean" `.mkv`.

## How it works

1. Probes the file with `ffprobe` to find the audio and subtitle streams.
2. Extracts the chosen subtitle track to plain-text `.srt` (works for SubRip/ASS/SSA/MOV_TEXT;
   image-based subtitles like PGS/VOBSUB aren't supported since there's no text to scan).
3. Scans each subtitle cue against a word list (`profanity-words.txt` by default).
4. For every cue that matches, censors the words in the subtitle text and records that
   cue's time range as a window to silence in the audio.
5. Runs `ffmpeg` once to: copy the video stream untouched, apply a `volume=0` filter only
   during the flagged time windows on the target audio track (re-encoded — muting requires
   re-encoding, it can't be a stream copy), copy any other audio/subtitle tracks untouched,
   and swap in the censored subtitle track — placed first among the subtitle streams, flagged
   as the default track, and titled "\<original title> (Censored)" (or just "Censored" if the
   source track had no title), so it's both the first option in a player's subtitle menu and
   clearly labeled.

**Default behavior — line-level muting:** standard `.srt` subtitles only have start/end times
for a whole line, not for individual words. So by default, muting happens at the *cue* level —
if a line has one flagged word, the audio is silent for that whole line's duration (plus a
small padding). For typical dialogue pacing this is usually fine and barely noticeable; for
cues with a lot of unrelated dialogue packed around the flagged word, you may want to shorten
the cue in the subtitle first or reduce `--padding`.

**Optional — word-level muting (`--word-level`):** for tighter muting, the app can forced-align
each flagged cue's own text against a short audio clip around it, using a local wav2vec2 CTC
model, and mute only the matched word's actual span instead of the whole line. This is *forced*
alignment, not transcription — since the text is already known (from the subtitle), the model
only has to work out *when* each word was said, which is faster and far more accurate than
blind speech recognition. It runs fully locally via ONNX Runtime (optionally CUDA-accelerated),
no Python and no network calls at runtime. See `Assets/Models/README.txt` for how to get a
model — it's a one-time download, not something the app fetches itself. If alignment fails for
a particular cue (clip too short, unexpected output, etc.) that cue automatically falls back to
whole-line muting rather than aborting the run.

## Requirements

- .NET 8 SDK to build/run: https://dotnet.microsoft.com/download/dotnet/8.0
- `ffmpeg` and `ffprobe`. The app looks for them in this order:
  1. `--ffmpeg-path` / `--ffprobe-path`, if you pass them
  2. `Assets/ffmpeg(.exe)` and `Assets/ffprobe(.exe)` next to the executable
  3. `ffmpeg` / `ffprobe` on PATH

  Drop static builds from https://ffmpeg.org/download.html into the `Assets/`
  folder (see `Assets/README.txt`) and you don't need ffmpeg installed
  system-wide at all — the project file copies that folder next to the
  built/published executable automatically.
- Only if using `--word-level`: a CUDA-capable NVIDIA GPU with CUDA/cuDNN installed
  (falls back to CPU automatically otherwise, just slower) — see
  https://onnxruntime.ai/docs/install/ for the exact CUDA/cuDNN version the
  bundled `Microsoft.ML.OnnxRuntime.Gpu` package needs — plus a model file, see
  `Assets/Models/README.txt`.

## Build & run

```bash
dotnet build
dotnet run -- --input "movie.mkv" --output "movie.censored.mkv"
```

Or publish a standalone executable:

```bash
dotnet publish -c Release -r win-x64 --self-contained -p:PublishSingleFile=true
dotnet publish -c Release -r linux-x64 --self-contained -p:PublishSingleFile=true
dotnet publish -c Release -r osx-x64 --self-contained -p:PublishSingleFile=true
```

## Usage

```
MkvCensor --input movie.mkv [options]

  -i, --input <path>          Source .mkv file (required)
  -o, --output <path>         Output .mkv file (default: <input>.censored.mkv)
      --wordlist <path>       Text file, one word/phrase per line, '#' comments allowed
                              (default: profanity-words.txt next to the executable)
      --audio-index <n>       Which audio stream to mute, 0-based among audio streams (default: 0)
      --subtitle-index <n>    Which subtitle stream to scan, 0-based among subtitle streams
                              (default: first text-based subtitle track found)
      --keep-first-letter     Censor as "s***" instead of "****"
      --padding <seconds>     Extra silence padding before/after each muted cue (default: 0.15)
      --dry-run               Report what would be censored without producing an output file
      --limit <seconds>       Only render the first N seconds (quick test render, e.g. to check sync)
      --keep-temp             Don't delete the working directory (extracted/censored .srt files)
      --audio-encoder <name>  Codec for the re-encoded audio track (default: aac)
      --audio-bitrate <kbps>  Bitrate for the re-encoded audio track (default: 192)
      --ffmpeg-path <path>    Path to ffmpeg executable (default: "ffmpeg" on PATH)
      --ffprobe-path <path>   Path to ffprobe executable (default: "ffprobe" on PATH)
  -h, --help                  Show this help
```

### Examples

Preview what would be censored, no file written:

```bash
dotnet run -- --input movie.mkv --dry-run
```

Use a custom word list and keep the first letter visible in subtitles:

```bash
dotnet run -- --input movie.mkv --output movie.clean.mkv --wordlist my-words.txt --keep-first-letter
```

Multiple audio/subtitle tracks — target the second audio track and third subtitle track:

```bash
dotnet run -- --input movie.mkv --audio-index 1 --subtitle-index 2
```

Word-level muting (after putting a model in `Assets/Models/` — see its README):

```bash
dotnet run -- --input movie.mkv --word-level
```

## Customizing the word list

Edit `profanity-words.txt` (or point `--wordlist` at your own file). One entry per line;
multi-word phrases are supported ("oh my god"); lines starting with `#` are ignored.
Matching is whole-word/whole-phrase and case-insensitive, so it won't false-positive on
substrings inside unrelated words.

End an entry with `*` to also catch any suffix glued onto it: `fuck*` matches `fuck`,
`fucking`, `fucked`, `fucker`, etc. in one entry instead of listing each variant separately.
Leave the `*` off for an exact whole-word match only — worth doing for short roots that are
also the start of innocent words (`hell*` would flag `hello`; `ass*` would flag `assassin`),
which is why the shipped list keeps those two as exact matches rather than wildcards.

## Notes / things you might want to extend

- Only one audio track and one subtitle track are censored per run; other tracks are copied
  through untouched. If you need multiple audio tracks muted in one pass, run the tool once
  and then run it again on the output pointing `--audio-index` at the next track (you'll need
  `--subtitle-index` to point at the *already-censored* track the second time, or just script
  around it).
- Muting uses `volume=0` rather than a beep tone. If you'd rather have a bleep sound than
  silence, swap the `volume` filter in `FfmpegService.BuildMuteFilterExpression` for a mix
  with a short sine-tone input during the same windows.
- The audio track being muted is always re-encoded (video and other tracks stay as stream
  copies), since sample-accurate muting isn't possible without decoding.

## Troubleshooting

**Plays fine in VLC but stutters, stalls, or stops partway through in other players.** VLC's
demuxer is unusually forgiving of timestamp irregularities; most other players (native OS
players, phones, smart TVs, mpv, hardware players) are much stricter. This mismatch is a known
sharp edge of mixing a stream-copied video track with a re-encoded audio track — their
timestamps can end up very slightly out of step even though nothing looks obviously wrong. The
tool applies `-max_interleave_delta 0` for this. It deliberately does not use
`-avoid_negative_ts make_zero`, `-fflags +genpts` or `aresample=async`/`first_pts`: those
move timestamps around and can shift audio relative to video (A/V misalignment). If you still
hit stalling on a particular file, a next diagnostic step is comparing `ffprobe -show_entries
packet=pts_time,dts_time,stream_index -of csv <file>` between the original and the censored
output to look for non-monotonic or drifting timestamps, or testing in `mpv -v` (which surfaces
demuxer warnings VLC hides).
