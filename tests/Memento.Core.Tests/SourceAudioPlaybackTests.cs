using Memento.Core.Audio;
using Memento.Core.Domain;

namespace Memento.Core.Tests;

public sealed class SourceAudioPlaybackTests
{
    [Fact]
    public async Task Source_playback_rejects_tampered_audio_before_opening_output()
    {
        var root = Path.Combine(Path.GetTempPath(), "memento-source-playback-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var started = DateTimeOffset.UtcNow;
            var format = new PcmWaveFormat(8000, 1, 16);
            using var writer = PcmWaveWriter.Create(root, "session-playback", started, format, "source-playback");
            writer.Append(new byte[format.BlockAlign * 4]);
            var asset = writer.FinalizeAsset();
            var bytes = File.ReadAllBytes(asset.FilePath);
            bytes[^1] ^= 0x01;
            File.WriteAllBytes(asset.FilePath, bytes);

            var source = new SourceMetadata(asset.SourceId, "audio", "session-playback", null, asset.FilePath, "PCM WAV", format.SampleRate, format.Channels, format.BitsPerSample, asset.ByteLength, asset.DurationMs, asset.Sha256, asset.StartedAt, asset.FinalizedAt, "finalized", started);

            await Assert.ThrowsAsync<InvalidDataException>(() => new WaveFileSourceAudioPlayback().PlayAsync(source));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Source_playback_rejects_missing_audio()
    {
        var source = new SourceMetadata("source-missing-playback", "audio", "session", null, Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".wav"), "PCM WAV", 8000, 1, 16, 44, 0, "abc", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "finalized", DateTimeOffset.UtcNow);

        await Assert.ThrowsAsync<FileNotFoundException>(() => new WaveFileSourceAudioPlayback().PlayAsync(source));
    }
}
