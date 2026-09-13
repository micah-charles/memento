using Microsoft.UI.Xaml;
using Memento.Core.Audio;
using Memento.Core.Conversation;
using Memento.Core.Memory;
using Memento.Core.Security;
using Memento.Core.Storage;

namespace Memento.App;

public partial class App : Application
{
    private Window? _window;
    private HttpClient? _httpClient;
    internal static SqliteArchive? Archive { get; private set; }
    internal static ArchiveRepository? Repository { get; private set; }
    internal static string? DataDirectory { get; private set; }

    public App() => InitializeComponent();

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var dataDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MEMENTO");
        DataDirectory = dataDirectory;
        Archive = new SqliteArchive(Path.Combine(dataDirectory, "data", "memory.db"));
        Archive.Initialize();
        Repository = new ArchiveRepository(Archive);

        var audioDirectory = Path.Combine(dataDirectory, "raw", "audio");
        var recoverableAudioCount = AudioRecoveryScanner.Scan(audioDirectory).Count;
        var derivedAudioStore = new DerivedAudioStore(Repository, Path.Combine(dataDirectory, "derived", "audio"));
        _httpClient = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
        var credentials = new WindowsCredentialProvider();
        var transcriptionProvider = new OpenAiTranscriptionProvider(_httpClient, credentials);
        var conversationProvider = new OpenAiResponsesProvider(_httpClient, credentials, "gpt-5.6-terra");
        var speechOutputProvider = new OpenAiSpeechOutputProvider(_httpClient, credentials);
        var extractionProvider = new OpenAiMemoryExtractionProvider(_httpClient, credentials, "gpt-5.6-terra");
        var voiceConversation = new BoundedVoiceConversationService(
            Repository,
            transcriptionProvider,
            conversationProvider,
            speechOutputProvider,
            derivedAudioStore,
            new WaveFileSpeechOutputPlayback(derivedAudioStore),
            queueExtractionJobs: true);
        var retryProcessor = new CompositeConversationJobProcessor(
            new DurableTranscriptionJobProcessor(Repository, transcriptionProvider, queueExtractionJobs: true),
            new DurableResponseJobProcessor(Repository, conversationProvider, speechOutputProvider, derivedAudioStore),
            new DurableMemoryExtractionJobProcessor(Repository, extractionProvider));
        var retryWorker = new ConversationJobWorker(Repository, retryProcessor);
        var adminAuthorizer = new WindowsAdministratorAuthorizer();
        var adminReview = new Memento.Core.Admin.FamilyAdminReviewService(Repository, adminAuthorizer);
        var adminActorId = adminAuthorizer.GetCurrentActorId();
        var deletion = adminAuthorizer.IsAuthorized(adminActorId) ? new Memento.Core.Admin.ArchiveDeletionService(Repository, adminAuthorizer) : null;
        var withdrawal = adminAuthorizer.IsAuthorized(adminActorId) ? new Memento.Core.Admin.ArchiveWithdrawalService(Repository, adminAuthorizer) : null;
        _window = new MainWindow(Repository, audioDirectory, recoverableAudioCount, voiceConversation, new WaveFileSpeechOutputPlayback(derivedAudioStore), adminReview, adminActorId, retryWorker, () => !string.IsNullOrWhiteSpace(credentials.GetApiKey()), dataDirectory, deletion, withdrawal);
        _window.Activate();
    }
}
