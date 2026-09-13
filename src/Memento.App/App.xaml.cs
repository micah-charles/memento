using Microsoft.UI.Xaml;
using Memento.Core.Storage;

namespace Memento.App;

public partial class App : Application
{
    private Window? _window;

    public App() => InitializeComponent();

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var dataDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MEMENTO");
        using var archive = new SqliteArchive(Path.Combine(dataDirectory, "data", "memory.db"));
        archive.Initialize();

        _window = new MainWindow();
        _window.Activate();
    }
}
