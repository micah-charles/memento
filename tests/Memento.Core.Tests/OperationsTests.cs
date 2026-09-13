using System.Security.Cryptography;
using System.IO.Compression;
using Memento.Core.Audio;
using Memento.Core.Admin;
using Memento.Core.Conversation;
using Memento.Core.Domain;
using Memento.Core.External;
using Memento.Core.Memory;
using Memento.Core.Storage;

namespace Memento.Core.Tests;

public sealed class OperationsTests
{
    [Fact]
    public async Task Current_information_is_explicitly_untrusted_external_data()
    {
        var result = await new CurrentInformationService(new DeterministicSearchProvider()).SearchAsync("Hong Kong weather");

        Assert.True(result.IsUntrustedExternalInformation);
        Assert.Equal("deterministic-test", result.Provider);
        Assert.StartsWith("https://", result.Sources[0].Url, StringComparison.Ordinal);
    }

    [Fact]
    public void Family_admin_review_requires_authorization_and_keeps_annotation_attributed()
    {
        using var fixture = new OperationsFixture();
        using var archive = fixture.CreateArchive();
        var repository = new ArchiveRepository(archive);
        var session = repository.AddSession(DateTimeOffset.UtcNow, PrivacyMode.Normal);
        var source = repository.AddSource(fixture.Source(session.SessionId, "source-admin"));
        var revision = repository.AddTranscriptRevision(new TranscriptRevision("revision-admin", source.SourceId, null, 1, "initial", "我鍾意魚蛋", 0.8, null, DateTimeOffset.UtcNow));
        var extraction = new MemoryExtractionService(repository, new DeterministicMemoryExtractionProvider()).ExtractAndPersist(session, source, revision);
        var service = new FamilyAdminReviewService(repository, new FixedTestAdminAuthorizer("admin-1"));

        Assert.Throws<UnauthorizedAccessException>(() => service.ListCandidates("wrong"));
        var claim = Assert.Single(service.ListCandidates("admin-1"));
        var annotation = service.AnnotateClaim("admin-1", claim, "family_assessment", "Family review supports the candidate.", "supported");

        Assert.Equal("admin-1", annotation.ActorId);
        Assert.Empty(service.ListCandidates("admin-1"));
        Assert.Equal(ClaimStatus.Candidate, claim.Status);
        Assert.NotNull(extraction);
    }

    [Fact]
    public void Export_writes_jsonl_media_and_encrypted_backup_roundtrip()
    {
        using var fixture = new OperationsFixture();
        using var archive = fixture.CreateArchive();
        var repository = new ArchiveRepository(archive);
        var session = repository.AddSession(DateTimeOffset.UtcNow, PrivacyMode.Normal);
        var audio = Path.Combine(fixture.DirectoryPath, "recording.wav");
        File.WriteAllBytes(audio, [7, 8, 9]);
        repository.AddSource(new SourceMetadata("source-export", "audio", session.SessionId, null, audio, "PCM WAV", 48000, 1, 16, 3, 0, "abc", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "finalized", DateTimeOffset.UtcNow));
        var secondAudioDirectory = Path.Combine(fixture.DirectoryPath, "second");
        Directory.CreateDirectory(secondAudioDirectory);
        var secondAudio = Path.Combine(secondAudioDirectory, "recording.wav");
        File.WriteAllBytes(secondAudio, [4, 5, 6]);
        repository.AddSource(new SourceMetadata("source-export-second", "audio", session.SessionId, null, secondAudio, "PCM WAV", 48000, 1, 16, 3, 0, "def", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "finalized", DateTimeOffset.UtcNow));
        var result = ArchiveExporter.Export(archive, fixture.ExportRoot, includeMedia: true);
        var secondResult = ArchiveExporter.Export(archive, fixture.ExportRoot, includeMedia: true);

        Assert.True(File.Exists(result.ManifestPath));
        Assert.True(File.Exists(Path.Combine(result.ExportDirectory, "sources.jsonl")));
        Assert.True(File.Exists(Path.Combine(result.ExportDirectory, "deletion_tombstones.jsonl")));
        Assert.True(File.Exists(Path.Combine(result.ExportDirectory, "media", "recording.wav")));
        Assert.True(File.Exists(Path.Combine(result.ExportDirectory, "media", "source-source-export-second-recording.wav")));
        Assert.NotEqual(result.ExportDirectory, secondResult.ExportDirectory);
        Assert.True(File.Exists(Path.Combine(result.ExportDirectory, "archive.sqlite")));
        var encrypted = Path.Combine(fixture.ExportRoot, "backup.memento");
        var restored = Path.Combine(fixture.ExportRoot, "restored.sqlite");
        ArchiveBackupProtector.EncryptFile(Path.Combine(result.ExportDirectory, "archive.sqlite"), encrypted, "test-password");
        ArchiveBackupProtector.DecryptFile(encrypted, restored, "test-password");
        Assert.Equal(SHA256.HashData(File.ReadAllBytes(Path.Combine(result.ExportDirectory, "archive.sqlite"))), SHA256.HashData(File.ReadAllBytes(restored)));

        var encryptedBundle = Path.Combine(fixture.ExportRoot, "backup-bundle.memento");
        var restoredBundle = Path.Combine(fixture.ExportRoot, "restored-bundle");
        ArchiveBackupProtector.EncryptDirectory(result.ExportDirectory, encryptedBundle, "test-password");
        var restoreReport = ArchiveBackupProtector.DecryptDirectory(encryptedBundle, restoredBundle, "test-password");
        Assert.True(restoreReport.IntegrityOk);
        Assert.Empty(restoreReport.Findings);
        Assert.True(File.Exists(Path.Combine(restoredBundle, "archive.sqlite")));
        Assert.True(File.Exists(Path.Combine(restoredBundle, "media", "recording.wav")));

        var maliciousZip = Path.Combine(fixture.ExportRoot, "malicious.zip");
        using (var zip = ZipFile.Open(maliciousZip, ZipArchiveMode.Create))
        using (var writer = new StreamWriter(zip.CreateEntry("../escape.txt").Open()))
            writer.Write("unsafe");
        var maliciousBackup = Path.Combine(fixture.ExportRoot, "malicious.memento");
        ArchiveBackupProtector.EncryptFile(maliciousZip, maliciousBackup, "test-password");
        Assert.Throws<InvalidDataException>(() => ArchiveBackupProtector.DecryptDirectory(maliciousBackup, Path.Combine(fixture.ExportRoot, "malicious-restore"), "test-password"));
    }

