using Microsoft.UI.Xaml;
using Memento.Core.Storage;

namespace Memento.App;

public partial class App : Application
{
    private Window? _window;
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

        _window = new MainWindow(Repository, Path.Combine(dataDirectory, "raw", "audio"));
        _window.Activate();
    }
}
