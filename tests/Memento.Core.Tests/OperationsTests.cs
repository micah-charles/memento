using System.Security.Cryptography;
using System.IO.Compression;
using Microsoft.Data.Sqlite;
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
        var result = await new CurrentInformationService(new DeterministicSearchProvider()).SearchAsync("Hong Kong weather", PrivacyMode.Normal, true);

        Assert.True(result.IsUntrustedExternalInformation);
        Assert.Equal("deterministic-test", result.Provider);
        Assert.StartsWith("https://", result.Sources[0].Url, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Current_information_service_normalizes_provider_identity_and_untrusted_boundary()
    {
        var result = await new CurrentInformationService(new TrustingSearchProvider()).SearchAsync(
            "Hong Kong weather", PrivacyMode.Normal, true);

        Assert.Equal("Hong Kong weather", result.Query);
        Assert.Equal("trusting-test", result.Provider);
        Assert.True(result.IsUntrustedExternalInformation);
    }

    [Theory]
    [InlineData(PrivacyMode.PrivateConversation)]
    [InlineData(PrivacyMode.LocalCaptureOnly)]
    public async Task Current_information_rejects_cloud_blocked_privacy_modes(PrivacyMode privacyMode)
    {
        var provider = new CountingSearchProvider();
        var service = new CurrentInformationService(provider);

        await Assert.ThrowsAsync<CloudNotPermittedException>(() => service.SearchAsync("Hong Kong weather", privacyMode, true));

        Assert.Equal(0, provider.CallCount);
    }

    [Fact]
    public async Task Current_information_requires_explicit_cloud_consent()
    {
        var provider = new CountingSearchProvider();
        var service = new CurrentInformationService(provider);

        await Assert.ThrowsAsync<CloudNotPermittedException>(() => service.SearchAsync("Hong Kong weather", PrivacyMode.Normal, false));

        Assert.Equal(0, provider.CallCount);
    }

    [Fact]
    public async Task Current_information_rejects_blank_query_before_provider_call()
    {
        var provider = new CountingSearchProvider();
        var service = new CurrentInformationService(provider);

        await Assert.ThrowsAsync<ArgumentException>(() => service.SearchAsync("  ", PrivacyMode.Normal, true));

        Assert.Equal(0, provider.CallCount);
    }

    [Theory]
    [InlineData(PrivacyMode.Normal, true, true)]
    [InlineData(PrivacyMode.Normal, false, false)]
    [InlineData(PrivacyMode.PrivateConversation, true, false)]
    [InlineData(PrivacyMode.LocalCaptureOnly, true, false)]
    public void Consent_policy_requires_the_checkbox_and_allows_only_normal_cloud_sessions(PrivacyMode privacyMode, bool requested, bool expected)
    {
        Assert.Equal(expected, ConsentPolicy.CloudConsentGranted(privacyMode, requested));
    }

    [Fact]
    public void Family_admin_review_requires_authorization_and_keeps_annotation_attributed()
    {
        using var fixture = new OperationsFixture();
        using var archive = fixture.CreateArchive();
        var repository = new ArchiveRepository(archive);
        var session = repository.AddSession(DateTimeOffset.UtcNow, PrivacyMode.Normal);
        repository.AddConsent(session.SessionId, ConsentScope.CloudTranscription, PrivacyMode.Normal, true, "privacy-1");
        var source = repository.AddSource(fixture.Source(session.SessionId, "source-admin"));
        var revision = repository.AddTranscriptRevision(new TranscriptRevision("revision-admin", source.SourceId, null, 1, "initial", "我鍾意魚蛋", 0.8, null, DateTimeOffset.UtcNow));
        var extraction = new MemoryExtractionService(repository, new DeterministicMemoryExtractionProvider()).ExtractAndPersist(session, source, revision);
        var service = new FamilyAdminReviewService(repository, new FixedTestAdminAuthorizer("admin-1"));

        Assert.Throws<UnauthorizedAccessException>(() => service.ListCandidates("wrong"));
        var claim = Assert.Single(service.ListCandidates("admin-1"));
        var annotation = service.AnnotateClaim("admin-1", claim, "family_assessment", "Family review supports the candidate.", "supported");

        Assert.Equal("admin-1", annotation.ActorId);
        var claimEvidence = repository.ListEvidenceForClaim(claim.MemoryClaimId);
        var supportingEvidence = Assert.Single(claimEvidence);
        Assert.Equal("supports", supportingEvidence.Relationship);
        Assert.Equal(extraction.Evidence[0].EvidenceId, supportingEvidence.Evidence.EvidenceId);
        Assert.Empty(service.ListCandidates("admin-1"));
        var reviewedClaims = service.ListReviewedClaims("admin-1");
        Assert.Single(reviewedClaims);
        Assert.Equal(claim.MemoryClaimId, reviewedClaims[0].MemoryClaimId);
        Assert.Equal(ClaimStatus.Candidate, claim.Status);
        Assert.NotNull(extraction);
    }

    [Fact]
    public void Family_admin_review_rejects_a_claim_that_is_not_a_current_candidate()
    {
        using var fixture = new OperationsFixture();
        using var archive = fixture.CreateArchive();
        var repository = new ArchiveRepository(archive);
        var session = repository.AddSession(DateTimeOffset.UtcNow, PrivacyMode.Normal);
        repository.AddConsent(session.SessionId, ConsentScope.CloudTranscription, PrivacyMode.Normal, true, "privacy-1");
        var source = repository.AddSource(fixture.Source(session.SessionId, "source-admin-stale"));
        var revision = repository.AddTranscriptRevision(new TranscriptRevision("revision-admin-stale", source.SourceId, null, 1, "initial", "我鍾意魚蛋", 0.8, null, DateTimeOffset.UtcNow));
        var extraction = new MemoryExtractionService(repository, new DeterministicMemoryExtractionProvider()).ExtractAndPersist(session, source, revision);
        var claim = Assert.Single(extraction.Claims);
        repository.UpdateMemoryClaimStatus(claim.MemoryClaimId, ClaimStatus.Reviewed);
        var service = new FamilyAdminReviewService(repository, new FixedTestAdminAuthorizer("admin-1"));

        Assert.Throws<InvalidOperationException>(() => service.AnnotateClaim("admin-1", claim, "family_assessment", "stale review", "supported"));
        using var connection = archive.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM review_annotations WHERE target_id = $claim";
        command.Parameters.AddWithValue("$claim", claim.MemoryClaimId);
        Assert.Equal(0L, Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public void Family_admin_review_cannot_create_speaker_confirmation_or_withdrawal_annotations()
    {
        using var fixture = new OperationsFixture();
        using var archive = fixture.CreateArchive();
        var repository = new ArchiveRepository(archive);
        var session = repository.AddSession(DateTimeOffset.UtcNow, PrivacyMode.Normal);
        repository.AddConsent(session.SessionId, ConsentScope.CloudTranscription, PrivacyMode.Normal, true, "privacy-1");
        var source = repository.AddSource(fixture.Source(session.SessionId, "source-admin-authority"));
        var revision = repository.AddTranscriptRevision(new TranscriptRevision("revision-admin-authority", source.SourceId, null, 1, "initial", "我鍾意魚蛋", 0.8, null, DateTimeOffset.UtcNow));
        new MemoryExtractionService(repository, new DeterministicMemoryExtractionProvider()).ExtractAndPersist(session, source, revision);
        var claim = Assert.Single(repository.ListCandidateClaims());
        var service = new FamilyAdminReviewService(repository, new FixedTestAdminAuthorizer("admin-1"));

        Assert.Throws<ArgumentException>(() => service.AnnotateClaim("admin-1", claim, "speaker_confirmation", "pretend speaker confirmation", "confirmed"));
        Assert.Throws<ArgumentException>(() => service.AnnotateClaim("admin-1", claim, "withdrawal", "pretend withdrawal", null));
        using var connection = archive.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM review_annotations";
        Assert.Equal(0L, Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public void Family_admin_operation_audit_requires_authorization_and_stores_no_content()
    {
        using var fixture = new OperationsFixture();
        using var archive = fixture.CreateArchive();
        var repository = new ArchiveRepository(archive);
        var service = new FamilyAdminReviewService(repository, new FixedTestAdminAuthorizer("admin-1"));

        Assert.Throws<UnauthorizedAccessException>(() => service.RecordAdminOperation("wrong", "export"));
        var audit = service.RecordAdminOperation("admin-1", "export");

        Assert.Equal("archive", audit.TargetType);
        Assert.Equal("archive", audit.TargetId);
        Assert.Equal("admin-1", audit.ActorId);
        Assert.DoesNotContain("/", audit.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("\\", audit.Body, StringComparison.Ordinal);
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
        var exportedSourceRows = File.ReadLines(Path.Combine(result.ExportDirectory, "sources.jsonl")).Count(line => !string.IsNullOrWhiteSpace(line));
        using (var snapshot = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(result.ExportDirectory, "archive.sqlite"), Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString()))
        {
            snapshot.Open();
            using var sourceCount = snapshot.CreateCommand();
            sourceCount.CommandText = "SELECT COUNT(*) FROM sources";
            Assert.Equal(Convert.ToInt32(sourceCount.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture), exportedSourceRows);
        }
        var encrypted = Path.Combine(fixture.ExportRoot, "backup.memento");
        var restored = Path.Combine(fixture.ExportRoot, "restored.sqlite");
        ArchiveBackupProtector.EncryptFile(Path.Combine(result.ExportDirectory, "archive.sqlite"), encrypted, "test-password");
        ArchiveBackupProtector.DecryptFile(encrypted, restored, "test-password");
        Assert.Equal(SHA256.HashData(File.ReadAllBytes(Path.Combine(result.ExportDirectory, "archive.sqlite"))), SHA256.HashData(File.ReadAllBytes(restored)));
        var rotated = Path.Combine(fixture.ExportRoot, "backup-rotated.memento");
        ArchiveBackupProtector.ReencryptFile(encrypted, rotated, "test-password", "new-test-password");
        var rotatedRestore = Path.Combine(fixture.ExportRoot, "rotated.sqlite");
        ArchiveBackupProtector.DecryptFile(rotated, rotatedRestore, "new-test-password");
        Assert.Equal(SHA256.HashData(File.ReadAllBytes(Path.Combine(result.ExportDirectory, "archive.sqlite"))), SHA256.HashData(File.ReadAllBytes(rotatedRestore)));
        Assert.ThrowsAny<Exception>(() => ArchiveBackupProtector.DecryptFile(rotated, Path.Combine(fixture.ExportRoot, "wrong-password.sqlite"), "test-password"));

        var encryptedBundle = Path.Combine(fixture.ExportRoot, "backup-bundle.memento");
        var restoredBundle = Path.Combine(fixture.ExportRoot, "restored-bundle");
        ArchiveBackupProtector.EncryptDirectory(result.ExportDirectory, encryptedBundle, "test-password");
        var restoreReport = ArchiveBackupProtector.DecryptDirectory(encryptedBundle, restoredBundle, "test-password");
        Assert.True(restoreReport.IntegrityOk);
        Assert.Empty(restoreReport.Findings);
        Assert.True(File.Exists(Path.Combine(restoredBundle, "archive.sqlite")));
        Assert.True(File.Exists(Path.Combine(restoredBundle, "media", "recording.wav")));
        var rotatedBundle = Path.Combine(fixture.ExportRoot, "backup-bundle-rotated.memento");
        var rotatedBundleReport = ArchiveBackupProtector.ReencryptDirectory(encryptedBundle, rotatedBundle, "test-password", "new-bundle-password");
        Assert.True(rotatedBundleReport.IntegrityOk);
        var rotatedBundleRestore = Path.Combine(fixture.ExportRoot, "restored-bundle-rotated");
        var rotatedBundleRestoreReport = ArchiveBackupProtector.DecryptDirectory(rotatedBundle, rotatedBundleRestore, "new-bundle-password");
        Assert.True(rotatedBundleRestoreReport.IntegrityOk);
        Assert.True(File.Exists(Path.Combine(rotatedBundleRestore, "media", "recording.wav")));

        var maliciousZip = Path.Combine(fixture.ExportRoot, "malicious.zip");
        using (var zip = ZipFile.Open(maliciousZip, ZipArchiveMode.Create))
        using (var writer = new StreamWriter(zip.CreateEntry("../escape.txt").Open()))
            writer.Write("unsafe");
        var maliciousBackup = Path.Combine(fixture.ExportRoot, "malicious.memento");
        ArchiveBackupProtector.EncryptFile(maliciousZip, maliciousBackup, "test-password");
        var maliciousRestore = Path.Combine(fixture.ExportRoot, "malicious-restore");
        Assert.Throws<InvalidDataException>(() => ArchiveBackupProtector.DecryptDirectory(maliciousBackup, maliciousRestore, "test-password"));
        Assert.False(Directory.Exists(maliciousRestore));

        var duplicateZip = Path.Combine(fixture.ExportRoot, "duplicate.zip");
        using (var zip = ZipFile.Open(duplicateZip, ZipArchiveMode.Create))
        {
            using (var first = new StreamWriter(zip.CreateEntry("archive.sqlite").Open())) first.Write("first");
            using (var second = new StreamWriter(zip.CreateEntry("archive.sqlite").Open())) second.Write("second");
        }
        var duplicateBackup = Path.Combine(fixture.ExportRoot, "duplicate.memento");
        ArchiveBackupProtector.EncryptFile(duplicateZip, duplicateBackup, "test-password");
        var duplicateRestore = Path.Combine(fixture.ExportRoot, "duplicate-restore");
        Assert.Throws<InvalidDataException>(() => ArchiveBackupProtector.DecryptDirectory(duplicateBackup, duplicateRestore, "test-password"));
        Assert.False(Directory.Exists(duplicateRestore));

        var aliasZip = Path.Combine(fixture.ExportRoot, "alias.zip");
        using (var zip = ZipFile.Open(aliasZip, ZipArchiveMode.Create))
        {
            using (var first = new StreamWriter(zip.CreateEntry("archive.sqlite").Open())) first.Write("first");
            using (var alias = new StreamWriter(zip.CreateEntry("nested/../archive.sqlite").Open())) alias.Write("second");
        }
        var aliasBackup = Path.Combine(fixture.ExportRoot, "alias.memento");
        ArchiveBackupProtector.EncryptFile(aliasZip, aliasBackup, "test-password");
        var aliasRestore = Path.Combine(fixture.ExportRoot, "alias-restore");
        Assert.Throws<InvalidDataException>(() => ArchiveBackupProtector.DecryptDirectory(aliasBackup, aliasRestore, "test-password"));
        Assert.False(Directory.Exists(aliasRestore));
    }

    [Fact]
    public void Scoped_redacted_export_withholds_source_audio_and_evidence_content()
    {
        using var fixture = new OperationsFixture();
        using var archive = fixture.CreateArchive();
        var repository = new ArchiveRepository(archive);
        var session = repository.AddSession(DateTimeOffset.UtcNow, PrivacyMode.Normal);
        var source = repository.AddSource(fixture.Source(session.SessionId, "source-scoped"));
        var revision = repository.AddTranscriptRevision(new TranscriptRevision("revision-scoped", source.SourceId, null, 1, "initial", "private transcript phrase", 0.9, null, DateTimeOffset.UtcNow));
        var evidence = repository.AddEvidence(new EvidenceRecord("evidence-scoped", EvidenceKind.DirectStatement, source.SourceId, session.SessionId, null, revision.TranscriptRevisionId, "private evidence statement", "private original expression", ParticipantCertainty.Stated, true, DateTimeOffset.UtcNow, 10, 20, "provider", "model"));
        var claim = repository.AddMemoryClaim(new MemoryClaim("claim-scoped", "Participant likes fish balls", null, "likes", "fish balls", ClaimStatus.Reviewed, DateTimeOffset.UtcNow));
        var secondClaim = repository.AddMemoryClaim(new MemoryClaim("claim-scoped-second", "Participant prefers warm tea", null, "prefers", "warm tea", ClaimStatus.Reviewed, DateTimeOffset.UtcNow));
        repository.AddEvidenceClaimLink(new EvidenceClaimLink(evidence.EvidenceId, claim.MemoryClaimId, "supports", DateTimeOffset.UtcNow));
        repository.AddEvidenceClaimLink(new EvidenceClaimLink(evidence.EvidenceId, secondClaim.MemoryClaimId, "contextualises", DateTimeOffset.UtcNow));
        repository.AddReviewAnnotation(new ReviewAnnotation("annotation-scoped", "memory_claim", claim.MemoryClaimId, "admin-1", "family_assessment", "private admin note", "supported", DateTimeOffset.UtcNow));

        var result = ArchiveExporter.ExportRedacted(archive, fixture.ExportRoot, [claim.MemoryClaimId, secondClaim.MemoryClaimId]);
        Assert.Contains(claim.MemoryClaimId, result.ExportedClaimIds);
        Assert.Contains(secondClaim.MemoryClaimId, result.ExportedClaimIds);
        Assert.Equal([evidence.EvidenceId], result.ExportedEvidenceIds);
        Assert.False(File.Exists(Path.Combine(result.ExportDirectory, "archive.sqlite")));
        Assert.False(Directory.Exists(Path.Combine(result.ExportDirectory, "media")));
        Assert.DoesNotContain(".snapshot.sqlite", Directory.EnumerateFiles(result.ExportDirectory).Select(Path.GetFileName));

        var claimsJson = File.ReadAllText(Path.Combine(result.ExportDirectory, "memory_claims.jsonl"));
        var evidenceJson = File.ReadAllText(Path.Combine(result.ExportDirectory, "evidence.jsonl"));
        var linksJson = File.ReadAllText(Path.Combine(result.ExportDirectory, "claim_evidence_links.jsonl"));
        var annotationsJson = File.ReadAllText(Path.Combine(result.ExportDirectory, "annotations.jsonl"));
        var manifestJson = File.ReadAllText(result.ManifestPath);
        Assert.Contains("Participant likes fish balls", claimsJson, StringComparison.Ordinal);
        Assert.Contains("source_withheld", evidenceJson, StringComparison.Ordinal);
        Assert.Contains("\"statement\":\"[REDACTED]\"", evidenceJson, StringComparison.Ordinal);
        Assert.DoesNotContain("private evidence statement", evidenceJson, StringComparison.Ordinal);
        Assert.DoesNotContain("private original expression", evidenceJson, StringComparison.Ordinal);
        Assert.DoesNotContain(source.SourceId, evidenceJson, StringComparison.Ordinal);
        Assert.DoesNotContain(revision.TranscriptRevisionId, evidenceJson, StringComparison.Ordinal);
        Assert.Contains("source_reference", linksJson, StringComparison.Ordinal);
        Assert.Contains(secondClaim.MemoryClaimId, linksJson, StringComparison.Ordinal);
        Assert.Contains("content_withheld", annotationsJson, StringComparison.Ordinal);
        Assert.DoesNotContain("private admin note", annotationsJson, StringComparison.Ordinal);
        Assert.Contains("scoped-redacted", manifestJson, StringComparison.Ordinal);
        Assert.Contains("source_audio_included", manifestJson, StringComparison.Ordinal);
        Assert.DoesNotContain(source.FilePath!, manifestJson, StringComparison.Ordinal);
    }

    [Fact]
    public void Scoped_redacted_export_rejects_empty_or_unknown_claim_selection()
    {
        using var fixture = new OperationsFixture();
        using var archive = fixture.CreateArchive();

        Assert.Throws<ArgumentException>(() => ArchiveExporter.ExportRedacted(archive, fixture.ExportRoot, []));
        Assert.Throws<InvalidDataException>(() => ArchiveExporter.ExportRedacted(archive, fixture.ExportRoot, ["missing-claim"]));
        Assert.Empty(Directory.GetDirectories(fixture.ExportRoot, "memento-scoped-export-*"));
    }

    [Fact]
    public void Scoped_redacted_export_rejects_unreviewed_claims()
    {
        using var fixture = new OperationsFixture();
        using var archive = fixture.CreateArchive();
        var repository = new ArchiveRepository(archive);
        repository.AddMemoryClaim(new MemoryClaim("candidate-only", "Candidate claim", null, "is", "candidate", ClaimStatus.Candidate, DateTimeOffset.UtcNow));

        Assert.Throws<InvalidDataException>(() => ArchiveExporter.ExportRedacted(archive, fixture.ExportRoot, ["candidate-only"]));
        Assert.Empty(Directory.GetDirectories(fixture.ExportRoot, "memento-scoped-export-*"));
    }

    [Fact]
    public void Export_rejects_tampered_media_and_removes_incomplete_bundle()
    {
        using var fixture = new OperationsFixture();
        using var archive = fixture.CreateArchive();
        var repository = new ArchiveRepository(archive);
        var session = repository.AddSession(DateTimeOffset.UtcNow, PrivacyMode.Normal);
        var audio = Path.Combine(fixture.DirectoryPath, "tampered.wav");
        var original = new byte[] { 1, 2, 3, 4 };
        File.WriteAllBytes(audio, original);
        var expectedHash = Convert.ToHexString(SHA256.HashData(original)).ToLowerInvariant();
        repository.AddSource(new SourceMetadata(
            "source-tampered-export", "audio", session.SessionId, null, audio, "PCM WAV", 48000, 1, 16,
            original.Length, 1, expectedHash, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "finalized", DateTimeOffset.UtcNow));

        File.WriteAllBytes(audio, [9, 8, 7, 6]);

        Assert.Throws<InvalidDataException>(() => ArchiveExporter.Export(archive, fixture.ExportRoot, includeMedia: true));
        Assert.Empty(Directory.GetDirectories(fixture.ExportRoot, "memento-export-*"));
        Assert.Equal([9, 8, 7, 6], File.ReadAllBytes(audio));
    }

    [Fact]
    public void Backup_file_replacement_is_complete_before_destination_is_replaced()
    {
        using var fixture = new OperationsFixture();
        Directory.CreateDirectory(fixture.ExportRoot);
        var source = Path.Combine(fixture.ExportRoot, "source.bin");
        var destination = Path.Combine(fixture.ExportRoot, "backup.memento");
        var restored = Path.Combine(fixture.ExportRoot, "restored.bin");
        File.WriteAllBytes(source, [1, 2, 3, 4]);
        File.WriteAllText(destination, "stale partial output");

        ArchiveBackupProtector.EncryptFile(source, destination, "test-password");
        ArchiveBackupProtector.DecryptFile(destination, restored, "test-password");

        Assert.Equal(File.ReadAllBytes(source), File.ReadAllBytes(restored));
        Assert.Empty(Directory.GetFiles(fixture.ExportRoot, "backup.memento.*.tmp"));
    }

    [Fact]
    public void Encrypted_backup_streams_large_files_and_reads_legacy_format()
    {
        using var fixture = new OperationsFixture();
        Directory.CreateDirectory(fixture.ExportRoot);
        var source = Path.Combine(fixture.ExportRoot, "large-source.bin");
        var encrypted = Path.Combine(fixture.ExportRoot, "large-backup.memento");
        var restored = Path.Combine(fixture.ExportRoot, "large-restored.bin");
        var content = new byte[(1024 * 1024 * 2) + 123];
        for (var index = 0; index < content.Length; index++)
            content[index] = (byte)(index % 251);
        File.WriteAllBytes(source, content);

        ArchiveBackupProtector.EncryptFile(source, encrypted, "test-password");
        using (var header = File.OpenRead(encrypted))
        {
            var magic = new byte[8];
            header.ReadExactly(magic);
            Assert.Equal("MEMENTO2"u8.ToArray(), magic);
        }
        ArchiveBackupProtector.DecryptFile(encrypted, restored, "test-password");
        using (var restoredStream = File.OpenRead(restored))
            Assert.Equal(SHA256.HashData(content), SHA256.HashData(restoredStream));

        var legacy = Path.Combine(fixture.ExportRoot, "legacy-backup.memento");
        var legacyPlain = new byte[] { 3, 1, 4, 1, 5, 9 };
        var salt = RandomNumberGenerator.GetBytes(16);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var key = Rfc2898DeriveBytes.Pbkdf2("test-password", salt, 150_000, HashAlgorithmName.SHA256, 32);
        var cipher = new byte[legacyPlain.Length];
        var tag = new byte[16];
        using (var aes = new AesGcm(key, tag.Length))
            aes.Encrypt(nonce, legacyPlain, cipher, tag);
        using (var legacyStream = File.Create(legacy))
        {
            legacyStream.Write("MEMENTO1"u8);
            legacyStream.Write(salt);
            legacyStream.Write(nonce);
            legacyStream.Write(tag);
            legacyStream.Write(cipher);
        }

        var legacyRestored = Path.Combine(fixture.ExportRoot, "legacy-restored.bin");
        ArchiveBackupProtector.DecryptFile(legacy, legacyRestored, "test-password");
        Assert.Equal(legacyPlain, File.ReadAllBytes(legacyRestored));
    }

    [Fact]
    public void Streaming_backup_rejects_tampered_and_trailing_chunks_without_output()
    {
        using var fixture = new OperationsFixture();
        Directory.CreateDirectory(fixture.ExportRoot);
        var source = Path.Combine(fixture.ExportRoot, "tamper-source.bin");
        var encrypted = Path.Combine(fixture.ExportRoot, "tamper-backup.memento");
        var tampered = Path.Combine(fixture.ExportRoot, "tampered-backup.memento");
        var restored = Path.Combine(fixture.ExportRoot, "tampered-restored.bin");
        File.WriteAllBytes(source, Enumerable.Repeat((byte)0x5A, 1024 * 1024 + 11).ToArray());
        ArchiveBackupProtector.EncryptFile(source, encrypted, "test-password");

        var bytes = File.ReadAllBytes(encrypted);
        bytes[8 + 16 + 4 + 12 + 16] ^= 0x7F;
        File.WriteAllBytes(tampered, bytes);
        Assert.Throws<InvalidDataException>(() => ArchiveBackupProtector.DecryptFile(tampered, restored, "test-password"));
        Assert.False(File.Exists(restored));

        var trailing = Path.Combine(fixture.ExportRoot, "trailing-backup.memento");
        File.Copy(encrypted, trailing);
        using (var append = new FileStream(trailing, FileMode.Append, FileAccess.Write, FileShare.None))
            append.WriteByte(0x42);
        Assert.Throws<InvalidDataException>(() => ArchiveBackupProtector.DecryptFile(trailing, restored, "test-password"));
        Assert.False(File.Exists(restored));
    }

    [Fact]
    public void Restore_rejects_a_manifest_that_omits_the_archive_snapshot()
    {
        using var fixture = new OperationsFixture();
        var exportDirectory = Path.Combine(fixture.ExportRoot, "malformed-export");
        Directory.CreateDirectory(exportDirectory);
        File.WriteAllBytes(Path.Combine(exportDirectory, "archive.sqlite"), [1, 2, 3]);
        File.WriteAllText(Path.Combine(exportDirectory, "manifest.json"), "{\"schema_version\":16,\"files\":[]}");
        var backup = Path.Combine(fixture.ExportRoot, "malformed-manifest.memento");
        var restore = Path.Combine(fixture.ExportRoot, "malformed-manifest-restore");

        ArchiveBackupProtector.EncryptDirectory(exportDirectory, backup, "test-password");
        var report = ArchiveBackupProtector.DecryptDirectory(backup, restore, "test-password");

        Assert.False(report.IntegrityOk);
        Assert.Contains(report.Findings, finding => finding.Contains("archive.sqlite", StringComparison.Ordinal));
    }

    [Fact]
    public void Restore_reports_manifest_entries_with_non_string_fields()
    {
        using var fixture = new OperationsFixture();
        var exportDirectory = Path.Combine(fixture.ExportRoot, "invalid-entry-export");
        Directory.CreateDirectory(exportDirectory);
        File.WriteAllBytes(Path.Combine(exportDirectory, "archive.sqlite"), [1, 2, 3]);
        File.WriteAllText(Path.Combine(exportDirectory, "manifest.json"), "{\"schema_version\":16,\"files\":[{\"path\":123,\"sha256\":true}]}");
        var backup = Path.Combine(fixture.ExportRoot, "invalid-entry-manifest.memento");
        var restore = Path.Combine(fixture.ExportRoot, "invalid-entry-manifest-restore");

        ArchiveBackupProtector.EncryptDirectory(exportDirectory, backup, "test-password");
        var report = ArchiveBackupProtector.DecryptDirectory(backup, restore, "test-password");

        Assert.False(report.IntegrityOk);
        Assert.Contains(report.Findings, finding => finding.Contains("invalid file entry", StringComparison.Ordinal));
    }

    [Fact]
    public void Restore_rejects_reparse_point_destination_without_following_it()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new OperationsFixture();
        var exportDirectory = Path.Combine(fixture.ExportRoot, "reparse-export");
        Directory.CreateDirectory(exportDirectory);
        var archiveBytes = new byte[] { 1, 2, 3 };
        File.WriteAllBytes(Path.Combine(exportDirectory, "archive.sqlite"), archiveBytes);
        var archiveHash = Convert.ToHexString(SHA256.HashData(archiveBytes)).ToLowerInvariant();
        File.WriteAllText(Path.Combine(exportDirectory, "manifest.json"), $"{{\"schema_version\":17,\"files\":[{{\"path\":\"archive.sqlite\",\"sha256\":\"{archiveHash}\"}}]}}");
        var backup = Path.Combine(fixture.ExportRoot, "reparse-backup.memento");
        ArchiveBackupProtector.EncryptDirectory(exportDirectory, backup, "test-password");

        var actualTarget = Path.Combine(fixture.ExportRoot, "reparse-target");
        Directory.CreateDirectory(actualTarget);
        var link = Path.Combine(fixture.ExportRoot, "reparse-link");
        try
        {
            Directory.CreateSymbolicLink(link, actualTarget);
        }
        catch (Exception error) when (error is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            return;
        }

        Assert.Throws<IOException>(() => ArchiveBackupProtector.DecryptDirectory(backup, link, "test-password"));
        Assert.Empty(Directory.EnumerateFileSystemEntries(actualTarget));
    }

    [Fact]
    public void File_backup_rejects_reparse_point_parent_without_writing_through_it()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new OperationsFixture();
        Directory.CreateDirectory(fixture.ExportRoot);
        var source = Path.Combine(fixture.ExportRoot, "reparse-source.bin");
        File.WriteAllBytes(source, [1, 2, 3]);
        var actualTarget = Path.Combine(fixture.ExportRoot, "file-reparse-target");
        Directory.CreateDirectory(actualTarget);
        var link = Path.Combine(fixture.ExportRoot, "file-reparse-link");
        try
        {
            Directory.CreateSymbolicLink(link, actualTarget);
        }
        catch (Exception error) when (error is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            return;
        }

        Assert.Throws<IOException>(() => ArchiveBackupProtector.EncryptFile(source, Path.Combine(link, "backup.memento"), "test-password"));
        Assert.Empty(Directory.EnumerateFileSystemEntries(actualTarget));
    }

    [Fact]
    public void Backup_rejects_same_source_and_destination_path()
    {
        using var fixture = new OperationsFixture();
        Directory.CreateDirectory(fixture.ExportRoot);
        var path = Path.Combine(fixture.ExportRoot, "same-path-backup.memento");
        File.WriteAllBytes(path, [1, 2, 3]);

        Assert.Throws<ArgumentException>(() => ArchiveBackupProtector.EncryptFile(path, path, "test-password"));
        Assert.Throws<ArgumentException>(() => ArchiveBackupProtector.DecryptFile(path, path, "test-password"));
        Assert.Throws<ArgumentException>(() => ArchiveBackupProtector.ReencryptFile(path, path, "old-password", "new-password"));
        Assert.Throws<ArgumentException>(() => ArchiveBackupProtector.ReencryptDirectory(path, path, "old-password", "new-password"));
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(path));
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
        Assert.Equal(17, report.SchemaVersion);
        Assert.Equal(1, report.RecoverableAudioCount);
        Assert.Equal(1, report.PendingConversationJobs);
        Assert.Equal(0, report.InvalidDerivedSpeechAssetCount);
        Assert.Equal(1, report.InvalidSourceAssetCount);
        Assert.NotEmpty(report.Findings);

        File.AppendAllBytes(derivedPath, [99]);
        var tampered = ArchiveHealthCheck.Run(archive, fixture.AudioRoot);
        Assert.Equal(1, tampered.InvalidDerivedSpeechAssetCount);
    }

    [Fact]
    public void Health_check_verifies_recovered_source_assets()
    {
        using var fixture = new OperationsFixture();
        using var archive = fixture.CreateArchive();
        var repository = new ArchiveRepository(archive);
        var session = repository.AddSession(DateTimeOffset.UtcNow, PrivacyMode.LocalCaptureOnly);
        var format = new PcmWaveFormat(16000, 1, 16);
        FinalizedAudioAsset asset;
        using (var writer = PcmWaveWriter.Create(fixture.AudioRoot, session.SessionId, DateTimeOffset.UtcNow, format, "recovered-health"))
        {
            writer.Append(new byte[320]);
            asset = writer.FinalizeAsset();
        }

        repository.AddSource(new SourceMetadata(asset.SourceId, "audio", session.SessionId, null, asset.FilePath, "PCM WAV", format.SampleRate, format.Channels, format.BitsPerSample, asset.ByteLength, asset.DurationMs, asset.Sha256, asset.StartedAt, asset.FinalizedAt, "recovered", DateTimeOffset.UtcNow));
        File.AppendAllBytes(asset.FilePath, [99]);

        var report = ArchiveHealthCheck.Run(archive, fixture.AudioRoot);

        Assert.Equal(1, report.InvalidSourceAssetCount);
        Assert.Contains(report.Findings, finding => finding.Contains("recovered", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Health_check_ignores_fresh_processing_jobs_but_reports_stale_jobs()
    {
        using var fixture = new OperationsFixture();
        using var archive = fixture.CreateArchive();
        var repository = new ArchiveRepository(archive);
        var session = repository.AddSession(DateTimeOffset.Parse("2026-09-13T10:00:00Z"), PrivacyMode.Normal);
        var source = repository.AddSource(fixture.Source(session.SessionId, "source-health-processing"));
        var processing = new ConversationJob(
            "job-health-processing",
            session.SessionId,
            null,
            source.SourceId,
            "durable_transcription",
            ConversationJobStatus.Processing,
            1,
            null,
            null,
            DateTimeOffset.Parse("2026-09-13T09:59:00Z"),
            DateTimeOffset.Parse("2026-09-13T09:59:00Z"));
        repository.AddConversationJob(processing);

        var fresh = ArchiveHealthCheck.Run(archive, fixture.AudioRoot, DateTimeOffset.Parse("2026-09-13T10:03:00Z"));
        var stale = ArchiveHealthCheck.Run(archive, fixture.AudioRoot, DateTimeOffset.Parse("2026-09-13T10:05:00Z"));

        Assert.Equal(0, fresh.PendingConversationJobs);
        Assert.Equal(1, stale.PendingConversationJobs);
    }

    private sealed class CountingSearchProvider : ISearchProvider
    {
        public string Provider => "counting-test";
        public int CallCount { get; private set; }

        public Task<ExternalInformationResult> SearchAsync(string query, CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(new ExternalInformationResult(query, Provider, DateTimeOffset.UtcNow, [], true));
        }
    }

    private sealed class TrustingSearchProvider : ISearchProvider
    {
        public string Provider => "trusting-test";

        public Task<ExternalInformationResult> SearchAsync(string query, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new ExternalInformationResult(
                "spoofed query",
                "spoofed-provider",
                DateTimeOffset.UnixEpoch,
                [],
                false));
        }
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
