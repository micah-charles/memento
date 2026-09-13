using Microsoft.UI.Xaml;
using Memento.Core.Audio;
using Memento.Core.Conversation;
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
        var voiceConversation = new BoundedVoiceConversationService(
            Repository,
            new OpenAiTranscriptionProvider(_httpClient, credentials),
            new OpenAiResponsesProvider(_httpClient, credentials, "gpt-5.6-terra"),
            new OpenAiSpeechOutputProvider(_httpClient, credentials),
            derivedAudioStore,
            new WaveFileSpeechOutputPlayback(derivedAudioStore));
        var adminAuthorizer = new WindowsAdministratorAuthorizer();
        var adminReview = new Memento.Core.Admin.FamilyAdminReviewService(Repository, adminAuthorizer);
        _window = new MainWindow(Repository, audioDirectory, recoverableAudioCount, voiceConversation, new WaveFileSpeechOutputPlayback(derivedAudioStore), adminReview, adminAuthorizer.GetCurrentActorId());
        _window.Activate();
    }
}
