using System.Diagnostics;
using System.Text.Json.Serialization;
using Konata.Codec.Audio;
using Konata.Codec.Audio.Codecs;
using Lagrange.Core.Message;
using Lagrange.Core.Message.Entity;
using Lagrange.OneBot.Utility;

namespace Lagrange.OneBot.Message.Entity;

[Serializable]
public partial class RecordSegment(string url)
{
    public RecordSegment() : this("") { }

    [JsonPropertyName("file")][CQProperty] public string File { get; set; } = url;

    [JsonPropertyName("url")] public string Url { get; set; } = url;
}

[SegmentSubscriber(typeof(RecordEntity), "record")]
public partial class RecordSegment : SegmentBase
{
    private static readonly Lazy<string?> FfmpegExecutable = new(FindFfmpegExecutable);

    public override void Build(MessageBuilder builder, SegmentBase segment)
    {
        if (segment is RecordSegment recordSegment and not { File: "" } && CommonResolver.Resolve(recordSegment.File) is { } record)
        {
            byte[] silk = ConvertFormat(record, out double time);
            builder.Record(silk, (int)Math.Ceiling(time));
        }
    }

    public override SegmentBase FromEntity(MessageChain chain, IMessageEntity entity)
    {
        if (entity is not RecordEntity recordEntity) throw new ArgumentException("Invalid entity type.");

        return new RecordSegment(recordEntity.AudioUrl);
    }

    private static byte[] ConvertFormat(byte[] audio, out double audioTime)
    {
        var format = AudioHelper.DetectAudio(audio);
        switch (format) // Process
        {
            // Silk v3 for tx use
            case AudioHelper.AudioFormat.TenSilkV3:
                {
                    audioTime = AudioHelper.GetTenSilkTime(audio);
                    return audio;
                }

            // Amr format
            // We no need to convert it
            case AudioHelper.AudioFormat.Amr:
                {
                    audioTime = audio.Length / 1607.0;
                    return audio;
                }

            // Normal silk v3
            // We need to append a header 0x02
            // and remove 0xFFFF end for it
            case AudioHelper.AudioFormat.SilkV3:
                {
                    audio = [0x02, .. audio.AsSpan(0, audio.Length - 2)];
                    audioTime = AudioHelper.GetTenSilkTime(audio);
                    return audio;
                }

            // Need to convert
            case AudioHelper.AudioFormat.Wav:
            case AudioHelper.AudioFormat.Ogg:
            case AudioHelper.AudioFormat.Mp3:
                {
                    if (TryConvertWithFfmpeg(audio, out var wav))
                    {
                        return EncodeFormat(wav, AudioHelper.AudioFormat.Wav, out audioTime);
                    }

                    return EncodeFormat(audio, format, out audioTime);
                }

            // Cannot convert unknown type
            default:
                {
                    if (TryConvertWithFfmpeg(audio, out var wav))
                    {
                        return EncodeFormat(wav, AudioHelper.AudioFormat.Wav, out audioTime);
                    }

                    throw new Exception("Unknown Format");
                }
        }
    }

    private static byte[] EncodeFormat(byte[] audio, AudioHelper.AudioFormat format, out double audioTime)
    {
        if (format == AudioHelper.AudioFormat.Wav) audio = AudioHelper.NormalizeWaveForDecoder(audio);

        var input = new MemoryStream(audio);
        var output = new MemoryStream();

        var pipeline = new AudioPipeline() {
            format switch {
                AudioHelper.AudioFormat.Wav => new WavCodec.Decoder(input),
                AudioHelper.AudioFormat.Ogg => new VorbisCodec.Decoder(input),
                AudioHelper.AudioFormat.Mp3 => new Mp3Codec.Decoder(input),
                _ => throw new Exception("Unknown Format")
            },
            new AudioResampler(AudioInfo.SilkV3()),
            new SilkV3Codec.Encoder(),
            output
        };
        if (!pipeline.Start().Result) throw new Exception("Encode failed");

        audioTime = pipeline.GetAudioTime();
        return output.ToArray();
    }

    private static bool TryConvertWithFfmpeg(byte[] audio, out byte[] wav)
    {
        wav = [];
        string? ffmpeg = FfmpegExecutable.Value;
        if (ffmpeg == null) return false;

        string tempBase = Path.Combine(Path.GetTempPath(), $"lagrange-record-{Guid.NewGuid():N}");
        string inputPath = tempBase + ".input";
        string outputPath = tempBase + ".wav";

        try
        {
            System.IO.File.WriteAllBytes(inputPath, audio);

            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = ffmpeg,
                UseShellExecute = false,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            process.StartInfo.ArgumentList.Add("-hide_banner");
            process.StartInfo.ArgumentList.Add("-loglevel");
            process.StartInfo.ArgumentList.Add("error");
            process.StartInfo.ArgumentList.Add("-y");
            process.StartInfo.ArgumentList.Add("-i");
            process.StartInfo.ArgumentList.Add(inputPath);
            process.StartInfo.ArgumentList.Add("-vn");
            process.StartInfo.ArgumentList.Add("-acodec");
            process.StartInfo.ArgumentList.Add("pcm_s16le");
            process.StartInfo.ArgumentList.Add("-ac");
            process.StartInfo.ArgumentList.Add("1");
            process.StartInfo.ArgumentList.Add("-ar");
            process.StartInfo.ArgumentList.Add("16000");
            process.StartInfo.ArgumentList.Add(outputPath);

            process.Start();
            string error = process.StandardError.ReadToEnd();
            if (!process.WaitForExit(30_000))
            {
                process.Kill(true);
                return false;
            }

            if (process.ExitCode != 0 || !System.IO.File.Exists(outputPath)) return false;

            wav = System.IO.File.ReadAllBytes(outputPath);
            return wav.Length > 44 && AudioHelper.DetectAudio(wav) == AudioHelper.AudioFormat.Wav;
        }
        catch
        {
            return false;
        }
        finally
        {
            TryDelete(inputPath);
            TryDelete(outputPath);
        }
    }

    private static string? FindFfmpegExecutable()
    {
        string[] names = OperatingSystem.IsWindows() ? ["ffmpeg.exe", "ffmpeg"] : ["ffmpeg"];
        string? path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path)) return null;

        foreach (string dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (string name in names)
            {
                string candidate = Path.Combine(dir.Trim(), name);
                if (System.IO.File.Exists(candidate)) return candidate;
            }
        }

        return null;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (System.IO.File.Exists(path)) System.IO.File.Delete(path);
        }
        catch
        {
            // ignore temporary file cleanup errors
        }
    }
}