    [Fact]
    public void Health_check_reports_recoverable_audio_and_due_jobs()
    {
        using var fixture = new OperationsFixture();
        using var archive = fixture.CreateArchive();
        var repository = new ArchiveRepository(archive);
        var session = repository.AddSession(DateTimeOffset.UtcNow, PrivacyMode.Normal);
        var source = repository.AddSource(fixture.Source(session.SessionId, "source-health"));
        repository.AddConversationJob(new ConversationJob("job-health", session.SessionId, null, source.SourceId, "durable_transcription", ConversationJobStatus.Pending, 0, DateTimeOffset.UtcNow.AddMinutes(-1), null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
        using (var writer = PcmWaveWriter.Create(fixture.AudioRoot, session.SessionId, DateTimeOffset.UtcNow, new PcmWaveFormat(16000, 1, 16), "health-audio"))
        {
            writer.Append(new byte[320]);
        }
        var derivedPath = Path.Combine(fixture.DirectoryPath, "derived.wav");
        var derivedBytes = new byte[] { 1, 2, 3 };
        File.WriteAllBytes(derivedPath, derivedBytes);
        repository.AddDerivedSpeechAsset(new DerivedSpeechAsset("derived-health", session.SessionId, null, derivedPath, "wav", derivedBytes.LongLength, Convert.ToHexString(SHA256.HashData(derivedBytes)).ToLowerInvariant(), "deterministic-test", "fake-tts-v1", "test", "request", DateTimeOffset.UtcNow));

        var report = ArchiveHealthCheck.Run(archive, fixture.AudioRoot);

        Assert.True(report.IntegrityOk);
        Assert.Equal(15, report.SchemaVersion);
        Assert.Equal(1, report.RecoverableAudioCount);
        Assert.Equal(1, report.PendingConversationJobs);
        Assert.Equal(0, report.InvalidDerivedSpeechAssetCount);
        Assert.Equal(1, report.InvalidSourceAssetCount);
        Assert.NotEmpty(report.Findings);

        File.AppendAllBytes(derivedPath, [99]);
        var tampered = ArchiveHealthCheck.Run(archive, fixture.AudioRoot);
        Assert.Equal(1, tampered.InvalidDerivedSpeechAssetCount);
    }

    private sealed class OperationsFixture : IDisposable
    {
        public OperationsFixture()
        {
            DirectoryPath = Path.Combine(Path.GetTempPath(), "memento-operations-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(DirectoryPath);
            AudioRoot = Path.Combine(DirectoryPath, "raw", "audio");
            ExportRoot = Path.Combine(DirectoryPath, "exports");
        }

        public string DirectoryPath { get; }
        public string AudioRoot { get; }
        public string ExportRoot { get; }
        public SqliteArchive CreateArchive() { var archive = new SqliteArchive(Path.Combine(DirectoryPath, "data", "memory.db")); archive.Initialize(); return archive; }
        public SourceMetadata Source(string sessionId, string id) => new(id, "audio", sessionId, null, "raw/audio.wav", "PCM WAV", 48000, 1, 16, 100, 1, "abc", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "finalized", DateTimeOffset.UtcNow);
        public void Dispose() { if (Directory.Exists(DirectoryPath)) Directory.Delete(DirectoryPath, true); }
    }
}
