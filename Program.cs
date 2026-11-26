using System.Text.RegularExpressions;
using MkvCensor.Models;
using MkvCensor.Services;

namespace MkvCensor;

internal static class Program
{
    private static int Main(string[] args)
    {
        Options options;
        try
        {
            options = Options.Parse(args);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("Argument error: " + ex.Message);
            Options.PrintUsage();
            return 1;
        }

        if (options.ShowHelp)
        {
            Options.PrintUsage();
            return 0;
        }

        try
        {
            Run(options);
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine("ERROR: " + ex.Message);
            return 1;
        }
    }

    private static void Run(Options o)
    {
        var ffmpeg = new FfmpegService(o.FfmpegPath, o.FfprobePath);
        var subtitles = new SubtitleService();

        Console.WriteLine($"Using ffmpeg:  {o.FfmpegPath}");
        Console.WriteLine($"Using ffprobe: {o.FfprobePath}");
        ffmpeg.VerifyToolsAvailable();

        Console.WriteLine($"Probing '{o.InputPath}'...");
        var streams = ffmpeg.ProbeStreams(o.InputPath);

        var audioStreams = streams.Where(s => s.CodecType == "audio").ToList();
        var subtitleStreams = streams.Where(s => s.CodecType == "subtitle").ToList();

        if (audioStreams.Count == 0)
            throw new InvalidOperationException("No audio streams found in the input file.");
        if (subtitleStreams.Count == 0)
            throw new InvalidOperationException("No subtitle streams found in the input file.");

        int relativeAudioIndex = o.AudioIndex ?? 0;
        if (relativeAudioIndex < 0 || relativeAudioIndex >= audioStreams.Count)
            throw new InvalidOperationException(
                $"--audio-index {relativeAudioIndex} is out of range (file has {audioStreams.Count} audio stream(s)).");

        int relativeSubtitleIndex;
        if (o.SubtitleIndex.HasValue)
        {
            relativeSubtitleIndex = o.SubtitleIndex.Value;
            if (relativeSubtitleIndex < 0 || relativeSubtitleIndex >= subtitleStreams.Count)
                throw new InvalidOperationException(
                    $"--subtitle-index {relativeSubtitleIndex} is out of range (file has {subtitleStreams.Count} subtitle stream(s)).");
        }
        else
        {
            var firstText = subtitleStreams.FindIndex(s => s.IsTextSubtitle);
            if (firstText == -1)
                throw new InvalidOperationException(
                    "No text-based subtitle stream found (only image-based subtitles like PGS/VOBSUB are present). " +
                    "Those can't be scanned for words — supply a text subtitle track with --subtitle-index, " +
                    "or provide an external .srt alongside the file.");
            relativeSubtitleIndex = firstText;
        }

        if (!subtitleStreams[relativeSubtitleIndex].IsTextSubtitle)
            throw new InvalidOperationException(
                $"Subtitle stream {relativeSubtitleIndex} is not a text-based format " +
                $"(codec: {subtitleStreams[relativeSubtitleIndex].CodecName}). Choose a different --subtitle-index.");

        Console.WriteLine($"Using audio stream {relativeAudioIndex} " +
                           $"(language: {audioStreams[relativeAudioIndex].Language ?? "unknown"})");
        Console.WriteLine($"Using subtitle stream {relativeSubtitleIndex} " +
                           $"(language: {subtitleStreams[relativeSubtitleIndex].Language ?? "unknown"}, " +
                           $"codec: {subtitleStreams[relativeSubtitleIndex].CodecName})");

        var workDir = Directory.CreateTempSubdirectory("mkvcensor_").FullName;
        try
        {
            var rawSrtPath = Path.Combine(workDir, "original.srt");
            var absoluteSubIndex = subtitleStreams[relativeSubtitleIndex].Index;

            Console.WriteLine("Extracting subtitle track...");
            ffmpeg.ExtractSubtitleToSrt(o.InputPath, absoluteSubIndex, rawSrtPath);

            var entries = subtitles.ParseSrt(rawSrtPath);
            Console.WriteLine($"Parsed {entries.Count} subtitle cue(s).");

            var profanity = new ProfanityService(o.WordListPath);
            Console.WriteLine($"Loaded {profanity.Words.Count} word/phrase entr(y/ies) from '{o.WordListPath}'.");

            using var aligner = o.WordLevel
                ? new Wav2Vec2Aligner(
                    o.AlignmentModelPath, o.AlignmentVocabPath, useCuda: !o.AlignmentUseCpu,
                    wordDelimiter: o.AlignmentWordDelimiter, blankToken: o.AlignmentBlankToken)
                : null;
            if (aligner != null)
                Console.WriteLine("Word-level muting enabled — flagged cues will be forced-aligned individually.");

            var rawRanges = new List<MuteRange>();
            int censoredCount = 0;

            foreach (var entry in entries)
            {
                var original = entry.Text;
                if (!profanity.ContainsMatch(original)) continue;

                var matched = string.Join(", ", profanity.FindMatches(original).Distinct(StringComparer.OrdinalIgnoreCase));
                var censored = profanity.Censor(original, o.KeepFirstLetter);
                entry.Lines = censored.Split('\n').ToList();
                entry.WasCensored = true;
                censoredCount++;

                var lineStart = Math.Max(0, entry.Start.TotalSeconds - o.PaddingSeconds);
                var lineEnd = entry.End.TotalSeconds + o.PaddingSeconds;

                List<MuteRange>? wordRanges = aligner != null
                    ? TryGetWordLevelRanges(entry, original, profanity, ffmpeg, aligner, relativeAudioIndex, o)
                    : null;

                if (wordRanges != null)
                {
                    rawRanges.AddRange(wordRanges);
                    Console.WriteLine($"  [{FormatTime(entry.Start)} - {FormatTime(entry.End)}] matched: {matched} " +
                                       $"(word-level: {wordRanges.Count} tight range(s))");
                }
                else
                {
                    rawRanges.Add(new MuteRange(lineStart, lineEnd));
                    var note = aligner != null ? " (alignment fell back to whole line)" : "";
                    Console.WriteLine($"  [{FormatTime(entry.Start)} - {FormatTime(entry.End)}] matched: {matched}{note}");
                }
            }

            Console.WriteLine($"Censored {censoredCount} of {entries.Count} cue(s).");

            var mergedRanges = MergeRanges(rawRanges, o.PaddingSeconds);
            Console.WriteLine($"Will mute {mergedRanges.Count} audio range(s) totalling " +
                               $"{mergedRanges.Sum(r => r.DurationSeconds):0.0}s.");

            var censoredSrtPath = Path.Combine(workDir, "censored.srt");
            subtitles.WriteSrt(censoredSrtPath, entries);

            if (o.DryRun)
            {
                Console.WriteLine();
                Console.WriteLine("Dry run only — no output file was produced.");
                Console.WriteLine($"Censored subtitle preview saved at: {censoredSrtPath}");
                return;
            }

            if (censoredCount == 0)
            {
                Console.WriteLine("No matches found — copying the input file through unchanged.");
                File.Copy(o.InputPath, o.OutputPath, overwrite: true);
                return;
            }

            Console.WriteLine("Rendering final file (this can take a while for long videos)...");
            ffmpeg.RenderCensoredFile(
                inputPath: o.InputPath,
                censoredSrtPath: censoredSrtPath,
                outputPath: o.OutputPath,
                relativeAudioIndex: relativeAudioIndex,
                totalAudioStreams: audioStreams.Count,
                relativeSubtitleIndex: relativeSubtitleIndex,
                totalSubtitleStreams: subtitleStreams.Count,
                muteRanges: mergedRanges,
                limitSeconds: o.LimitSeconds,
                sourceSubtitleTitle: subtitleStreams[relativeSubtitleIndex].Title,
                sourceSubtitleLanguage: subtitleStreams[relativeSubtitleIndex].Language,
                audioEncoder: o.AudioEncoder,
                audioBitrateKbps: o.AudioBitrateKbps);

            Console.WriteLine();
            Console.WriteLine($"Done. Output written to: {o.OutputPath}");
        }
        finally
        {
            if (!o.KeepTempFiles)
            {
                try { Directory.Delete(workDir, recursive: true); } catch { /* best effort */ }
            }
            else
            {
                Console.WriteLine($"Temp files kept at: {workDir}");
            }
        }
    }

