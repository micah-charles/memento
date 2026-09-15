using System.Buffers.Binary;
using System.Diagnostics;
using System.Text.Json;
using Memento.Core.Audio;

namespace Memento.Core.Companion;

public sealed record SpeechSegment(long StartMs, long EndMs, string Text);
public sealed record LocalTranscript(string Text, IReadOnlyList<SpeechSegment> Segments);
public interface ILocalTranscriptionProvider
{
    Task<LocalTranscript> TranscribeAsync(byte[] pcm48Khz, CancellationToken cancellationToken = default);
}
public interface IUtteranceDetector
{
    bool HasSpeech { get; }
    bool Append(ReadOnlySpan<byte> pcm48Khz);
    void Reset();
}

/// <summary>Conservative energy endpointing; it does not identify speakers or edit archived PCM.</summary>
public sealed class EnergyUtteranceDetector(double silenceSeconds = 1.8, double threshold = 0.012) : IUtteranceDetector
{
    private long _silence;
    private long _voiced;
    public bool HasSpeech => _voiced >= 4800;
    public bool Append(ReadOnlySpan<byte> pcm)
    {
        if (pcm.Length == 0 || pcm.Length % 2 != 0) return false;
        double energy = 0;
        for (var i = 0; i < pcm.Length; i += 2) { var sample = BinaryPrimitives.ReadInt16LittleEndian(pcm[i..]) / 32768.0; energy += sample * sample; }
        var speech = Math.Sqrt(energy / (pcm.Length / 2)) > threshold;
        if (speech) { _voiced += pcm.Length / 2; _silence = 0; }
        else if (HasSpeech) _silence += pcm.Length / 2;
        return HasSpeech && _silence >= silenceSeconds * 48000;
    }
    public void Reset() { _silence = 0; _voiced = 0; }
}

public sealed class WhisperLocalTranscription(string executable, string modelPath, string scratchDirectory) : ILocalTranscriptionProvider
{
    public async Task<LocalTranscript> TranscribeAsync(byte[] pcm48Khz, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(executable) || !File.Exists(modelPath)) throw new InvalidOperationException("本機語音辨識未設定，請先安裝 Whisper 語音檔。");
        Directory.CreateDirectory(scratchDirectory);
        var stem = Path.Combine(scratchDirectory, Guid.NewGuid().ToString("N"));
        var wav = stem + ".wav";
        try
        {
            using (var stream = File.Create(wav))
            {
                var count = pcm48Khz.Length / 6;
                PcmWaveWriter.WriteHeader(stream, new(16000, 1, 16), count * 2L);
                var downsampled = new byte[count * 2];
                for (var i = 0; i < count; i++)
                {
                    var offset = i * 6;
                    var sum = BinaryPrimitives.ReadInt16LittleEndian(pcm48Khz.AsSpan(offset)) + BinaryPrimitives.ReadInt16LittleEndian(pcm48Khz.AsSpan(offset + 2)) + BinaryPrimitives.ReadInt16LittleEndian(pcm48Khz.AsSpan(offset + 4));
                    BinaryPrimitives.WriteInt16LittleEndian(downsampled.AsSpan(i * 2), (short)(sum / 3));
                }
                stream.Write(downsampled);
            }
            var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var arg in new[] { "-m", modelPath, "-f", wav, "-l", "auto", "-oj", "-of", stem, "-t", Math.Clamp(Environment.ProcessorCount / 2, 1, 8).ToString() }) start.ArgumentList.Add(arg);
            using var process = Process.Start(start) ?? throw new InvalidOperationException("Whisper could not start.");
            var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); timeout.CancelAfter(TimeSpan.FromMinutes(3));
            try { await process.WaitForExitAsync(timeout.Token); await Task.WhenAll(stdout, stderr); }
            catch { if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); } throw; }
            if (process.ExitCode != 0 || !File.Exists(stem + ".json")) throw new InvalidOperationException("本機語音辨識未完成；原聲已保存。");
            return Parse(await File.ReadAllTextAsync(stem + ".json", cancellationToken));
        }
        finally { foreach (var extension in new[] { ".wav", ".json" }) if (File.Exists(stem + extension)) File.Delete(stem + extension); }
    }
    public static LocalTranscript Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var segments = doc.RootElement.GetProperty("transcription").EnumerateArray().Select(s => new SpeechSegment(s.GetProperty("offsets").GetProperty("from").GetInt64(), s.GetProperty("offsets").GetProperty("to").GetInt64(), s.GetProperty("text").GetString() ?? "")).ToArray();
        return new(string.Concat(segments.Select(s => s.Text)).Trim(), segments);
    }
}
