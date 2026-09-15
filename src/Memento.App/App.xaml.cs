using Microsoft.UI.Xaml;
using Memento.Core.Audio;
using Memento.Core.Conversation;
using Memento.Core.External;
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

    public App()
    {
        UnhandledException += (_, eventArgs) =>
        {
            try
            {
                var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MEMENTO", "logs");
                Directory.CreateDirectory(directory);
                File.AppendAllText(Path.Combine(directory, "app-errors.log"), $"{DateTimeOffset.UtcNow:O}\n{eventArgs.Exception}\n\n");
            }
            catch { }
        };
        InitializeComponent();
    }

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
        var credentials = new WindowsCredentialProvider();
        var applicationLock = new WindowsApplicationLock();
        BoundedVoiceConversationService? voiceConversation = null;
        RealtimeConversationOrchestrator? realtimeConversation = null;
        RealtimeStreamingOrchestrator? realtimeStreaming = null;
        CurrentInformationService? currentInformation = null;
        ConversationJobWorker? retryWorker = null;
        // Explicit opt-in only: the default companion uses the existing Codex account and local speech.
        if (Repository.GetSetting("optional_paid_api_enabled") == "1")
        {
            _httpClient = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
            var transcriptionProvider = new OpenAiTranscriptionProvider(_httpClient, credentials);
            var conversationProvider = new OpenAiResponsesProvider(_httpClient, credentials, "gpt-5.6-terra");
            var speechOutputProvider = new OpenAiSpeechOutputProvider(_httpClient, credentials);
            var realtimeProvider = new OpenAiRealtimeWebSocketProvider(credentials);
            realtimeConversation = new RealtimeConversationOrchestrator(Repository, realtimeProvider, derivedAudioStore);
            var realtimeStreamingProvider = new OpenAiRealtimeStreamingProvider(credentials);
            realtimeStreaming = new RealtimeStreamingOrchestrator(Repository, realtimeStreamingProvider, derivedAudioStore);
            var extractionProvider = new OpenAiMemoryExtractionProvider(_httpClient, credentials, "gpt-5.6-terra");
            currentInformation = new CurrentInformationService(new OpenAiWebSearchProvider(
                _httpClient,
                credentials,
                "gpt-5.6-terra",
                ["hko.gov.hk", "gov.hk", "td.gov.hk", "news.gov.hk"]));
            voiceConversation = new BoundedVoiceConversationService(
                Repository,
                transcriptionProvider,
                conversationProvider,
                speechOutputProvider,
                derivedAudioStore,
                new WaveFileSpeechOutputPlayback(derivedAudioStore),
                queueExtractionJobs: false);
            var retryProcessor = new CompositeConversationJobProcessor(
                new DurableTranscriptionJobProcessor(Repository, transcriptionProvider, queueExtractionJobs: false),
                new DurableResponseJobProcessor(Repository, conversationProvider, speechOutputProvider, derivedAudioStore),
                new DurableMemoryExtractionJobProcessor(Repository, extractionProvider));
            retryWorker = new ConversationJobWorker(Repository, retryProcessor);
        }
        var adminAuthorizer = new WindowsAdministratorAuthorizer();
        var adminReview = new Memento.Core.Admin.FamilyAdminReviewService(Repository, adminAuthorizer);
        var adminActorId = adminAuthorizer.GetCurrentActorId();
        var adminAuthorized = adminAuthorizer.IsAuthorized(adminActorId);
        var deletion = adminAuthorized ? new Memento.Core.Admin.ArchiveDeletionService(Repository, adminAuthorizer) : null;
        var withdrawal = adminAuthorized ? new Memento.Core.Admin.ArchiveWithdrawalService(Repository, adminAuthorizer) : null;
        var sourceAudioPlayback = adminAuthorized ? new WaveFileSourceAudioPlayback(dataDirectory) : null;
        _window = new MainWindow(Repository, audioDirectory, recoverableAudioCount, voiceConversation, new WaveFileSpeechOutputPlayback(derivedAudioStore), adminReview, adminActorId, retryWorker, () => !string.IsNullOrWhiteSpace(credentials.GetApiKey()), dataDirectory, deletion, withdrawal, sourceAudioPlayback, currentInformation, applicationLock, realtimeConversation, realtimeStreaming);
        _window.Activate();
    }

    internal void DisposeRuntimeServices()
    {
        _httpClient?.Dispose();
        _httpClient = null;
        Archive?.Dispose();
        Archive = null;
        Repository = null;
        DataDirectory = null;
    }
}
