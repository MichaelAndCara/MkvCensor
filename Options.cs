namespace MkvCensor;

internal class Options
{
    public string InputPath { get; private set; } = "";
    public string OutputPath { get; private set; } = "";
    public string WordListPath { get; private set; } = "profanity-words.txt";
    public int? AudioIndex { get; private set; }
    public int? SubtitleIndex { get; private set; }
    public bool KeepFirstLetter { get; private set; }
    public double PaddingSeconds { get; private set; } = 0.15;
    public string FfmpegPath { get; private set; } = "";
    public string FfprobePath { get; private set; } = "";
    public bool DryRun { get; private set; }
    public bool KeepTempFiles { get; private set; }
    public string AudioEncoder { get; private set; } = "aac";
    public int AudioBitrateKbps { get; private set; } = 192;
    public bool ShowHelp { get; private set; }
    public double? LimitSeconds { get; private set; }

    public bool WordLevel { get; private set; }
    public string AlignmentModelPath { get; private set; } = "";
    public string AlignmentVocabPath { get; private set; } = "";
    public bool AlignmentUseCpu { get; private set; }
    public double AlignmentClipPaddingSeconds { get; private set; } = 0.4;
    public double AlignmentWordPaddingMs { get; private set; } = 60;
    public string AlignmentBlankToken { get; private set; } = "<pad>";
    public char AlignmentWordDelimiter { get; private set; } = '|';

