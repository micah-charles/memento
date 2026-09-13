using System.Security.Cryptography;
using Memento.Core.Audio;
using Memento.Core.Domain;
using Memento.Core.Storage;

namespace Memento.Core.Tests;

public sealed class AudioTests
{
    [Fact]
    public void Finalization_writes_valid_pcm_wav_and_checksum()
    {
        using var fixture = new AudioFixture();
        using var archive = new SqliteArchive(fixture.DatabasePath);
        archive.Initialize();
        var session = new ArchiveRepository(archive).AddSession(DateTimeOffset.UtcNow, PrivacyMode.Normal);
        var format = new PcmWaveFormat(48000, 1, 16);
        using var writer = PcmWaveWriter.Create(fixture.AudioRoot, session.SessionId, DateTimeOffset.Parse("2026-09-13T10:00:00Z"), format, "source-audio");
        var pcm = new byte[format.BlockAlign * 480];
        RandomNumberGenerator.Fill(pcm);
        writer.Append(pcm);
        var asset = writer.FinalizeAsset();

        Assert.True(File.Exists(asset.FilePath));
        Assert.False(File.Exists(writer.TemporaryPath));
        Assert.Equal(44 + pcm.Length, asset.ByteLength);
        Assert.Equal(10, asset.DurationMs);
        Assert.Equal(asset.Sha256, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(asset.FilePath))).ToLowerInvariant());
        Assert.Equal(format, PcmWaveValidator.Validate(asset.FilePath, allowPartial: false));
    }

    [Fact]
    public void Controller_requires_explicit_consent_and_registers_source()
    {
        using var fixture = new AudioFixture();
        using var archive = new SqliteArchive(fixture.DatabasePath);
        archive.Initialize();
        var repository = new ArchiveRepository(archive);
        var session = repository.AddSession(DateTimeOffset.UtcNow, PrivacyMode.Normal);
        var turn = repository.AddTurn(session.SessionId, 0, "participant", DateTimeOffset.UtcNow);
        var controller = new AudioCaptureController(repository, fixture.AudioRoot);
        var fake = new FakeAudioInput(new PcmWaveFormat(48000, 1, 16));

        Assert.Throws<ConsentRequiredException>(() => controller.Start(session.SessionId, null, false, _ => fake));

        controller.Start(session.SessionId, turn.TurnId, true, _ => fake);
        var source = controller.Stop();

        Assert.Equal(AudioCaptureState.Finalized, controller.State);
        Assert.Equal(session.SessionId, source.SessionId);
        Assert.Equal("finalized", source.RecoveryStatus);
        using var connection = archive.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sources WHERE source_id = $id";
        command.Parameters.AddWithValue("$id", source.SourceId);
        Assert.Equal(1L, Convert.ToInt64(command.ExecuteScalar()));
    }

    [Fact]
    public void Abort_leaves_recoverable_partial_and_scanner_does_not_delete_it()
    {
        using var fixture = new AudioFixture();
        var format = new PcmWaveFormat(16000, 1, 16);
        var writer = PcmWaveWriter.Create(fixture.AudioRoot, "session-recovery", DateTimeOffset.UtcNow, format, "source-recovery");
        writer.Append(new byte[format.BlockAlign * 160]);
        var temporary = writer.TemporaryPath;
        writer.Dispose();

        var recovered = Assert.Single(AudioRecoveryScanner.Scan(fixture.AudioRoot));

        Assert.Equal(temporary, recovered.TemporaryPath);
        Assert.True(recovered.IsValidPcm);
        Assert.Equal(10, recovered.DurationMs);
        Assert.True(File.Exists(temporary));
    }

    [Fact]
    public void Corrupt_partial_is_reported_without_being_deleted()
    {
        using var fixture = new AudioFixture();
        var directory = Path.Combine(fixture.AudioRoot, "2026", "09", "13");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "corrupt.capture.tmp");
        File.WriteAllText(path, "not audio");

        var recovered = Assert.Single(AudioRecoveryScanner.Scan(fixture.AudioRoot));

        Assert.False(recovered.IsValidPcm);
        Assert.NotNull(recovered.Error);
        Assert.True(File.Exists(path));
    }

    private sealed class FakeAudioInput(PcmWaveFormat format) : IAudioInput
    {
        public PcmWaveFormat Format { get; } = format;
        public event EventHandler<AudioDataEventArgs>? DataAvailable;
#pragma warning disable CS0067
        public event EventHandler<Exception>? CaptureError;
#pragma warning restore CS0067

        public void Start() => DataAvailable?.Invoke(this, new AudioDataEventArgs(new byte[Format.BlockAlign * 480], Format.BlockAlign * 480));
        public void Stop() { }
        public void Dispose() { }
    }

    private sealed class AudioFixture : IDisposable
    {
        public AudioFixture()
        {
            DirectoryPath = Path.Combine(Path.GetTempPath(), "memento-audio-tests", Guid.NewGuid().ToString("N"));
            AudioRoot = Path.Combine(DirectoryPath, "raw", "audio");
            DatabasePath = Path.Combine(DirectoryPath, "data", "memory.db");
            Directory.CreateDirectory(AudioRoot);
        }

        public string DirectoryPath { get; }
        public string AudioRoot { get; }
        public string DatabasePath { get; }

        public void Dispose()
        {
            if (Directory.Exists(DirectoryPath))
                Directory.Delete(DirectoryPath, recursive: true);
        }
    }
}
