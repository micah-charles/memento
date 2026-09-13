using Memento.Core.Audio;
using Memento.Core.Conversation;
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
    private readonly BoundedVoiceConversationService? _voiceConversation;
    private readonly ISpeechOutputPlayback? _speechPlayback;
    private AudioCaptureController? _capture;
    private Session? _session;
    private SourceMetadata? _lastSource;
    private DerivedSpeechAsset? _latestSpeechAsset;
    private bool _processing;
    private bool _recordingEnabled;

    public MainWindow(ArchiveRepository repository, string audioRoot, int recoverableAudioCount = 0, BoundedVoiceConversationService? voiceConversation = null, ISpeechOutputPlayback? speechPlayback = null)
    {
        _repository = repository;
        _audioRoot = audioRoot;
        _recoverableAudioCount = recoverableAudioCount;
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
        _voiceConversation = voiceConversation;
        _speechPlayback = speechPlayback;
        InitializeComponent();
        Closed += MainWindow_Closed;
        _recordingEnabled = !string.Equals(_repository.GetSetting("recording_enabled"), "0", StringComparison.Ordinal);
        RecordingEnabledCheckBox.IsChecked = _recordingEnabled;
        _latestSpeechAsset = _repository.GetLatestDerivedSpeechAsset();
        PlaySpeechButton.IsEnabled = _latestSpeechAsset is not null && _speechPlayback is not null;
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

    private void CloudConsentChanged(object sender, RoutedEventArgs e)
    {
        if (_session is not null && _session.EndedAt is null && CloudConsentCheckBox.IsChecked == true)
            _repository.AddConsent(_session.SessionId, ConsentScope.CloudTranscription, PrivacyMode.Normal, true, "privacy-1");
        else if (_session is not null && _session.EndedAt is not null && _session.PrivacyMode != PrivacyMode.LocalCaptureOnly && CloudConsentCheckBox.IsChecked != true)
            _repository.AddConsent(_session.SessionId, ConsentScope.CloudTranscription, _session.PrivacyMode, false, "privacy-1");
        UpdateRecordControl();
    }

    private void RecordButton_Click(object sender, RoutedEventArgs e)
    {
        if (_capture?.State == AudioCaptureState.Capturing)
        {
            try
            {
                _lastSource = _capture.Stop();
                if (_session is not null)
                    _session = _repository.EndSession(_session);
                StatusText.Text = "已儲存本機錄音 · Local archive";
            }
            catch (Exception)
            {
                StatusText.Text = "錄音未能完成，請檢查咪高風或 Windows 權限。";
            }
            finally
            {
                RecordButton.Content = "開始錄音";
                ConsentCheckBox.IsEnabled = true;
                CloudConsentCheckBox.IsEnabled = true;
                RecordingEnabledCheckBox.IsEnabled = true;
                UpdateRecordControl();
            }

            return;
        }

        try
        {
            var privacyMode = CloudConsentCheckBox.IsChecked == true ? PrivacyMode.Normal : PrivacyMode.LocalCaptureOnly;
            _session = _repository.AddSession(DateTimeOffset.UtcNow, privacyMode);
            _repository.AddConsent(_session.SessionId, ConsentScope.LocalCapture, privacyMode, true, "privacy-1");
            if (privacyMode != PrivacyMode.LocalCaptureOnly)
                _repository.AddConsent(_session.SessionId, ConsentScope.CloudTranscription, privacyMode, true, "privacy-1");
            _capture = new AudioCaptureController(_repository, _audioRoot);
            _capture.CaptureFailed += CaptureFailed;
            _capture.Start(_session.SessionId, null, ConsentCheckBox.IsChecked == true, format => new WaveInAudioInput(format));
            StatusText.Text = privacyMode == PrivacyMode.LocalCaptureOnly ? "Listening… 本機錄音中（只保留本機）" : "Listening… 本機錄音中（已同意完成後雲端處理）";
            RecordButton.Content = "停止錄音";
            ConsentCheckBox.IsEnabled = false;
            CloudConsentCheckBox.IsEnabled = false;
            RecordingEnabledCheckBox.IsEnabled = false;
        }
        catch (ConsentRequiredException)
        {
            StatusText.Text = "請先同意本機錄音。";
        }
        catch (Exception)
        {
            StatusText.Text = "無法使用咪高風，請檢查 Windows 權限或接駁。";
            if (_session is not null && _session.EndedAt is null)
                _session = _repository.EndSession(_session);
            RecordButton.Content = "開始錄音";
            ConsentCheckBox.IsEnabled = true;
            CloudConsentCheckBox.IsEnabled = true;
            RecordingEnabledCheckBox.IsEnabled = true;
            UpdateRecordControl();
        }
    }

    private async void ProcessButton_Click(object sender, RoutedEventArgs e)
    {
        if (_voiceConversation is null || _lastSource?.FilePath is null || _session is null || _session.PrivacyMode == PrivacyMode.LocalCaptureOnly || CloudConsentCheckBox.IsChecked != true || _processing)
            return;

        _processing = true;
        ProcessButton.IsEnabled = false;
        PlaySpeechButton.IsEnabled = false;
        StatusText.Text = "正在轉錄及準備回覆…";
        try
        {
            var request = new ConversationRequest(_session.SessionId, _lastSource.TurnId, _lastSource.FilePath, _session.PrivacyMode, true, DateTimeOffset.UtcNow, SourceId: _lastSource.SourceId);
            var result = await _voiceConversation.ExecuteAsync(request);
            _latestSpeechAsset = result.SpeechAsset ?? _repository.GetLatestDerivedSpeechAsset();
            StatusText.Text = result.Conversation.Response is null ? "錄音已保留；雲端暫時未能回覆。" : "已完成轉錄及回覆。";
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "雲端處理已取消；本機錄音仍然保留。";
        }
        catch (Exception)
        {
            StatusText.Text = "雲端處理未能完成；本機錄音仍然保留。";
        }
        finally
        {
            _processing = false;
            UpdateRecordControl();
        }
    }

    private async void PlaySpeechButton_Click(object sender, RoutedEventArgs e)
    {
        if (_speechPlayback is null || _latestSpeechAsset is null) return;
        PlaySpeechButton.IsEnabled = false;
        StatusText.Text = "播放中…";
        try
        {
            await _speechPlayback.PlayAsync(_latestSpeechAsset);
            StatusText.Text = "已播放最近回覆。";
        }
        catch (Exception)
        {
            StatusText.Text = "未能播放回覆，請檢查喇叭或輸出裝置。";
        }
        finally
        {
            PlaySpeechButton.IsEnabled = _latestSpeechAsset is not null && _speechPlayback is not null;
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
            StatusText.Text = "錄音中斷，已保留暫存檔；請檢查咪高風或 Windows 權限。";
            RecordButton.Content = "開始錄音";
            ConsentCheckBox.IsEnabled = true;
            CloudConsentCheckBox.IsEnabled = true;
            RecordingEnabledCheckBox.IsEnabled = true;
            UpdateRecordControl();
        });
    }

    private void UpdateRecordControl()
    {
        RecordButton.IsEnabled = _recordingEnabled && ConsentCheckBox.IsChecked == true && ConsentCheckBox.IsEnabled && !_processing;
        ProcessButton.IsEnabled = !_processing && _voiceConversation is not null && _lastSource?.FilePath is not null && _session?.PrivacyMode != PrivacyMode.LocalCaptureOnly && CloudConsentCheckBox.IsChecked == true;
        PlaySpeechButton.IsEnabled = !_processing && _latestSpeechAsset is not null && _speechPlayback is not null;
    }
}
