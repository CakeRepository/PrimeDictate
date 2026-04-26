using System.IO;
using System.Media;
using System.Text;

namespace PrimeDictate;

internal enum DictationAudioCueKind
{
    Start = 0,
    Stop = 1
}

internal sealed class DictationAudioCuePlayer
{
    private const int SampleRate = 24_000;
    private const short BitsPerSample = 16;
    private const short ChannelCount = 1;
    private static readonly byte[] StartCueWave = BuildWaveFile(
    [
        CueSegment.Tone(415.30, 466.16, 0.05, 0.27),
        CueSegment.Silence(0.012),
        CueSegment.Tone(523.25, 587.33, 0.055, 0.24),
        CueSegment.Silence(0.01),
        CueSegment.Tone(659.25, 739.99, 0.085, 0.22)
    ]);
    private static readonly byte[] StopCueWave = BuildWaveFile(
    [
        CueSegment.Tone(698.46, 659.25, 0.05, 0.23),
        CueSegment.Silence(0.01),
        CueSegment.Tone(587.33, 523.25, 0.055, 0.20),
        CueSegment.Silence(0.012),
        CueSegment.Tone(466.16, 392.00, 0.09, 0.18)
    ]);

    public void Play(DictationAudioCueKind cueKind)
    {
        var waveData = cueKind == DictationAudioCueKind.Start
            ? StartCueWave
            : StopCueWave;

        _ = Task.Run(() =>
        {
            try
            {
                using var stream = new MemoryStream(waveData, writable: false);
                using var player = new SoundPlayer(stream);
                player.PlaySync();
            }
            catch (Exception ex)
            {
                AppLog.Error($"Audio cue playback failed: {ex.Message}");
            }
        });
    }

    private static byte[] BuildWaveFile(IReadOnlyList<CueSegment> segments)
    {
        var pcmBytes = new List<byte>();
        foreach (var segment in segments)
        {
            if (segment.IsSilence)
            {
                int silentSampleCount = Math.Max(1, (int)Math.Round(segment.DurationSeconds * SampleRate));
                pcmBytes.AddRange(new byte[silentSampleCount * sizeof(short)]);
                continue;
            }

            AppendTone(pcmBytes, segment);
        }

        var dataSize = pcmBytes.Count;
        using var stream = new MemoryStream(44 + dataSize);
        using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);

        writer.Write(Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(36 + dataSize);
        writer.Write(Encoding.ASCII.GetBytes("WAVE"));
        writer.Write(Encoding.ASCII.GetBytes("fmt "));
        writer.Write(16);
        writer.Write((short)1);
        writer.Write(ChannelCount);
        writer.Write(SampleRate);
        writer.Write(SampleRate * ChannelCount * (BitsPerSample / 8));
        writer.Write((short)(ChannelCount * (BitsPerSample / 8)));
        writer.Write(BitsPerSample);
        writer.Write(Encoding.ASCII.GetBytes("data"));
        writer.Write(dataSize);
        writer.Write(pcmBytes.ToArray());
        writer.Flush();

        return stream.ToArray();
    }

    private static void AppendTone(List<byte> pcmBytes, CueSegment segment)
    {
        int sampleCount = Math.Max(1, (int)Math.Round(segment.DurationSeconds * SampleRate));
        double attackSamples = Math.Max(1.0, sampleCount * 0.07);
        double releaseSamples = Math.Max(1.0, sampleCount * 0.22);
        double phase = 0;

        for (int sampleIndex = 0; sampleIndex < sampleCount; sampleIndex++)
        {
            double progress = sampleCount == 1 ? 1.0 : (double)sampleIndex / (sampleCount - 1);
            double scoop = 1.0 - (0.09 * Math.Exp(-progress * 18.0));
            double frequency = Lerp(segment.StartFrequencyHz, segment.EndFrequencyHz, progress) * scoop;
            phase += (2 * Math.PI * frequency) / SampleRate;

            double attackEnvelope = sampleIndex < attackSamples
                ? sampleIndex / attackSamples
                : 1.0;
            double releaseEnvelope = sampleIndex >= sampleCount - releaseSamples
                ? (sampleCount - sampleIndex - 1) / releaseSamples
                : 1.0;
            double envelope = Math.Max(0, Math.Min(attackEnvelope, releaseEnvelope));

            double shimmer =
                (0.90 * Math.Sin(phase)) +
                (0.16 * Math.Sin((phase * 0.5) + 0.2)) +
                (0.18 * Math.Sin((phase * 2.0) - 0.35)) +
                (0.07 * Math.Sin((phase * 3.0) + 1.1));
            double sampleValue = shimmer * segment.Amplitude * envelope * 0.78;
            short pcmSample = (short)Math.Clamp(
                sampleValue * short.MaxValue,
                short.MinValue,
                short.MaxValue);

            pcmBytes.Add((byte)(pcmSample & 0xFF));
            pcmBytes.Add((byte)((pcmSample >> 8) & 0xFF));
        }
    }

    private static double Lerp(double start, double end, double progress) =>
        start + ((end - start) * progress);

    private readonly record struct CueSegment(
        double StartFrequencyHz,
        double EndFrequencyHz,
        double DurationSeconds,
        double Amplitude,
        bool IsSilence)
    {
        public static CueSegment Tone(double startFrequencyHz, double endFrequencyHz, double durationSeconds, double amplitude) =>
            new(startFrequencyHz, endFrequencyHz, durationSeconds, amplitude, IsSilence: false);

        public static CueSegment Silence(double durationSeconds) =>
            new(0, 0, durationSeconds, 0, IsSilence: true);
    }
}
