using Memento.Core.Audio;
using Memento.Core.Domain;
using Memento.Core.Storage;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace Memento.App;

public sealed partial class MainWindow : Window
{
    private readonly ArchiveRepository _repository;
    private readonly string _audioRoot;
    private readonly int _recoverableAudioCount;
    private readonly DispatcherQueue _dispatcherQueue;
    private AudioCaptureController? _capture;
    private Session? _session;
    private bool _recordingEnabled;

    public MainWindow(ArchiveRepository repository, string audioRoot, int recoverableAudioCount = 0)
    {
        _repository = repository;
        _audioRoot = audioRoot;
        _recoverableAudioCount = recoverableAudioCount;
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
        InitializeComponent();
        Closed += MainWindow_Closed;
        _recordingEnabled = !string.Equals(_repository.GetSetting("recording_enabled"), "0", StringComparison.Ordinal);
        RecordingEnabledCheckBox.IsChecked = _recordingEnabled;
        UpdateRecordControl();
        if (_recoverableAudioCount > 0)
            StatusText.Text = $"有 {_recoverableAudioCount} 段未完成錄音，已保留待處理 · Local archive";
    }

    private void ConsentChanged(object sender, RoutedEventArgs e)
    {
        UpdateRecordControl();
        if (_session is not null && ConsentCheckBox.IsChecked != true)
            _repository.AddConsent(_session.SessionId, ConsentScope.LocalCapture, PrivacyMode.LocalCaptureOnly, false, "privacy-1");
    }

    private void RecordingEnabledChanged(object sender, RoutedEventArgs e)
    {
        _recordingEnabled = RecordingEnabledCheckBox.IsChecked == true;
        _repository.SetSetting("recording_enabled", _recordingEnabled ? "1" : "0");
        UpdateRecordControl();
        if (!_recordingEnabled)
            StatusText.Text = "本機錄音已停用。";
    }

    private void RecordButton_Click(object sender, RoutedEventArgs e)
    {
        if (_capture?.State == AudioCaptureState.Capturing)
        {
            try
            {
                _capture.Stop();
                if (_session is not null)
                    _session = _repository.EndSession(_session);
                StatusText.Text = "已儲存本機錄音 · Local archive";
            }
            catch (Exception error)
            {
                StatusText.Text = $"錄音未能完成：{error.Message}";
            }
            finally
            {
                RecordButton.Content = "開始錄音";
                ConsentCheckBox.IsEnabled = true;
                RecordingEnabledCheckBox.IsEnabled = true;
                UpdateRecordControl();
            }

            return;
        }

        try
        {
            _session = _repository.AddSession(DateTimeOffset.UtcNow, PrivacyMode.LocalCaptureOnly);
            _repository.AddConsent(_session.SessionId, ConsentScope.LocalCapture, PrivacyMode.LocalCaptureOnly, true, "privacy-1");
            _capture = new AudioCaptureController(_repository, _audioRoot);
            _capture.CaptureFailed += CaptureFailed;
            _capture.Start(_session.SessionId, null, ConsentCheckBox.IsChecked == true, format => new WaveInAudioInput(format));
            StatusText.Text = "Listening… 本機錄音中";
            RecordButton.Content = "停止錄音";
            ConsentCheckBox.IsEnabled = false;
            RecordingEnabledCheckBox.IsEnabled = false;
        }
        catch (ConsentRequiredException)
        {
            StatusText.Text = "請先同意本機錄音。";
        }
        catch (Exception error)
        {
            StatusText.Text = $"無法使用咪高風：{error.Message}";
            if (_session is not null && _session.EndedAt is null)
                _session = _repository.EndSession(_session);
            RecordButton.Content = "開始錄音";
            ConsentCheckBox.IsEnabled = true;
            RecordingEnabledCheckBox.IsEnabled = true;
            UpdateRecordControl();
        }
    }

    private void MainWindow_Closed(object sender, WindowEventArgs args)
    {
        if (_capture?.State == AudioCaptureState.Capturing)
        {
            _capture.AbortForRecovery();
            if (_session is not null)
                _session = _repository.EndSession(_session);
        }
    }

    private void CaptureFailed(object? sender, Exception error)
    {
        _dispatcherQueue.TryEnqueue(() =>
        {
            StatusText.Text = $"錄音中斷，已保留暫存檔：{error.Message}";
            RecordButton.Content = "開始錄音";
            ConsentCheckBox.IsEnabled = true;
            RecordingEnabledCheckBox.IsEnabled = true;
            UpdateRecordControl();
        });
    }

    private void UpdateRecordControl()
        => RecordButton.IsEnabled = _recordingEnabled && ConsentCheckBox.IsChecked == true && ConsentCheckBox.IsEnabled;
}