    public static Options Parse(string[] args)
    {
        var o = new Options();
        if (args.Length == 0)
        {
            o.ShowHelp = true;
            return o;
        }

        string? input = null, output = null;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "-h":
                case "--help":
                    o.ShowHelp = true;
                    return o;

                case "--input":
                case "-i":
                    input = RequireValue(args, ref i, "--input");
                    break;

                case "--output":
                case "-o":
                    output = RequireValue(args, ref i, "--output");
                    break;

                case "--wordlist":
                    o.WordListPath = RequireValue(args, ref i, "--wordlist");
                    break;

                case "--audio-index":
                    o.AudioIndex = int.Parse(RequireValue(args, ref i, "--audio-index"));
                    break;

                case "--subtitle-index":
                    o.SubtitleIndex = int.Parse(RequireValue(args, ref i, "--subtitle-index"));
                    break;

                case "--keep-first-letter":
                    o.KeepFirstLetter = true;
                    break;

                case "--padding":
                    o.PaddingSeconds = double.Parse(RequireValue(args, ref i, "--padding"));
                    break;

                case "--ffmpeg-path":
                    o.FfmpegPath = RequireValue(args, ref i, "--ffmpeg-path");
                    break;

                case "--ffprobe-path":
                    o.FfprobePath = RequireValue(args, ref i, "--ffprobe-path");
                    break;

                case "--limit":
                    o.LimitSeconds = double.Parse(RequireValue(args, ref i, "--limit"));
                    break;

                case "--dry-run":
                    o.DryRun = true;
                    break;

                case "--keep-temp":
                    o.KeepTempFiles = true;
                    break;

                case "--audio-encoder":
                    o.AudioEncoder = RequireValue(args, ref i, "--audio-encoder");
                    break;

                case "--audio-bitrate":
                    o.AudioBitrateKbps = int.Parse(RequireValue(args, ref i, "--audio-bitrate"));
                    break;

                case "--word-level":
                    o.WordLevel = true;
                    break;

                case "--alignment-model":
                    o.AlignmentModelPath = RequireValue(args, ref i, "--alignment-model");
                    break;

                case "--alignment-vocab":
                    o.AlignmentVocabPath = RequireValue(args, ref i, "--alignment-vocab");
                    break;

                case "--alignment-cpu":
                    o.AlignmentUseCpu = true;
                    break;

                case "--alignment-clip-padding":
                    o.AlignmentClipPaddingSeconds = double.Parse(RequireValue(args, ref i, "--alignment-clip-padding"));
                    break;

                case "--alignment-word-padding":
                    o.AlignmentWordPaddingMs = double.Parse(RequireValue(args, ref i, "--alignment-word-padding"));
                    break;

                case "--alignment-blank-token":
                    o.AlignmentBlankToken = RequireValue(args, ref i, "--alignment-blank-token");
                    break;

                case "--alignment-word-delimiter":
                    o.AlignmentWordDelimiter = RequireValue(args, ref i, "--alignment-word-delimiter")[0];
                    break;

                default:
                    throw new ArgumentException($"Unrecognized argument: {args[i]}");
            }
        }

        if (o.ShowHelp) return o;

        if (string.IsNullOrWhiteSpace(input))
            throw new ArgumentException("--input is required.");
        if (!File.Exists(input))
            throw new ArgumentException($"Input file not found: {input}");
        o.InputPath = input;

        o.OutputPath = string.IsNullOrWhiteSpace(output)
            ? Path.Combine(
                Path.GetDirectoryName(Path.GetFullPath(input)) ?? ".",
                Path.GetFileNameWithoutExtension(input) + ".censored.mkv")
            : output;

        // Resolve ffmpeg/ffprobe last: an explicit --ffmpeg-path/--ffprobe-path
        // (already captured in o.FfmpegPath/o.FfprobePath above) always wins;
        // otherwise prefer the copy bundled in Assets/, falling back to PATH.
        o.FfmpegPath = ToolLocator.ResolveFfmpeg(string.IsNullOrWhiteSpace(o.FfmpegPath) ? null : o.FfmpegPath);
        o.FfprobePath = ToolLocator.ResolveFfprobe(string.IsNullOrWhiteSpace(o.FfprobePath) ? null : o.FfprobePath);

        if (string.IsNullOrWhiteSpace(o.AlignmentModelPath))
            o.AlignmentModelPath = Path.Combine(AppContext.BaseDirectory, "Assets", "Models", "model.onnx");
        if (string.IsNullOrWhiteSpace(o.AlignmentVocabPath))
            o.AlignmentVocabPath = Path.Combine(AppContext.BaseDirectory, "Assets", "Models", "vocab.json");

        if (o.WordLevel)
        {
            if (!File.Exists(o.AlignmentModelPath))
                throw new ArgumentException(
                    $"--word-level was given but no alignment model was found at '{o.AlignmentModelPath}'. " +
                    "Put a wav2vec2 CTC ONNX model there (see Assets/Models/README.txt) or pass --alignment-model.");
            if (!File.Exists(o.AlignmentVocabPath))
                throw new ArgumentException(
                    $"--word-level was given but no vocab.json was found at '{o.AlignmentVocabPath}'. " +
                    "Put the model's vocab.json there (see Assets/Models/README.txt) or pass --alignment-vocab.");
        }

        return o;
    }

    private static string RequireValue(string[] args, ref int i, string flag)
    {
        if (i + 1 >= args.Length)
            throw new ArgumentException($"{flag} requires a value.");
        i++;
        return args[i];
    }

    public static void PrintUsage()
    {
        Console.WriteLine("""
            MkvCensor - mute profanity in the audio track and censor it in the subtitles of an MKV file.

            REQUIRED:
              ffmpeg and ffprobe. Resolved in this order:
                1. --ffmpeg-path / --ffprobe-path, if given
                2. Assets/ffmpeg(.exe) and Assets/ffprobe(.exe) next to this executable, if present
                3. "ffmpeg" / "ffprobe" on PATH
              https://ffmpeg.org/download.html

            USAGE:
              MkvCensor --input movie.mkv [options]

            OPTIONS:
              -i, --input <path>          Source .mkv file (required)
              -o, --output <path>         Output .mkv file (default: <input>.censored.mkv)
                  --wordlist <path>       Text file, one word/phrase per line, '#' comments allowed,
                                          trailing '*' matches suffixes too (e.g. "fuck*")
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
                  --ffmpeg-path <path>    Path to ffmpeg executable (overrides Assets/ + PATH lookup)
                  --ffprobe-path <path>   Path to ffprobe executable (overrides Assets/ + PATH lookup)
              -h, --help                  Show this help

            WORD-LEVEL MUTING (optional, off by default):
              By default muting happens per subtitle cue (see NOTES below). --word-level instead
              forced-aligns each flagged cue's own text against a short audio clip around it, using
              a local wav2vec2 CTC model (see Assets/Models/README.txt for how to get one), and mutes
              only the matched word's actual span. Needs a wav2vec2 CTC ONNX model + vocab.json.

                  --word-level                  Mute only the exact flagged word(s), not the whole cue
                  --alignment-model <path>      wav2vec2 ONNX model (default: Assets/Models/model.onnx)
                  --alignment-vocab <path>      Matching vocab.json (default: Assets/Models/vocab.json)
                  --alignment-cpu                Force CPU even if a CUDA GPU is available
                  --alignment-clip-padding <s>   Context padded onto each cue before aligning it (default: 0.4)
                  --alignment-word-padding <ms>  Padding around the aligned word before muting (default: 60)
                  --alignment-blank-token <tok>  Vocab entry used as the CTC blank (default: <pad>)
                  --alignment-word-delimiter <c> Vocab character used as the word boundary (default: |)

              If alignment fails for a given cue (clip too short, unexpected output, etc.) that cue
              falls back to whole-line muting automatically — it won't abort the run.

            NOTES:
              Muting works at subtitle-cue granularity, not exact word timing — standard .srt files
              only carry start/end times for whole lines, not individual words. Each cue containing a
              flagged word gets its whole audio window silenced (plus --padding), so a line with one
              bad word muted will briefly go silent for that whole line. For most content this reads
              fine; if a cue is packed with unrelated dialogue around the flagged word you may want to
              tighten --padding or edit the subtitle file's cue boundaries first.
            """);
    }
}
