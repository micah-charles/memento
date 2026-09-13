using Memento.Core.Audio;
using Memento.Core.Admin;
using Memento.Core.Conversation;
using Memento.Core.Domain;
using Memento.Core.Storage;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Memento.App;

public sealed partial class MainWindow : Window
{
    private readonly ArchiveRepository _repository;
    private readonly string _audioRoot;
    private readonly int _recoverableAudioCount;
    private readonly DispatcherQueue _dispatcherQueue;
    private readonly BoundedVoiceConversationService? _voiceConversation;
    private readonly ISpeechOutputPlayback? _speechPlayback;
    private readonly FamilyAdminReviewService? _adminReview;
    private readonly string? _adminActorId;
    private AudioCaptureController? _capture;
    private Session? _session;
    private SourceMetadata? _lastSource;
    private DerivedSpeechAsset? _latestSpeechAsset;
    private bool _processing;
    private bool _recordingEnabled;

    public MainWindow(ArchiveRepository repository, string audioRoot, int recoverableAudioCount = 0, BoundedVoiceConversationService? voiceConversation = null, ISpeechOutputPlayback? speechPlayback = null, FamilyAdminReviewService? adminReview = null, string? adminActorId = null)
    {
        _repository = repository;
        _audioRoot = audioRoot;
        _recoverableAudioCount = recoverableAudioCount;
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
        _voiceConversation = voiceConversation;
        _speechPlayback = speechPlayback;
        _adminReview = adminReview;
        _adminActorId = adminActorId;
        InitializeComponent();
        Closed += MainWindow_Closed;
        _recordingEnabled = !string.Equals(_repository.GetSetting("recording_enabled"), "0", StringComparison.Ordinal);
        RecordingEnabledCheckBox.IsChecked = _recordingEnabled;
        _lastSource = _repository.GetLatestFinalizedSource();
        if (_lastSource?.SessionId is not null)
            _session = _repository.GetSession(_lastSource.SessionId);
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
            _repository.AddConsent(_session.SessionId, ConsentScope.LocalCapture, _session.PrivacyMode, false, "privacy-1");
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
        if (_voiceConversation is null || _lastSource?.FilePath is null || _session is null || _session.EndedAt is null || _capture?.State == AudioCaptureState.Capturing || _session.PrivacyMode == PrivacyMode.LocalCaptureOnly || CloudConsentCheckBox.IsChecked != true || _processing)
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

    private async void AdminReviewButton_Click(object sender, RoutedEventArgs e)
    {
        if (_adminReview is null || string.IsNullOrWhiteSpace(_adminActorId)) return;
        IReadOnlyList<MemoryClaim> candidates;
        try
        {
            candidates = _adminReview.ListCandidates(_adminActorId);
        }
        catch (UnauthorizedAccessException)
        {
            await ShowAdminMessageAsync("未獲授權", "只有獲授權嘅 Windows 管理員可以進入家庭管理審閱。", "知道了");
            return;
        }

        if (candidates.Count == 0)
        {
            await ShowAdminMessageAsync("未有候選記憶", "目前沒有需要家庭管理審閱嘅候選記憶。", "關閉");
            return;
        }

        var claim = candidates[0];
        var dialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = "家庭管理審閱",
            Content = new TextBlock { Text = claim.Statement, TextWrapping = TextWrapping.Wrap, FontSize = 18 },
            PrimaryButtonText = "支持候選記憶",
            SecondaryButtonText = "拒絕候選記憶",
            CloseButtonText = "稍後處理",
            DefaultButton = ContentDialogButton.Close
        };
        var result = await dialog.ShowAsync();
        try
        {
            if (result == ContentDialogResult.Primary)
            {
                _adminReview.AnnotateClaim(_adminActorId, claim, "family_assessment", "家庭管理審閱：支持候選記憶。", "supported");
                StatusText.Text = "已記錄家庭支持；仍保留原始證據鏈。";
            }
            else if (result == ContentDialogResult.Secondary)
            {
                _adminReview.AnnotateClaim(_adminActorId, claim, "admin_annotation", "家庭管理審閱：拒絕候選記憶。", "rejected");
                StatusText.Text = "已拒絕候選記憶；原始證據仍然保留。";
            }
        }
        catch (Exception)
        {
            StatusText.Text = "未能儲存家庭管理審閱。";
        }
    }

    private async Task ShowAdminMessageAsync(string title, string message, string closeText)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = title,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
            CloseButtonText = closeText
        };
        await dialog.ShowAsync();
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
        ProcessButton.IsEnabled = !_processing && _voiceConversation is not null && _capture?.State != AudioCaptureState.Capturing && _lastSource?.FilePath is not null && _session?.EndedAt is not null && _session.PrivacyMode != PrivacyMode.LocalCaptureOnly && CloudConsentCheckBox.IsChecked == true;
        PlaySpeechButton.IsEnabled = !_processing && _latestSpeechAsset is not null && _speechPlayback is not null;
    }
}
