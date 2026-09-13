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
    public void Writer_rejects_path_traversal_identifiers()
    {
        using var fixture = new AudioFixture();
        var format = new PcmWaveFormat(16000, 1, 16);

        Assert.Throws<ArgumentException>(() => PcmWaveWriter.Create(fixture.AudioRoot, "session..\\escape", DateTimeOffset.UtcNow, format, "source-audio"));
        Assert.Throws<ArgumentException>(() => PcmWaveWriter.Create(fixture.AudioRoot, "session-safe", DateTimeOffset.UtcNow, format, ".."));
        Assert.Empty(Directory.EnumerateFiles(fixture.AudioRoot, "*.capture.tmp", SearchOption.AllDirectories));
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

    [Fact]
    public void Capture_error_releases_input_and_leaves_recoverable_audio()
    {
        using var fixture = new AudioFixture();
        using var archive = new SqliteArchive(fixture.DatabasePath);
        archive.Initialize();
        var repository = new ArchiveRepository(archive);
        var session = repository.AddSession(DateTimeOffset.UtcNow, PrivacyMode.LocalCaptureOnly);
        var fake = new FakeAudioInput(new PcmWaveFormat(48000, 1, 16));
        var controller = new AudioCaptureController(repository, fixture.AudioRoot);
        controller.Start(session.SessionId, null, true, _ => fake);
        Exception? raised = null;
        controller.CaptureFailed += (_, error) => raised = error;

        fake.RaiseError(new IOException("microphone disconnected"));

        Assert.Equal(AudioCaptureState.Failed, controller.State);
        Assert.Contains("microphone disconnected", controller.Failure);
        Assert.NotNull(raised);
        var recovered = Assert.Single(AudioRecoveryScanner.Scan(fixture.AudioRoot));
        Assert.True(recovered.IsValidPcm);
        Assert.True(fake.Disposed);
    }

    [Fact]
    public void Stop_error_preserves_partial_capture_for_recovery()
    {
        using var fixture = new AudioFixture();
        using var archive = new SqliteArchive(fixture.DatabasePath);
        archive.Initialize();
        var repository = new ArchiveRepository(archive);
        var session = repository.AddSession(DateTimeOffset.UtcNow, PrivacyMode.LocalCaptureOnly);
        var fake = new FakeAudioInput(new PcmWaveFormat(48000, 1, 16)) { ThrowOnStop = true };
        var controller = new AudioCaptureController(repository, fixture.AudioRoot);
        controller.Start(session.SessionId, null, true, _ => fake);

        Assert.Throws<InvalidOperationException>(() => controller.Stop());

        Assert.Equal(AudioCaptureState.Failed, controller.State);
        Assert.Single(AudioRecoveryScanner.Scan(fixture.AudioRoot));
    }

    [Fact]
    public void Capture_error_during_start_does_not_reenter_capturing()
    {
        using var fixture = new AudioFixture();
        using var archive = new SqliteArchive(fixture.DatabasePath);
        archive.Initialize();
        var repository = new ArchiveRepository(archive);
        var session = repository.AddSession(DateTimeOffset.UtcNow, PrivacyMode.LocalCaptureOnly);
        var fake = new FakeAudioInput(new PcmWaveFormat(48000, 1, 16)) { RaiseErrorOnStart = true };
        var controller = new AudioCaptureController(repository, fixture.AudioRoot);
        Exception? raised = null;
        controller.CaptureFailed += (_, error) => raised = error;

        Assert.Throws<InvalidOperationException>(() => controller.Start(session.SessionId, null, true, _ => fake));

        Assert.Equal(AudioCaptureState.Failed, controller.State);
        Assert.NotNull(raised);
        Assert.True(fake.Disposed);
        Assert.Single(AudioRecoveryScanner.Scan(fixture.AudioRoot));
    }

    [Fact]
    public void Input_factory_failure_marks_capture_failed()
    {
        using var fixture = new AudioFixture();
        using var archive = new SqliteArchive(fixture.DatabasePath);
        archive.Initialize();
        var repository = new ArchiveRepository(archive);
        var session = repository.AddSession(DateTimeOffset.UtcNow, PrivacyMode.LocalCaptureOnly);
        var controller = new AudioCaptureController(repository, fixture.AudioRoot);
        Exception? raised = null;
        controller.CaptureFailed += (_, error) => raised = error;

        Assert.Throws<IOException>(() => controller.Start(session.SessionId, null, true, _ => throw new IOException("no microphone")));

        Assert.Equal(AudioCaptureState.Failed, controller.State);
        Assert.Contains("no microphone", controller.Failure);
        Assert.NotNull(raised);
        Assert.Empty(AudioRecoveryScanner.Scan(fixture.AudioRoot));
    }

    [Fact]
    public void Source_registration_failure_marks_capture_failed_and_preserves_finalized_audio_for_recovery()
    {
        using var fixture = new AudioFixture();
        var archive = new SqliteArchive(fixture.DatabasePath);
        archive.Initialize();
        var repository = new ArchiveRepository(archive);
        var session = repository.AddSession(DateTimeOffset.UtcNow, PrivacyMode.LocalCaptureOnly);
        var fake = new FakeAudioInput(new PcmWaveFormat(48000, 1, 16));
        var controller = new AudioCaptureController(repository, fixture.AudioRoot);
        controller.Start(session.SessionId, null, true, _ => fake);
        Exception? raised = null;
        controller.CaptureFailed += (_, error) => raised = error;
        archive.Dispose();

        Assert.Throws<ObjectDisposedException>(() => controller.Stop());

        Assert.Equal(AudioCaptureState.Failed, controller.State);
        Assert.NotNull(raised);
        var recovered = Assert.Single(AudioRecoveryScanner.Scan(fixture.AudioRoot));
        Assert.True(recovered.IsValidPcm);
        Assert.True(recovered.ByteLength >= 44);
    }

    [Fact]
    public void Input_dispose_failure_preserves_capture_for_recovery()
    {
        using var fixture = new AudioFixture();
        using var archive = new SqliteArchive(fixture.DatabasePath);
        archive.Initialize();
        var repository = new ArchiveRepository(archive);
        var session = repository.AddSession(DateTimeOffset.UtcNow, PrivacyMode.LocalCaptureOnly);
        var fake = new FakeAudioInput(new PcmWaveFormat(48000, 1, 16)) { ThrowOnDispose = true };
        var controller = new AudioCaptureController(repository, fixture.AudioRoot);
        controller.Start(session.SessionId, null, true, _ => fake);

        Assert.Throws<InvalidOperationException>(() => controller.Stop());

        Assert.Equal(AudioCaptureState.Failed, controller.State);
        Assert.Single(AudioRecoveryScanner.Scan(fixture.AudioRoot));
    }

    [Fact]
    public void Abort_for_recovery_swallows_cleanup_failure_and_keeps_partial_audio()
    {
        using var fixture = new AudioFixture();
        using var archive = new SqliteArchive(fixture.DatabasePath);
        archive.Initialize();
        var repository = new ArchiveRepository(archive);
        var session = repository.AddSession(DateTimeOffset.UtcNow, PrivacyMode.LocalCaptureOnly);
        var fake = new FakeAudioInput(new PcmWaveFormat(48000, 1, 16)) { ThrowOnDispose = true };
        var controller = new AudioCaptureController(repository, fixture.AudioRoot);
        controller.Start(session.SessionId, null, true, _ => fake);

        var exception = Record.Exception(controller.AbortForRecovery);

        Assert.Null(exception);
        Assert.Equal(AudioCaptureState.Recoverable, controller.State);
        Assert.Contains("input dispose failed", controller.Failure);
        Assert.Single(AudioRecoveryScanner.Scan(fixture.AudioRoot));
    }

    [Fact]
    public void Duplicate_final_path_keeps_the_second_capture_recoverable()
    {
        using var fixture = new AudioFixture();
        var format = new PcmWaveFormat(16000, 1, 16);
        using (var first = PcmWaveWriter.Create(fixture.AudioRoot, "session-duplicate", DateTimeOffset.UtcNow, format, "same-source"))
        {
            first.Append(new byte[format.BlockAlign * 160]);
            first.FinalizeAsset();
        }

        using var second = PcmWaveWriter.Create(fixture.AudioRoot, "session-duplicate", DateTimeOffset.UtcNow, format, "same-source");
        second.Append(new byte[format.BlockAlign * 80]);

        Assert.Throws<IOException>(() => second.FinalizeAsset());
        Assert.True(File.Exists(second.TemporaryPath));
        Assert.True(PcmWaveValidator.Validate(second.TemporaryPath, allowPartial: false).Equals(format));
    }

    private sealed class FakeAudioInput(PcmWaveFormat format) : IAudioInput
    {
        public PcmWaveFormat Format { get; } = format;
        public event EventHandler<AudioDataEventArgs>? DataAvailable;
#pragma warning disable CS0067
        public event EventHandler<Exception>? CaptureError;
#pragma warning restore CS0067

        public void Start() => StartWithErrorIfRequested();
        public void Stop() { if (ThrowOnStop) throw new InvalidOperationException("stop failed"); }
        public bool Disposed { get; private set; }
        public bool ThrowOnStop { get; init; }
        public bool ThrowOnDispose { get; init; }
        public bool RaiseErrorOnStart { get; init; }
        public void RaiseError(Exception error) => CaptureError?.Invoke(this, error);
        public void StartWithErrorIfRequested()
        {
            if (RaiseErrorOnStart)
            {
                RaiseError(new IOException("microphone failed during start"));
                return;
            }

            DataAvailable?.Invoke(this, new AudioDataEventArgs(new byte[Format.BlockAlign * 480], Format.BlockAlign * 480));
        }
        public void Dispose()
        {
            Disposed = true;
            if (ThrowOnDispose) throw new InvalidOperationException("input dispose failed");
        }
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