    /// <summary>
    /// Sorts and merges overlapping or near-adjacent mute ranges so ffmpeg
    /// doesn't get handed redundant/overlapping filter windows.
    /// </summary>
    private static List<MuteRange> MergeRanges(List<MuteRange> ranges, double paddingSeconds)
    {
        if (ranges.Count == 0) return ranges;

        var sorted = ranges.OrderBy(r => r.StartSeconds).ToList();
        var merged = new List<MuteRange> { sorted[0] };

        // Ranges closer together than the padding amount get merged into one
        // window rather than left as two overlapping/adjacent filters.
        var gapThreshold = Math.Max(paddingSeconds, 0.05);

        foreach (var current in sorted.Skip(1))
        {
            var last = merged[^1];
            if (current.StartSeconds <= last.EndSeconds + gapThreshold)
            {
                merged[^1] = new MuteRange(last.StartSeconds, Math.Max(last.EndSeconds, current.EndSeconds));
            }
            else
            {
                merged.Add(current);
            }
        }

        return merged;
    }

    /// <summary>
    /// Forced-aligns one cue's own text against a short audio clip around
    /// it, then returns one tight <see cref="MuteRange"/> per profanity
    /// match (which may itself span a multi-word phrase) instead of one
    /// range covering the whole line. Returns null — meaning "fall back to
    /// whole-line muting for this cue" — on anything from a too-short clip
    /// to an alignment mismatch, so one bad cue never aborts the run.
    /// </summary>
    private static List<MuteRange>? TryGetWordLevelRanges(
        SubtitleEntry entry, string originalText, ProfanityService profanity,
        FfmpegService ffmpeg, Wav2Vec2Aligner aligner, int relativeAudioIndex, Options o)
    {
        try
        {
            var wordMatches = Regex.Matches(originalText, @"\S+");
            if (wordMatches.Count == 0) return null;
            var words = wordMatches.Select(m => m.Value).ToList();

            double clipStart = Math.Max(0, entry.Start.TotalSeconds - o.AlignmentClipPaddingSeconds);
            double clipEnd = entry.End.TotalSeconds + o.AlignmentClipPaddingSeconds;
            double duration = clipEnd - clipStart;
            if (duration <= 0) return null;

            var pcm = ffmpeg.ExtractPcm16kMono(o.InputPath, relativeAudioIndex, clipStart, duration);
            if (pcm.Length < 1600) return null; // under 0.1s — not worth aligning

            var aligned = aligner.TryAlignWords(pcm, words);
            if (aligned == null) return null;

            double wordPaddingSeconds = o.AlignmentWordPaddingMs / 1000.0;
            var ranges = new List<MuteRange>();
            var lineStart = Math.Max(0, entry.Start.TotalSeconds - o.PaddingSeconds);
            var lineEnd = entry.End.TotalSeconds + o.PaddingSeconds;

            foreach (Match profanityMatch in profanity.FindMatchPositions(originalText))
            {
                var coveredWordIndices = new List<int>();
                for (int i = 0; i < wordMatches.Count; i++)
                {
                    var wordMatch = wordMatches[i];
                    bool overlaps = wordMatch.Index < profanityMatch.Index + profanityMatch.Length
                        && profanityMatch.Index < wordMatch.Index + wordMatch.Length;
                    if (overlaps) coveredWordIndices.Add(i);
                }
                if (coveredWordIndices.Count == 0) continue;

                double? minStart = null, maxEnd = null;
                bool allAligned = true;
                foreach (var idx in coveredWordIndices)
                {
                    if (aligned.TryGetValue(idx, out var result))
                    {
                        var s = clipStart + result.StartSeconds;
                        var e = clipStart + result.EndSeconds;
                        minStart = minStart.HasValue ? Math.Min(minStart.Value, s) : s;
                        maxEnd = maxEnd.HasValue ? Math.Max(maxEnd.Value, e) : e;
                    }
                    else
                    {
                        allAligned = false;
                    }
                }

                ranges.Add(allAligned && minStart.HasValue && maxEnd.HasValue
                    ? new MuteRange(Math.Max(0, minStart.Value - wordPaddingSeconds), maxEnd.Value + wordPaddingSeconds)
                    : new MuteRange(lineStart, lineEnd)); // this occurrence's words didn't fully align — be safe
            }

            return ranges.Count > 0 ? ranges : null;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"    (alignment error, falling back to whole-line muting for this cue: {ex.Message})");
            return null;
        }
    }

    private static string FormatTime(TimeSpan t) => t.ToString(@"hh\:mm\:ss\,fff");
}
