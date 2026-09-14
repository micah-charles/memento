using Memento.Core.Audio;
using Memento.Core.Admin;
using Memento.Core.Conversation;
using Memento.Core.Domain;
using Memento.Core.External;
using Memento.Core.Security;
using Memento.Core.Storage;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace Memento.App;

public sealed partial class MainWindow : Window
{
    private readonly ArchiveRepository _repository;
    private readonly string _audioRoot;
    private readonly int _recoverableAudioCount;
    private readonly AudioRecoveryService _audioRecovery;
    private readonly DispatcherQueue _dispatcherQueue;
    private readonly string _dataRoot;
    private readonly BoundedVoiceConversationService? _voiceConversation;
    private readonly RealtimeConversationOrchestrator? _realtimeConversation;
    private readonly RealtimeStreamingOrchestrator? _realtimeStreaming;
    private readonly ISpeechOutputPlayback? _speechPlayback;
    private readonly ISourceAudioPlayback? _sourceAudioPlayback;
    private readonly FamilyAdminReviewService? _adminReview;
    private readonly ArchiveDeletionService? _deletion;
    private readonly ArchiveWithdrawalService? _withdrawal;
    private readonly CurrentInformationService? _currentInformation;
    private readonly string? _adminActorId;
    private readonly ConversationJobWorker? _retryWorker;
    private readonly Func<bool>? _credentialAvailable;
    private readonly IApplicationLock? _applicationLock;
    private AudioCaptureController? _capture;
    private Session? _session;
    private Turn? _turn;
    private SourceMetadata? _lastSource;
    private TranscriptRevision? _pendingClarificationRevision;
    private DerivedSpeechAsset? _latestSpeechAsset;
    private bool _processing;
    private bool _recordingEnabled;
    private bool _initializing;
    private bool _locked;
    private CancellationTokenSource? _retryCancellation;
    private Task? _retryTask;
    private CancellationTokenSource? _sourcePlaybackCancellation;
    private CancellationTokenSource? _currentInfoCancellation;
    private RealtimeStreamingArchiveSession? _activeRealtimeStreaming;
    private readonly object _archiveOperationGate = new();
    private TaskCompletionSource<bool> _archiveOperationsIdle = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _activeArchiveOperations;

    public MainWindow(ArchiveRepository repository, string audioRoot, int recoverableAudioCount = 0, BoundedVoiceConversationService? voiceConversation = null, ISpeechOutputPlayback? speechPlayback = null, FamilyAdminReviewService? adminReview = null, string? adminActorId = null, ConversationJobWorker? retryWorker = null, Func<bool>? credentialAvailable = null, string? dataRoot = null, ArchiveDeletionService? deletion = null, ArchiveWithdrawalService? withdrawal = null, ISourceAudioPlayback? sourceAudioPlayback = null, CurrentInformationService? currentInformation = null, IApplicationLock? applicationLock = null, RealtimeConversationOrchestrator? realtimeConversation = null, RealtimeStreamingOrchestrator? realtimeStreaming = null)
    {
        _repository = repository;
        _audioRoot = audioRoot;
        _recoverableAudioCount = recoverableAudioCount;
        _audioRecovery = new AudioRecoveryService(repository, audioRoot);
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
        _dataRoot = dataRoot is null ? Path.GetFullPath(Path.Combine(audioRoot, "..", "..")) : Path.GetFullPath(dataRoot);
        _voiceConversation = voiceConversation;
        _realtimeConversation = realtimeConversation;
        _realtimeStreaming = realtimeStreaming;
        _speechPlayback = speechPlayback;
        _sourceAudioPlayback = sourceAudioPlayback;
        _adminReview = adminReview;
        _deletion = deletion;
        _withdrawal = withdrawal;
        _currentInformation = currentInformation;
        _adminActorId = adminActorId;
        _retryWorker = retryWorker;
        _credentialAvailable = credentialAvailable;
        _applicationLock = applicationLock;
        // XAML can raise SelectionChanged/Checked while InitializeComponent
        // is materialising controls. Suppress handlers until every named
        // element exists and the persisted session state has been restored.
        _initializing = true;
        InitializeComponent();
        Closed += MainWindow_Closed;
        try
        {
            _recordingEnabled = !string.Equals(_repository.GetSetting("recording_enabled"), "0", StringComparison.Ordinal);
            RecordingEnabledCheckBox.IsChecked = _recordingEnabled;
            StatusText.Text = _recordingEnabled ? "本機錄音已啟用。" : "本機錄音已停用。";
            _lastSource = _repository.GetLatestFinalizedSource();
            if (_lastSource?.SessionId is not null)
            {
                _session = _repository.GetSession(_lastSource.SessionId);
                if (_session is not null)
                {
                    SetSelectedPrivacyMode(_session.PrivacyMode);
                    ConsentCheckBox.IsChecked = _repository.HasGrantedConsent(_session.SessionId, ConsentScope.LocalCapture);
                    CloudConsentCheckBox.IsChecked = !CloudNotPermittedException.IsBlocked(_session.PrivacyMode)
                        && _repository.HasGrantedConsent(_session.SessionId, ConsentScope.CloudTranscription);
                    RealtimeConsentCheckBox.IsChecked = !CloudNotPermittedException.IsBlocked(_session.PrivacyMode)
                        && _repository.HasGrantedConsent(_session.SessionId, ConsentScope.LiveCloudConversation);
                }
            }
            _latestSpeechAsset = _repository.GetLatestDerivedSpeechAsset();
            RefreshClarificationRevision();
            _locked = IsApplicationLockConfigured();
            ApplyLockState();
            PlaySpeechButton.IsEnabled = _latestSpeechAsset is not null && _speechPlayback is not null;
            UpdateRecordControl();
            if (_recoverableAudioCount > 0)
                StatusText.Text = $"有 {_recoverableAudioCount} 段未完成錄音，已保留待處理 · Local archive";
        }
        finally
        {
            _initializing = false;
        }
        StartRetryWorkerIfAvailable();
    }

    private void ConsentChanged(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        UpdateRecordControl();
        if (_session is not null)
            _repository.AddConsent(_session.SessionId, ConsentScope.LocalCapture, _session.PrivacyMode, ConsentCheckBox.IsChecked == true, "privacy-1");
    }

    private void RecordingEnabledChanged(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        _recordingEnabled = RecordingEnabledCheckBox.IsChecked == true;
        _repository.SetSetting("recording_enabled", _recordingEnabled ? "1" : "0");
        UpdateRecordControl();
        StatusText.Text = _recordingEnabled ? "本機錄音已啟用。" : "本機錄音已停用。";
    }

    private void CloudConsentChanged(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        if (_session is not null && !CloudNotPermittedException.IsBlocked(_session.PrivacyMode))
            _repository.AddConsent(_session.SessionId, ConsentScope.CloudTranscription, _session.PrivacyMode, CloudConsentCheckBox.IsChecked == true, "privacy-1");
        if (CloudConsentCheckBox.IsChecked != true)
        {
            _currentInfoCancellation?.Cancel();
            CurrentInfoResultsText.Text = "雲端同意已撤回；目前資訊結果已清除。";
        }
        UpdateRecordControl();
    }

    private void RealtimeConsentChanged(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        if (_session is not null && !CloudNotPermittedException.IsBlocked(_session.PrivacyMode))
            _repository.AddConsent(_session.SessionId, ConsentScope.LiveCloudConversation, _session.PrivacyMode, RealtimeConsentCheckBox.IsChecked == true, "privacy-1");
        UpdateRecordControl();
    }

    private void ConfigureApplicationLockButton_Click(object sender, RoutedEventArgs e)
    {
        _ = ConfigureApplicationLockAsync();
    }

    private async void LockNowButton_Click(object sender, RoutedEventArgs e)
    {
        if (_applicationLock is null || !_applicationLock.IsConfigured || _capture?.State == AudioCaptureState.Capturing || _processing)
            return;
        _locked = true;
        ApplyLockState();
        await StopRetryWorkerAsync();
    }

    private async void UnlockButton_Click(object sender, RoutedEventArgs e)
    {
        if (_applicationLock is null || !_locked) return;
        try
        {
            if (!_applicationLock.Verify(AppLockPasswordBox.Password))
            {
                AppLockStatusText.Text = "密碼不正確。";
                AppLockPasswordBox.Password = string.Empty;
                return;
            }

            AppLockPasswordBox.Password = string.Empty;
            AppLockStatusText.Text = string.Empty;
            _locked = false;
            ApplyLockState();
            StartRetryWorkerIfAvailable();
            StatusText.Text = "已解鎖 MEMENTO。";
        }
        catch (Exception)
        {
            AppLockStatusText.Text = "未能讀取應用程式鎖，請檢查 Windows Credential Manager。";
        }
        await Task.CompletedTask;
    }

    private async Task ConfigureApplicationLockAsync()
    {
        if (_applicationLock is null || _locked || _capture?.State == AudioCaptureState.Capturing || _processing)
            return;

        bool configured;
        try
        {
            configured = _applicationLock.IsConfigured;
        }
        catch (Exception)
        {
            StatusText.Text = "未能讀取應用程式鎖；請使用 recovery helper 清理損壞嘅 Windows credential。";
            _locked = true;
            ApplyLockState();
            return;
        }

        if (configured)
        {
            var currentPassword = new PasswordBox { Header = "目前應用程式鎖密碼", MinWidth = 300 };
            var disableDialog = new ContentDialog
            {
                XamlRoot = RootGrid.XamlRoot,
                Title = "停用應用程式鎖？",
                Content = currentPassword,
                PrimaryButtonText = "停用",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Close
            };
            if (await disableDialog.ShowAsync() != ContentDialogResult.Primary) return;
            try
            {
                if (!_applicationLock.Verify(currentPassword.Password))
                {
                    StatusText.Text = "目前應用程式鎖密碼不正確。";
                    return;
                }
                _applicationLock.Clear();
                StatusText.Text = "已停用應用程式鎖。";
                ApplyLockState();
            }
            catch (Exception)
            {
                StatusText.Text = "未能停用應用程式鎖。";
            }
            return;
        }

        var newPassword = new PasswordBox { Header = "新密碼（至少六個字元）", MinWidth = 300 };
        var confirmPassword = new PasswordBox { Header = "再次輸入新密碼", MinWidth = 300 };
        var content = new StackPanel { Spacing = 10 };
        content.Children.Add(new TextBlock { Text = "密碼會由目前 Windows 使用者嘅 Credential Manager 保護；遺失密碼後只能由管理員清除該 Windows credential。", TextWrapping = TextWrapping.Wrap });
        content.Children.Add(newPassword);
        content.Children.Add(confirmPassword);
        var enableDialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = "設定應用程式鎖",
            Content = content,
            PrimaryButtonText = "啟用",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close
        };
        if (await enableDialog.ShowAsync() != ContentDialogResult.Primary) return;
        if (!string.Equals(newPassword.Password, confirmPassword.Password, StringComparison.Ordinal))
        {
            StatusText.Text = "兩次密碼不一致。";
            return;
        }
        try
        {
            _applicationLock.Configure(newPassword.Password);
            StatusText.Text = "已啟用應用程式鎖。你可以按立即鎖定測試。";
            ApplyLockState();
        }
        catch (ArgumentException error)
        {
            StatusText.Text = error.Message;
        }
        catch (Exception)
        {
            StatusText.Text = "未能啟用應用程式鎖。";
        }
    }

    private void PrivacyModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing) return;
        var privacyMode = GetSelectedPrivacyMode();
        if (CloudNotPermittedException.IsBlocked(privacyMode))
        {
            _initializing = true;
            try
            {
                CloudConsentCheckBox.IsChecked = false;
                RealtimeConsentCheckBox.IsChecked = false;
            }
            finally { _initializing = false; }
            _currentInfoCancellation?.Cancel();
            CurrentInfoResultsText.Text = "本次對話只保留本機；目前資訊結果已清除。";
            CloudConsentCheckBox.IsEnabled = false;
            RealtimeConsentCheckBox.IsEnabled = false;
            StatusText.Text = privacyMode == PrivacyMode.PrivateConversation
                ? "已選擇私密對話：錄音只會保留喺本機。"
                : "已選擇只本機保存：錄音只會保留喺本機。";
        }
        else
        {
            CloudConsentCheckBox.IsEnabled = true;
            RealtimeConsentCheckBox.IsEnabled = true;
            StatusText.Text = "已選擇一般模式：完成錄音後可使用雲端功能。";
        }
        UpdateRecordControl();
    }

    private async void RecordButton_Click(object sender, RoutedEventArgs e)
    {
        if (_capture?.State != AudioCaptureState.Capturing && !_recordingEnabled)
        {
            StatusText.Text = "本機錄音已停用。";
            UpdateRecordControl();
            return;
        }

        if (_capture?.State != AudioCaptureState.Capturing && ConsentCheckBox.IsChecked != true)
        {
            StatusText.Text = "請先同意本機錄音。";
            UpdateRecordControl();
            return;
        }

        if (_capture?.State == AudioCaptureState.Capturing)
        {
            var streaming = _activeRealtimeStreaming;
            _activeRealtimeStreaming = null;
            try
            {
                _lastSource = _capture.Stop();
                if (_turn is not null)
                    _turn = _repository.EndTurn(_turn, _lastSource.FinalizedAt ?? DateTimeOffset.UtcNow);
                if (_session is not null)
                    _session = _repository.EndSession(_session);
                StatusText.Text = "已儲存本機錄音 · Local archive";
                if (streaming is not null)
                {
                    _processing = true;
                    UpdateRecordControl();
                    try
                    {
                        var result = await streaming.CompleteAsync();
                        _latestSpeechAsset = result.OutputSpeechAsset ?? _repository.GetLatestDerivedSpeechAsset();
                        StatusText.Text = result.OutputSpeechAsset is null
                            ? "已儲存本機錄音；Realtime 已完成文字回覆。"
                            : "已儲存本機錄音及 Realtime 語音回覆。";
                    }
                    catch (Exception)
                    {
                        var queuedFallback = QueueRealtimeFallback();
                        StatusText.Text = queuedFallback
                            ? "已儲存本機錄音；Realtime 未能完成，已安排稍後轉錄重試。"
                            : "已儲存本機錄音；Realtime 未能完成，錄音仍然保留。";
                    }
                    finally
                    {
                        _processing = false;
                    }
                }
            }
            catch (Exception)
            {
                StatusText.Text = "錄音未能完成，請檢查咪高風或 Windows 權限。";
                if (_session is not null && _session.EndedAt is null)
                {
                    try { _session = _repository.EndSession(_session); } catch { }
                }
                EndActiveTurnSafely();
                // Do not leave a failed new session paired with the previous
                // Source; that mismatch could expose a confusing retry action.
                _session = null;
                _lastSource = null;
                _pendingClarificationRevision = null;
                ClarificationPanel.Visibility = Visibility.Collapsed;
            }
            finally
            {
                if (streaming is not null)
                {
                    try { await streaming.DisposeAsync(); } catch { }
                }
                RecordButton.Content = "開始錄音";
                ConsentCheckBox.IsEnabled = true;
                CloudConsentCheckBox.IsEnabled = true;
                RealtimeConsentCheckBox.IsEnabled = true;
                RecordingEnabledCheckBox.IsEnabled = true;
                PrivacyModeBox.IsEnabled = true;
                UpdateRecordControl();
            }

            return;
        }

        try
        {
            var privacyMode = GetSelectedPrivacyMode();
            var startedAt = DateTimeOffset.UtcNow;
            _currentInfoCancellation?.Cancel();
            CurrentInfoResultsText.Text = string.Empty;
            _pendingClarificationRevision = null;
            ClarificationPanel.Visibility = Visibility.Collapsed;
            _session = _repository.AddSession(startedAt, privacyMode);
            _turn = _repository.AddTurn(_session.SessionId, _repository.GetNextTurnSequence(_session.SessionId), "participant", startedAt);
            _repository.AddConsent(_session.SessionId, ConsentScope.LocalCapture, privacyMode, true, "privacy-1");
            var cloudConsentGranted = ConsentPolicy.CloudConsentGranted(privacyMode, CloudConsentCheckBox.IsChecked == true);
            _repository.AddConsent(_session.SessionId, ConsentScope.CloudTranscription, privacyMode, cloudConsentGranted, "privacy-1");
            var realtimeConsentGranted = ConsentPolicy.CloudConsentGranted(privacyMode, RealtimeConsentCheckBox.IsChecked == true);
            _repository.AddConsent(_session.SessionId, ConsentScope.LiveCloudConversation, privacyMode, realtimeConsentGranted, "privacy-1");
            _capture = new AudioCaptureController(_repository, _audioRoot);
            _capture.CaptureFailed += CaptureFailed;
            var realtimeStreamingReady = false;
            if (realtimeConsentGranted && _realtimeStreaming is not null)
            {
                try
                {
                        _activeRealtimeStreaming = await _realtimeStreaming.StartAsync(
                        new RealtimeStreamingRequest(_session.SessionId, _turn.TurnId, privacyMode, true, startedAt),
                        _capture);
                    realtimeStreamingReady = true;
                }
                catch (Exception)
                {
                    _activeRealtimeStreaming = null;
                    StatusText.Text = "Realtime 未能連線；會繼續保留本機錄音。";
                }
            }
            _capture.Start(_session.SessionId, _turn.TurnId, ConsentCheckBox.IsChecked == true, format => new WaveInAudioInput(format), startedAt);
            StatusText.Text = CloudNotPermittedException.IsBlocked(privacyMode)
                ? $"Listening… 本機錄音中（{PrivacyModeLabel(privacyMode)}）"
                : realtimeStreamingReady
                    ? "Listening… 本機錄音中（Realtime 語音串流已啟動）"
                    : realtimeConsentGranted && _realtimeStreaming is not null
                        ? "Listening… 本機錄音中（Realtime 未連線，但本機錄音仍然保留）"
                : cloudConsentGranted
                    ? "Listening… 本機錄音中（已同意完成後雲端處理）"
                    : "Listening… 本機錄音中（未同意雲端處理）";
            RecordButton.Content = "停止錄音";
            ConsentCheckBox.IsEnabled = false;
            CloudConsentCheckBox.IsEnabled = false;
            RealtimeConsentCheckBox.IsEnabled = false;
            RecordingEnabledCheckBox.IsEnabled = false;
            PrivacyModeBox.IsEnabled = false;
        }
        catch (ConsentRequiredException)
        {
            StatusText.Text = "請先同意本機錄音。";
        }
        catch (Exception)
        {
            if (_activeRealtimeStreaming is not null)
            {
                try { await _activeRealtimeStreaming.DisposeAsync(); } catch { }
                _activeRealtimeStreaming = null;
            }
            StatusText.Text = "無法使用咪高風，請檢查 Windows 權限或接駁。";
            if (_session is not null && _session.EndedAt is null)
            {
                try { _session = _repository.EndSession(_session); } catch { }
            }
            EndActiveTurnSafely();
            _session = null;
            _lastSource = null;
            _pendingClarificationRevision = null;
            ClarificationPanel.Visibility = Visibility.Collapsed;
            RecordButton.Content = "開始錄音";
            ConsentCheckBox.IsEnabled = true;
            CloudConsentCheckBox.IsEnabled = true;
            RealtimeConsentCheckBox.IsEnabled = true;
            RecordingEnabledCheckBox.IsEnabled = true;
            PrivacyModeBox.IsEnabled = true;
            UpdateRecordControl();
        }
    }

    private async void ProcessButton_Click(object sender, RoutedEventArgs e)
    {
        if (_voiceConversation is null || _lastSource?.FilePath is null || _session is null || _session.EndedAt is null || _capture?.State == AudioCaptureState.Capturing || CloudNotPermittedException.IsBlocked(_session.PrivacyMode) || CloudConsentCheckBox.IsChecked != true || _processing)
            return;

        _processing = true;
        UpdateRecordControl();
        StatusText.Text = "正在轉錄及準備回覆…";
        try
        {
            var request = new ConversationRequest(_session.SessionId, _lastSource.TurnId, _lastSource.FilePath, _session.PrivacyMode, true, DateTimeOffset.UtcNow, SourceId: _lastSource.SourceId);
            var result = await _voiceConversation.ExecuteAsync(request);
            _latestSpeechAsset = result.SpeechAsset ?? _repository.GetLatestDerivedSpeechAsset();
            RefreshClarificationRevision();
            if (result.Conversation.Response is null)
            {
                StartRetryWorkerIfAvailable();
                StatusText.Text = "錄音已保留；雲端暫時未能回覆，已安排稍後重試。";
            }
            else
            {
                StatusText.Text = "已完成轉錄及回覆。";
            }
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "雲端處理已取消；本機錄音仍然保留。";
        }
        catch (Exception)
        {
            StartRetryWorkerIfAvailable();
            StatusText.Text = "雲端處理未能完成；本機錄音仍然保留。";
        }
        finally
        {
            _processing = false;
            UpdateRecordControl();
        }
    }

    private async void RealtimeProcessButton_Click(object sender, RoutedEventArgs e)
    {
        if (_realtimeConversation is null || _lastSource?.FilePath is null || _session is null || _session.EndedAt is null || _capture?.State == AudioCaptureState.Capturing || CloudNotPermittedException.IsBlocked(_session.PrivacyMode) || !HasGrantedRealtimeConsent() || _processing)
            return;

        _processing = true;
        UpdateRecordControl();
        StatusText.Text = "正在使用 Realtime 語音回覆…";
        try
        {
            var request = new RealtimeConversationRequest(_session.SessionId, _lastSource.TurnId, _lastSource.FilePath, _session.PrivacyMode, true, DateTimeOffset.UtcNow, _lastSource.SourceId);
            var result = await _realtimeConversation.ExecuteAsync(request);
            _latestSpeechAsset = result.OutputSpeechAsset ?? _repository.GetLatestDerivedSpeechAsset();
            RefreshClarificationRevision();
            StatusText.Text = result.OutputSpeechAsset is null
                ? "Realtime 已完成文字回覆；沒有可播放嘅語音輸出。"
                : "Realtime 已完成語音回覆，可以播放最近回覆。";
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Realtime 處理已取消；本機錄音仍然保留。";
        }
        catch (Exception)
        {
            StatusText.Text = "Realtime 處理未能完成；本機錄音仍然保留。請檢查 credential 或網絡。";
        }
        finally
        {
            _processing = false;
            UpdateRecordControl();
        }
    }

    private bool QueueRealtimeFallback()
    {
        if (_session is null || _lastSource is null || !HasGrantedCloudConsent()) return false;
        try
        {
            if (_repository.ListTranscriptRevisions(_lastSource.SourceId).Count > 0)
                return false;
            if (new ConversationSessionWriter(_repository).QueueTranscriptionIfNeeded(_session, _turn, _lastSource) is null)
                return false;
            StartRetryWorkerIfAvailable();
            return true;
        }
        catch
        {
            return false;
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

    private async void PlaySourceButton_Click(object sender, RoutedEventArgs e)
    {
        if (_sourceAudioPlayback is null || _lastSource is null || _processing || _capture?.State == AudioCaptureState.Capturing)
            return;

        _sourcePlaybackCancellation?.Dispose();
        _sourcePlaybackCancellation = new CancellationTokenSource();
        PlaySourceButton.IsEnabled = false;
        StatusText.Text = "播放最近本機錄音中…";
        try
        {
            await _sourceAudioPlayback.PlayAsync(_lastSource, _sourcePlaybackCancellation.Token);
            StatusText.Text = "已播放最近本機錄音。";
        }
        catch (OperationCanceledException) when (_sourcePlaybackCancellation.IsCancellationRequested)
        {
            StatusText.Text = "已停止播放本機錄音。";
        }
        catch (Exception)
        {
            StatusText.Text = "未能播放本機錄音；檔案可能已損壞或輸出裝置不可用。";
        }
        finally
        {
            _sourcePlaybackCancellation.Dispose();
            _sourcePlaybackCancellation = null;
            UpdateRecordControl();
        }
    }

    private void SpeakerConfirmedButton_Click(object sender, RoutedEventArgs e)
        => RecordClarificationOutcome(ClarificationOutcome.SpeakerConfirmed);

    private void TwoPossibilitiesButton_Click(object sender, RoutedEventArgs e)
        => RecordClarificationOutcome(ClarificationOutcome.TwoPossibilities);

    private void DoesNotRememberButton_Click(object sender, RoutedEventArgs e)
        => RecordClarificationOutcome(ClarificationOutcome.ParticipantDoesNotRemember);

    private void ParticipantRefusedButton_Click(object sender, RoutedEventArgs e)
        => RecordClarificationOutcome(ClarificationOutcome.ParticipantRefused);

    private void RecordClarificationOutcome(ClarificationOutcome outcome)
    {
        if (_pendingClarificationRevision is null || _session is null || _session.EndedAt is null)
        {
            ClarificationStatusText.Text = "目前沒有可以澄清嘅轉錄。";
            return;
        }

        var question = ClarificationQuestionBox.Text.Trim();
        if (question.Length == 0)
        {
            ClarificationStatusText.Text = "請先輸入想確認嘅問題。";
            return;
        }

        var correction = ClarificationCorrectionBox.Text.Trim();
        var requiresCorrection = outcome is ClarificationOutcome.SpeakerConfirmed or ClarificationOutcome.CorrectedPreviousCorrection;
        if (requiresCorrection && correction.Length == 0)
        {
            ClarificationStatusText.Text = "講者確認時，請輸入更正後完整文字；原始轉錄會保留。";
            return;
        }
        if (!requiresCorrection && correction.Length > 0)
        {
            ClarificationStatusText.Text = "如果要記錄更正，請按「講者確認／更正」；不確定答案不會被當成更正。";
            return;
        }

        var entityKind = GetSelectedClarificationEntityKind();
        var response = string.IsNullOrWhiteSpace(ClarificationResponseBox.Text) ? null : ClarificationResponseBox.Text.Trim();
        var canonical = string.IsNullOrWhiteSpace(ClarificationCanonicalBox.Text) ? null : ClarificationCanonicalBox.Text.Trim();
        try
        {
            var chain = new ClarificationProtocol(_repository).RecordOutcome(
                _session,
                _pendingClarificationRevision,
                entityKind,
                question,
                response,
                outcome,
                requiresCorrection ? correction : null,
                requiresCorrection ? canonical : null);

            _pendingClarificationRevision = null;
            ClarificationPanel.Visibility = Visibility.Collapsed;
            ClarificationStatusText.Text = chain.CorrectedRevision is null
                ? "已記錄參與者答案；原始轉錄保持不變。"
                : "已記錄講者更正；原始及更正後轉錄都已保留。";
            StatusText.Text = chain.CorrectedRevision is null
                ? "已記錄澄清結果。"
                : "已記錄講者更正；可以稍後審閱記憶候選。";
            UpdateRecordControl();
        }
        catch (Exception)
        {
            ClarificationStatusText.Text = "未能記錄澄清結果；原始轉錄仍然保留。";
        }
    }

    private ClarificationEntityKind GetSelectedClarificationEntityKind()
    {
        var tag = (ClarificationEntityKindBox.SelectedItem as ComboBoxItem)?.Tag?.ToString();
        return Enum.TryParse<ClarificationEntityKind>(tag, out var entityKind)
            ? entityKind
            : ClarificationEntityKind.None;
    }

    private void RefreshClarificationRevision()
    {
        _pendingClarificationRevision = null;
        ClarificationPanel.Visibility = Visibility.Collapsed;
        if (_lastSource is null || _session is null || _session.EndedAt is null || string.Equals(_lastSource.RecoveryStatus, "withdrawn", StringComparison.OrdinalIgnoreCase))
            return;

        try
        {
            _pendingClarificationRevision = _repository.ListTranscriptRevisions(_lastSource.SourceId)
                .OrderByDescending(revision => revision.RevisionNumber)
                .ThenByDescending(revision => revision.CreatedAt)
                .FirstOrDefault();
            if (_pendingClarificationRevision is null) return;
            ClarificationTranscriptText.Text = $"最近轉錄：{_pendingClarificationRevision.Text}";
            ClarificationQuestionBox.Text = string.Empty;
            ClarificationResponseBox.Text = string.Empty;
            ClarificationCorrectionBox.Text = string.Empty;
            ClarificationCanonicalBox.Text = string.Empty;
            ClarificationStatusText.Text = "原始轉錄會保留；只有講者確認／更正才會建立新 revision。";
            ClarificationPanel.Visibility = Visibility.Visible;
        }
        catch
        {
            _pendingClarificationRevision = null;
        }
    }

    private void SearchBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != Windows.System.VirtualKey.Enter) return;
        SearchArchiveButton_Click(sender, new RoutedEventArgs());
        e.Handled = true;
    }

    private async void SearchArchiveButton_Click(object sender, RoutedEventArgs e)
    {
        if (_processing) return;
        var query = SearchBox.Text.Trim();
        if (query.Length == 0)
        {
            SearchResultsText.Text = "請輸入要搜尋嘅字詞。";
            return;
        }

        _processing = true;
        UpdateRecordControl();
        try
        {
            var hits = await RunArchiveWorkAsync(() => new ArchiveSearchService(_repository.Archive).Search(query, 20));
            SearchResultsText.Text = hits.Count == 0
                ? "未找到符合嘅本機記錄。"
                : string.Join(Environment.NewLine, hits.Select(hit => $"[{hit.RecordType}] {hit.Content}"));
            StatusText.Text = $"本機搜尋完成：{hits.Count} 項。";
        }
        catch (Exception)
        {
            SearchResultsText.Text = "未能完成本機搜尋。";
        }
        finally
        {
            _processing = false;
            UpdateRecordControl();
        }
    }

    private void CurrentInfoBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != Windows.System.VirtualKey.Enter) return;
        SearchCurrentInformationButton_Click(sender, new RoutedEventArgs());
        e.Handled = true;
    }

    private async void SearchCurrentInformationButton_Click(object sender, RoutedEventArgs e)
    {
        var query = CurrentInfoBox.Text.Trim();
        if (query.Length == 0)
        {
            CurrentInfoResultsText.Text = "請輸入要查詢嘅天氣、交通或其他目前資訊。";
            return;
        }
        if (_currentInformation is null || _processing || _sourcePlaybackCancellation is not null) return;
        if (CloudNotPermittedException.IsBlocked(GetSelectedPrivacyMode()) || (_session is not null && CloudNotPermittedException.IsBlocked(_session.PrivacyMode)))
        {
            var mode = CloudNotPermittedException.IsBlocked(GetSelectedPrivacyMode()) ? GetSelectedPrivacyMode() : _session!.PrivacyMode;
            CurrentInfoResultsText.Text = $"本次對話設定為{PrivacyModeLabel(mode)}，未能使用雲端目前資訊查詢。";
            return;
        }
        if (!HasGrantedCloudConsent())
        {
            CurrentInfoResultsText.Text = "請先同意使用雲端目前資訊查詢。";
            return;
        }

        var cancellation = new CancellationTokenSource();
        _currentInfoCancellation?.Cancel();
        _currentInfoCancellation?.Dispose();
        _currentInfoCancellation = cancellation;
        SearchCurrentInformationButton.IsEnabled = false;
        CurrentInfoResultsText.Text = "查詢中…";
        try
        {
            var privacyMode = _session?.PrivacyMode ?? GetSelectedPrivacyMode();
            var result = await _currentInformation.SearchAsync(query, privacyMode, HasGrantedCloudConsent(), cancellation.Token);
            // A cancelled request may still complete if a provider ignores
            // cancellation. Only the currently-owned request may update the
            // visible result, so an old external answer cannot cross into a
            // new session or consent state.
            if (!ReferenceEquals(_currentInfoCancellation, cancellation))
                return;
            if (!HasGrantedCloudConsent())
            {
                CurrentInfoResultsText.Text = "雲端同意已撤回，未顯示目前資訊結果。";
                return;
            }
            var resultHeader = $"查詢時間：{result.RetrievedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss zzz}\n" +
                               $"來源：{result.Provider}\n" +
                               (result.IsUntrustedExternalInformation
                                   ? "外部資料只供本次回答參考，不會加入本機記憶。"
                                   : "本次結果未標記為外部不可信資料。\n");
            var sourceText = result.Sources.Count == 0
                ? "未收到 allowlisted source。"
                : string.Join(Environment.NewLine + Environment.NewLine, result.Sources.Select(source => $"{source.Title}\n{source.Snippet}\n{source.Url}"));
            CurrentInfoResultsText.Text = resultHeader + Environment.NewLine + Environment.NewLine + sourceText;
            StatusText.Text = "目前資訊已收到；外部資料未加入本機記憶。";
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            CurrentInfoResultsText.Text = "已取消目前資訊查詢。";
        }
        catch (Exception)
        {
            CurrentInfoResultsText.Text = "未能完成目前資訊查詢；請檢查雲端 credential 或網絡。";
        }
        finally
        {
            if (ReferenceEquals(_currentInfoCancellation, cancellation))
            {
                _currentInfoCancellation.Dispose();
                _currentInfoCancellation = null;
            }
            UpdateRecordControl();
        }
    }

    private async void AdminReviewButton_Click(object sender, RoutedEventArgs e)
    {
        if (_adminReview is null || string.IsNullOrWhiteSpace(_adminActorId) || _processing) return;
        _processing = true;
        UpdateRecordControl();
        IReadOnlyList<MemoryClaim> candidates;
        try
        {
            candidates = await RunArchiveWorkAsync(() => _adminReview.ListCandidates(_adminActorId));
        }
        catch (UnauthorizedAccessException)
        {
            try { await ShowAdminMessageAsync("未獲授權", "只有獲授權嘅 Windows 管理員可以進入家庭管理審閱。", "知道了"); }
            finally { _processing = false; UpdateRecordControl(); }
            return;
        }
        catch (Exception)
        {
            StatusText.Text = "未能讀取候選記憶。";
            _processing = false;
            UpdateRecordControl();
            return;
        }

        if (candidates.Count == 0)
        {
            try { await ShowAdminMessageAsync("未有候選記憶", "目前沒有需要家庭管理審閱嘅候選記憶。", "關閉"); }
            finally { _processing = false; UpdateRecordControl(); }
            return;
        }

        var claimSelector = new ComboBox
        {
            Header = "選擇候選記憶",
            ItemsSource = candidates,
            DisplayMemberPath = nameof(MemoryClaim.Statement),
            SelectedIndex = 0,
            MinWidth = 420
        };
        var evidenceSummary = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            FontSize = 16
        };
        var reviewContentPanel = new StackPanel { Spacing = 12 };
        reviewContentPanel.Children.Add(new TextBlock
        {
            Text = $"共有 {candidates.Count} 個候選記憶；請逐一查看原始 Evidence，再決定是否支持或拒絕。",
            TextWrapping = TextWrapping.Wrap
        });
        reviewContentPanel.Children.Add(claimSelector);
        reviewContentPanel.Children.Add(evidenceSummary);

        void RefreshReviewEvidence()
        {
            if (claimSelector.SelectedItem is not MemoryClaim selectedClaim)
            {
                evidenceSummary.Text = "未選擇候選記憶。";
                return;
            }

            try
            {
                var claimEvidence = _repository.ListEvidenceForClaim(selectedClaim.MemoryClaimId);
                evidenceSummary.Text = claimEvidence.Count == 0
                    ? $"狀態：{selectedClaim.Status}\n未有可顯示嘅 supporting Evidence。"
                    : $"狀態：{selectedClaim.Status}\n\n原始 Evidence：\n" + string.Join(Environment.NewLine + Environment.NewLine, claimEvidence.Select(item =>
                        $"[{item.Relationship}] {item.Evidence.Statement}" +
                        $"\n原始表達：{item.Evidence.OriginalExpression}" +
                        $"\n確定程度：{item.Evidence.ParticipantCertainty}；講者確認：{(item.Evidence.SpeakerConfirmed ? "是" : "未有")}" +
                        $"\nSource：{item.Evidence.SourceId}" +
                        (item.Evidence.AudioStartMs is null ? string.Empty : $"\nAudio span：{item.Evidence.AudioStartMs}–{item.Evidence.AudioEndMs} ms")));
            }
            catch (Exception)
            {
                evidenceSummary.Text = "未能讀取候選記憶嘅原始證據。";
            }
        }

        claimSelector.SelectionChanged += (_, _) => RefreshReviewEvidence();
        RefreshReviewEvidence();
        var dialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = "家庭管理審閱",
            Content = new ScrollViewer { Content = reviewContentPanel, MaxHeight = 520 },
            PrimaryButtonText = "支持候選記憶",
            SecondaryButtonText = "拒絕候選記憶",
            CloseButtonText = "稍後處理",
            DefaultButton = ContentDialogButton.Close
        };
        try
        {
            var result = await dialog.ShowAsync();
            if (claimSelector.SelectedItem is not MemoryClaim claim)
            {
                StatusText.Text = "未選擇候選記憶。";
                return;
            }

            if (result == ContentDialogResult.Primary)
            {
                await RunArchiveWorkAsync(() => _adminReview.AnnotateClaim(_adminActorId, claim, "family_assessment", "家庭管理審閱：支持候選記憶。", "supported"));
                StatusText.Text = "已記錄家庭支持；仍保留原始證據鏈。";
            }
            else if (result == ContentDialogResult.Secondary)
            {
                await RunArchiveWorkAsync(() => _adminReview.AnnotateClaim(_adminActorId, claim, "admin_annotation", "家庭管理審閱：拒絕候選記憶。", "rejected"));
                StatusText.Text = "已拒絕候選記憶；原始證據仍然保留。";
            }
        }
        catch (Exception)
        {
            StatusText.Text = "未能儲存家庭管理審閱。";
        }
        finally
        {
            _processing = false;
            UpdateRecordControl();
        }
    }

    private async void DeleteLatestSourceButton_Click(object sender, RoutedEventArgs e)
    {
        if (_deletion is null || string.IsNullOrWhiteSpace(_adminActorId) || _lastSource is null || _capture?.State == AudioCaptureState.Capturing || _processing)
            return;

        var dialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = "刪除最近本機錄音？",
            Content = new TextBlock
            {
                Text = "這會刪除最近錄音及其 transcript、候選記憶、derived audio 和相關 provider metadata。只保留最小刪除 audit tombstone，動作不能復原。",
                TextWrapping = TextWrapping.Wrap
            },
            PrimaryButtonText = "確認刪除",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        _processing = true;
        UpdateRecordControl();
        try
        {
            var sourceId = _lastSource.SourceId;
            var result = await RunArchiveWorkAsync(() => _deletion.DeleteSource(_adminActorId, sourceId, "participant requested deletion"));
            _lastSource = null;
            _session = null;
            _pendingClarificationRevision = null;
            ClarificationPanel.Visibility = Visibility.Collapsed;
            _latestSpeechAsset = _repository.GetLatestDerivedSpeechAsset();
            StatusText.Text = result.MediaRemoved
                ? "最近錄音及相關資料已刪除；已保留最小 audit tombstone。"
                : "資料已刪除，但有 media 檔案未能移除，請交由管理員檢查。";
            UpdateRecordControl();
        }
        catch (Exception)
        {
            StatusText.Text = "未能刪除最近錄音；原有資料仍然保留。";
        }
        finally
        {
            _processing = false;
            UpdateRecordControl();
        }
    }

    private async void WithdrawLatestSourceButton_Click(object sender, RoutedEventArgs e)
    {
        if (_withdrawal is null || string.IsNullOrWhiteSpace(_adminActorId) || _lastSource is null || _capture?.State == AudioCaptureState.Capturing || _processing || string.Equals(_lastSource.RecoveryStatus, "withdrawn", StringComparison.OrdinalIgnoreCase))
            return;

        var dialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = "停止日後雲端處理？",
            Content = new TextBlock
            {
                Text = "這會保留歷史錄音及資料作本機管理，但由此來源衍生嘅資料會停止日後雲端轉錄、記憶抽取、搜尋及一般匯出。原始錄音不會刪除。",
                TextWrapping = TextWrapping.Wrap
            },
            PrimaryButtonText = "確認停止",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        _processing = true;
        UpdateRecordControl();
        try
        {
            var sourceId = _lastSource.SourceId;
            var result = await RunArchiveWorkAsync(() => _withdrawal.WithdrawSource(_adminActorId, sourceId, "participant requested future cloud processing withdrawal"));
            _lastSource = await RunArchiveWorkAsync(() => _repository.GetSource(result.SourceId));
            _pendingClarificationRevision = null;
            ClarificationPanel.Visibility = Visibility.Collapsed;
            StatusText.Text = "已停止此錄音日後雲端處理；歷史資料及原始錄音仍然保留。";
            UpdateRecordControl();
        }
        catch (Exception)
        {
            StatusText.Text = "未能停止日後雲端處理；原有資料仍然保留。";
        }
        finally
        {
            _processing = false;
            UpdateRecordControl();
        }
    }

    private async void HealthCheckButton_Click(object sender, RoutedEventArgs e)
    {
        if (_processing) return;
        _processing = true;
        UpdateRecordControl();
        try
        {
            var report = await RunArchiveWorkAsync(() => ArchiveHealthCheck.Run(_repository.Archive, _audioRoot));
            StatusText.Text = report.Findings.Count == 0
                ? $"健康檢查完成：SQLite {report.SchemaVersion}，未發現問題。"
                : $"健康檢查發現 {report.Findings.Count} 項：{string.Join("；", report.Findings)}";
        }
        catch (Exception)
        {
            StatusText.Text = "未能完成本機資料健康檢查。";
        }
        finally
        {
            _processing = false;
            UpdateRecordControl();
        }
    }

    private async void RecoverAudioButton_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureAdminForOperation()) return;
        if (_processing || _capture?.State == AudioCaptureState.Capturing)
        {
            StatusText.Text = "請先完成目前錄音或處理工作。";
            return;
        }

        RecoverableAudioAsset[] candidates;
        try
        {
            candidates = await RunArchiveWorkAsync(() => AudioRecoveryScanner.Scan(_audioRoot).Where(candidate => candidate.IsValidPcm).ToArray());
        }
        catch (Exception)
        {
            StatusText.Text = "未能檢查未完成錄音；原有暫存檔仍然保留。";
            return;
        }
        if (candidates.Length == 0)
        {
            StatusText.Text = "目前沒有可整理嘅未完成錄音。";
            return;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = "整理未完成錄音？",
            Content = new TextBlock
            {
                Text = $"會將 {candidates.Length} 段可讀取嘅暫存 WAV 修復成 archive Source；原始 transcript 不會自行新增或修改。",
                TextWrapping = TextWrapping.Wrap
            },
            PrimaryButtonText = "整理",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        _processing = true;
        UpdateRecordControl();
        try
        {
            var result = await RunArchiveWorkAsync(() =>
            {
                var recovered = 0;
                var skipped = 0;
                foreach (var candidate in candidates)
                {
                    if (!AudioRecoveryService.TryInferSessionId(candidate.TemporaryPath, out var sessionId))
                    {
                        skipped++;
                        continue;
                    }

                    try
                    {
                        _audioRecovery.Recover(candidate.TemporaryPath, sessionId);
                        recovered++;
                    }
                    catch
                    {
                        // Keep the marker for a later supervised review. Do not show
                        // paths or raw exception text in the participant-facing shell.
                        skipped++;
                    }
                }

                if (recovered > 0)
                    _adminReview!.RecordAdminOperation(_adminActorId!, "recover_audio");
                return (recovered, skipped);
            });
            StatusText.Text = result.skipped == 0
                ? $"已整理 {result.recovered} 段未完成錄音。"
                : $"已整理 {result.recovered} 段未完成錄音；{result.skipped} 段保留待管理員檢查。";
        }
        catch (Exception)
        {
            StatusText.Text = "未能整理未完成錄音；原有暫存檔仍然保留。";
        }
        finally
        {
            _processing = false;
            UpdateRecordControl();
        }
    }

    private async void RebuildSearchButton_Click(object sender, RoutedEventArgs e)
    {
        if (_processing) return;
        _processing = true;
        UpdateRecordControl();
        try
        {
            var count = await RunArchiveWorkAsync(() => new ArchiveSearchService(_repository.Archive).Rebuild());
            StatusText.Text = $"本機搜尋索引已修復：{count} 項。";
        }
        catch (Exception)
        {
            StatusText.Text = "未能修復本機搜尋索引；原有資料仍然保留。";
        }
        finally
        {
            _processing = false;
            UpdateRecordControl();
        }
    }

    private async void ExportButton_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureAdminForOperation()) return;
        if (_processing) return;
        _processing = true;
        UpdateRecordControl();
        StatusText.Text = "正在匯出本機資料…";
        try
        {
            var result = await RunArchiveWorkAsync(() =>
            {
                var export = ArchiveExporter.Export(_repository.Archive, Path.Combine(_dataRoot, "exports"), includeMedia: true);
                _adminReview!.RecordAdminOperation(_adminActorId!, "export");
                return export;
            });
            StatusText.Text = $"已匯出本機資料：{result.ExportDirectory}";
        }
        catch (Exception)
        {
            StatusText.Text = "未能匯出本機資料；原有資料仍然保留。";
        }
        finally
        {
            _processing = false;
            UpdateRecordControl();
        }
    }

    private async void ScopedExportButton_Click(object sender, RoutedEventArgs e)
    {
        if (_processing) return;
        if (!EnsureAdminForOperation()) return;
        IReadOnlyList<MemoryClaim> claims;
        try
        {
            claims = _adminReview!.ListReviewedClaims(_adminActorId!);
        }
        catch (UnauthorizedAccessException)
        {
            await ShowAdminMessageAsync("未獲授權", "只有獲授權嘅 Windows 管理員可以匯出精簡記憶。", "知道了");
            return;
        }

        if (claims.Count == 0)
        {
            StatusText.Text = "目前沒有可分享嘅已審閱記憶。";
            return;
        }

        var selector = new ListView
        {
            ItemsSource = claims,
            DisplayMemberPath = nameof(MemoryClaim.Statement),
            SelectionMode = ListViewSelectionMode.Multiple,
            MaxHeight = 260,
            MinWidth = 420
        };
        selector.SelectedItems.Add(claims[0]);
        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(selector);
        panel.Children.Add(new TextBlock
        {
            Text = "只會匯出 Claim、審閱狀態同遮蔽後 Evidence 關係；錄音、Source 路徑、transcript 內容、provider 資料同 annotation 內容都會留喺本機。",
            TextWrapping = TextWrapping.Wrap
        });
        var dialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = "匯出精簡記憶（遮蔽錄音）",
            Content = panel,
            PrimaryButtonText = "匯出",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        var selectedClaims = selector.SelectedItems.OfType<MemoryClaim>().ToArray();
        if (selectedClaims.Length == 0)
        {
            StatusText.Text = "未選擇要分享嘅記憶。";
            return;
        }

        if (_processing) return;
        _processing = true;
        UpdateRecordControl();
        try
        {
            StatusText.Text = "正在匯出精簡記憶…";
            var selectedClaimIds = selectedClaims.Select(claim => claim.MemoryClaimId).ToArray();
            var result = await RunArchiveWorkAsync(() =>
            {
                var export = ArchiveExporter.ExportRedacted(_repository.Archive, Path.Combine(_dataRoot, "exports"), selectedClaimIds);
                _adminReview.RecordAdminOperation(_adminActorId!, "export_redacted");
                return export;
            });
            StatusText.Text = $"已匯出 {result.ExportedClaimIds.Count} 項精簡記憶（錄音已遮蔽）：{result.ExportDirectory}";
        }
        catch (Exception)
        {
            StatusText.Text = "未能匯出精簡記憶；原有資料仍然保留。";
        }
        finally
        {
            _processing = false;
            UpdateRecordControl();
        }
    }

    private async void BackupButton_Click(object sender, RoutedEventArgs e)
    {
        if (_processing) return;
        if (!EnsureAdminForOperation()) return;
        var passwordBox = new PasswordBox { PlaceholderText = "輸入備份密碼", MinWidth = 280 };
        var dialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = "建立加密備份",
            Content = passwordBox,
            PrimaryButtonText = "建立備份",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary || string.IsNullOrWhiteSpace(passwordBox.Password))
        {
            StatusText.Text = "已取消加密備份。";
            return;
        }

        var temporaryRoot = Path.Combine(Path.GetTempPath(), "memento-backup-" + Guid.NewGuid().ToString("N"));
        var password = passwordBox.Password;
        if (_processing) return;
        _processing = true;
        UpdateRecordControl();
        StatusText.Text = "正在建立加密備份…";
        try
        {
            var destination = Path.Combine(_dataRoot, "backups", "memento-" + DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmssfff", System.Globalization.CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N") + ".memento");
            await RunArchiveWorkAsync(() =>
            {
                var export = ArchiveExporter.Export(_repository.Archive, temporaryRoot, includeMedia: true);
                ArchiveBackupProtector.EncryptDirectory(export.ExportDirectory, destination, password);
                _adminReview!.RecordAdminOperation(_adminActorId!, "encrypted_backup");
            });
            StatusText.Text = $"已建立加密備份：{destination}";
        }
        catch (Exception)
        {
            StatusText.Text = "未能建立加密備份；原有資料仍然保留。";
        }
        finally
        {
            passwordBox.Password = string.Empty;
            try
            {
                if (Directory.Exists(temporaryRoot))
                    Directory.Delete(temporaryRoot, recursive: true);
            }
            catch
            {
                // Cleanup failure must not escape an async UI event or replace
                // the backup result; the temporary path is outside the archive.
            }
            _processing = false;
            UpdateRecordControl();
        }
    }

    private async void RestoreButton_Click(object sender, RoutedEventArgs e)
    {
        if (_processing) return;
        if (!EnsureAdminForOperation()) return;
        var backupPath = new TextBox { PlaceholderText = "輸入 .memento 備份檔案路徑", MinWidth = 360 };
        var passwordBox = new PasswordBox { PlaceholderText = "輸入備份密碼", MinWidth = 360 };
        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(new TextBlock { Text = "備份檔案" });
        panel.Children.Add(backupPath);
        panel.Children.Add(new TextBlock { Text = "備份密碼" });
        panel.Children.Add(passwordBox);
        var dialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = "驗證及還原加密備份",
            Content = panel,
            PrimaryButtonText = "還原到新資料夾",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            StatusText.Text = "已取消備份還原。";
            return;
        }

        if (string.IsNullOrWhiteSpace(backupPath.Text) || string.IsNullOrWhiteSpace(passwordBox.Password))
        {
            StatusText.Text = "請輸入備份檔案路徑及密碼。";
            return;
        }

        var restoreRoot = Path.Combine(_dataRoot, "restores", "memento-" + DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmssfff", System.Globalization.CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N"));
        var sourcePath = backupPath.Text.Trim();
        var password = passwordBox.Password;
        if (_processing) return;
        _processing = true;
        UpdateRecordControl();
        StatusText.Text = "正在驗證及還原加密備份…";
        try
        {
            var result = await RunArchiveWorkAsync(() =>
            {
                var report = ArchiveBackupProtector.DecryptDirectory(sourcePath, restoreRoot, password);
                _adminReview!.RecordAdminOperation(_adminActorId!, "restore_verification");
                return report;
            });
            StatusText.Text = result.IntegrityOk
                ? $"備份已還原並通過完整性驗證：{restoreRoot}"
                : $"備份已還原，但完整性驗證發現問題：{string.Join("；", result.Findings)}";
        }
        catch (Exception)
        {
            StatusText.Text = "未能還原備份；目前 archive 沒有被覆蓋。";
        }
        finally
        {
            passwordBox.Password = string.Empty;
            _processing = false;
            UpdateRecordControl();
        }
    }

    private async void RotateBackupButton_Click(object sender, RoutedEventArgs e)
    {
        if (_processing) return;
        if (!EnsureAdminForOperation()) return;

        var backupPath = new TextBox { PlaceholderText = "輸入現有 .memento 備份檔案路徑", MinWidth = 360 };
        var oldPassword = new PasswordBox { PlaceholderText = "輸入現有備份密碼", MinWidth = 360 };
        var newPassword = new PasswordBox { PlaceholderText = "輸入新備份密碼", MinWidth = 360 };
        var confirmPassword = new PasswordBox { PlaceholderText = "再次輸入新備份密碼", MinWidth = 360 };
        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(new TextBlock { Text = "現有備份檔案" });
        panel.Children.Add(backupPath);
        panel.Children.Add(new TextBlock { Text = "現有備份密碼" });
        panel.Children.Add(oldPassword);
        panel.Children.Add(new TextBlock { Text = "新備份密碼" });
        panel.Children.Add(newPassword);
        panel.Children.Add(confirmPassword);
        var dialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = "更新備份密碼",
            Content = panel,
            PrimaryButtonText = "建立新備份",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            StatusText.Text = "已取消更新備份密碼。";
            return;
        }

        if (string.IsNullOrWhiteSpace(backupPath.Text)
            || string.IsNullOrWhiteSpace(oldPassword.Password)
            || string.IsNullOrWhiteSpace(newPassword.Password))
        {
            StatusText.Text = "請輸入備份檔案路徑及所有密碼。";
            return;
        }

        if (!string.Equals(newPassword.Password, confirmPassword.Password, StringComparison.Ordinal))
        {
            StatusText.Text = "兩次新備份密碼不一致。";
            return;
        }

        var destination = Path.Combine(
            _dataRoot,
            "backups",
            "memento-rekey-" + DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmssfff", System.Globalization.CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N") + ".memento");
        var sourcePath = backupPath.Text.Trim();
        var previousPassword = oldPassword.Password;
        var replacementPassword = newPassword.Password;
        if (_processing) return;
        _processing = true;
        UpdateRecordControl();
        StatusText.Text = "正在更新備份密碼…";
        try
        {
            var report = await RunArchiveWorkAsync(() => ArchiveBackupProtector.ReencryptDirectory(
                sourcePath, destination, previousPassword, replacementPassword));
            if (!report.IntegrityOk)
            {
                if (File.Exists(destination)) File.Delete(destination);
                StatusText.Text = "現有備份完整性驗證失敗，未建立新備份。";
                return;
            }

            _adminReview!.RecordAdminOperation(_adminActorId!, "encrypted_backup_rekey");
            StatusText.Text = $"已建立更新密碼嘅加密備份：{destination}";
        }
        catch (Exception)
        {
            StatusText.Text = "未能更新備份密碼；原有備份仍然保留。";
            if (File.Exists(destination))
            {
                try { File.Delete(destination); } catch { }
            }
        }
        finally
        {
            oldPassword.Password = string.Empty;
            newPassword.Password = string.Empty;
            confirmPassword.Password = string.Empty;
            _processing = false;
            UpdateRecordControl();
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

    private bool EnsureAdminForOperation()
    {
        if (_adminReview is null || string.IsNullOrWhiteSpace(_adminActorId))
        {
            StatusText.Text = "呢項操作需要 Family Admin 權限。";
            return false;
        }

        try
        {
            _adminReview.EnsureAuthorized(_adminActorId);
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            StatusText.Text = "呢項操作需要獲授權嘅 Windows 管理員。";
            return false;
        }
    }

    private void MainWindow_Closed(object sender, WindowEventArgs args)
    {
        _retryCancellation?.Cancel();
        _sourcePlaybackCancellation?.Cancel();
        _currentInfoCancellation?.Cancel();
        if (_capture?.State == AudioCaptureState.Capturing)
        {
            _capture.AbortForRecovery();
            EndActiveTurnSafely();
            if (_session is not null)
            {
                try { _session = _repository.EndSession(_session); } catch { }
            }
        }

        _ = ShutdownRuntimeAsync();
    }

    private async Task ShutdownRuntimeAsync()
    {
        try { await StopRetryWorkerAsync().ConfigureAwait(false); } catch { }
        try { await DisposeActiveRealtimeStreamingAsync().ConfigureAwait(false); } catch { }
        try { await WaitForArchiveOperationsAsync().ConfigureAwait(false); } catch { }
        if (Application.Current is App app)
            app.DisposeRuntimeServices();
    }

    private async Task<T> RunArchiveWorkAsync<T>(Func<T> work)
    {
        ArgumentNullException.ThrowIfNull(work);
        BeginArchiveOperation();
        try
        {
            return await Task.Run(work).ConfigureAwait(true);
        }
        finally
        {
            EndArchiveOperation();
        }
    }

    private async Task RunArchiveWorkAsync(Action work)
    {
        ArgumentNullException.ThrowIfNull(work);
        BeginArchiveOperation();
        try
        {
            await Task.Run(work).ConfigureAwait(true);
        }
        finally
        {
            EndArchiveOperation();
        }
    }

    private void BeginArchiveOperation()
    {
        lock (_archiveOperationGate)
        {
            if (_activeArchiveOperations == 0)
                _archiveOperationsIdle = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _activeArchiveOperations++;
        }
    }

    private void EndArchiveOperation()
    {
        TaskCompletionSource<bool>? idle = null;
        lock (_archiveOperationGate)
        {
            if (_activeArchiveOperations <= 0) return;
            _activeArchiveOperations--;
            if (_activeArchiveOperations == 0)
                idle = _archiveOperationsIdle;
        }

        idle?.TrySetResult(true);
    }

    private Task WaitForArchiveOperationsAsync()
    {
        lock (_archiveOperationGate)
            return _activeArchiveOperations == 0 ? Task.CompletedTask : _archiveOperationsIdle.Task;
    }

    private void CaptureFailed(object? sender, Exception error)
    {
        _dispatcherQueue.TryEnqueue(() =>
        {
            _ = DisposeActiveRealtimeStreamingAsync();
            if (_session is not null && _session.EndedAt is null)
            {
                try { _session = _repository.EndSession(_session); } catch { }
            }
            EndActiveTurnSafely();
            _session = null;
            _lastSource = null;
            _pendingClarificationRevision = null;
            ClarificationPanel.Visibility = Visibility.Collapsed;
            StatusText.Text = "錄音中斷，已保留暫存檔；請檢查咪高風或 Windows 權限。";
            RecordButton.Content = "開始錄音";
            ConsentCheckBox.IsEnabled = true;
            CloudConsentCheckBox.IsEnabled = true;
            RealtimeConsentCheckBox.IsEnabled = true;
            RecordingEnabledCheckBox.IsEnabled = true;
            PrivacyModeBox.IsEnabled = true;
            UpdateRecordControl();
        });
    }

    private async Task DisposeActiveRealtimeStreamingAsync()
    {
        var streaming = Interlocked.Exchange(ref _activeRealtimeStreaming, null);
        if (streaming is null) return;
        try { await streaming.DisposeAsync(); } catch { }
    }

    private bool IsApplicationLockConfigured()
    {
        try
        {
            return _applicationLock?.IsConfigured == true;
        }
        catch
        {
            // If the lock store cannot be read, fail closed and require the
            // user to resolve the credential issue before seeing the archive.
            AppLockStatusText.Text = "未能讀取應用程式鎖；請輸入密碼或檢查 Windows Credential Manager。";
            return _applicationLock is not null;
        }
    }

    private void ApplyLockState()
    {
        var configured = IsApplicationLockConfigured();
        MainContentScrollViewer.IsEnabled = !_locked;
        AppLockOverlay.Visibility = _locked ? Visibility.Visible : Visibility.Collapsed;
        ConfigureApplicationLockButton.Visibility = _applicationLock is null ? Visibility.Collapsed : Visibility.Visible;
        ConfigureApplicationLockButton.Content = configured ? "停用應用程式鎖" : "設定應用程式鎖";
        LockNowButton.Visibility = configured ? Visibility.Visible : Visibility.Collapsed;
        if (_locked)
        {
            AppLockPasswordBox.Password = string.Empty;
            if (string.IsNullOrWhiteSpace(AppLockStatusText.Text))
                AppLockStatusText.Text = "請輸入密碼解鎖。";
        }
        UpdateRecordControl();
    }

    private void UpdateRecordControl()
    {
        if (_locked)
        {
            MainContentScrollViewer.IsEnabled = false;
            RecordButton.IsEnabled = false;
            ProcessButton.IsEnabled = false;
            RealtimeProcessButton.IsEnabled = false;
            PlaySpeechButton.IsEnabled = false;
            ClarificationPanel.IsHitTestVisible = false;
            return;
        }

        MainContentScrollViewer.IsEnabled = true;
        var capturing = _capture?.State == AudioCaptureState.Capturing;
        var selectedPrivacyMode = GetSelectedPrivacyMode();
        CloudConsentCheckBox.IsEnabled = !capturing && !_processing && !CloudNotPermittedException.IsBlocked(selectedPrivacyMode);
        RealtimeConsentCheckBox.IsEnabled = !capturing && !_processing && !CloudNotPermittedException.IsBlocked(selectedPrivacyMode);
        ConsentCheckBox.IsEnabled = !capturing && !_processing;
        RecordingEnabledCheckBox.IsEnabled = !capturing && !_processing;
        PrivacyModeBox.IsEnabled = !capturing && !_processing && _sourcePlaybackCancellation is null;
        RecordButton.IsEnabled = capturing
            ? !_processing && _sourcePlaybackCancellation is null
            : _recordingEnabled && ConsentCheckBox.IsChecked == true && ConsentCheckBox.IsEnabled && _sourcePlaybackCancellation is null;
        ProcessButton.IsEnabled = !_processing && _sourcePlaybackCancellation is null && _voiceConversation is not null && _capture?.State != AudioCaptureState.Capturing && _lastSource?.FilePath is not null && !string.Equals(_lastSource.RecoveryStatus, "withdrawn", StringComparison.OrdinalIgnoreCase) && _session?.EndedAt is not null && _session is not null && !CloudNotPermittedException.IsBlocked(_session.PrivacyMode) && CloudConsentCheckBox.IsChecked == true;
        RealtimeProcessButton.IsEnabled = !_processing && _sourcePlaybackCancellation is null && _realtimeConversation is not null && _capture?.State != AudioCaptureState.Capturing && _lastSource?.FilePath is not null && !string.Equals(_lastSource.RecoveryStatus, "withdrawn", StringComparison.OrdinalIgnoreCase) && _session?.EndedAt is not null && _session is not null && !CloudNotPermittedException.IsBlocked(_session.PrivacyMode) && HasGrantedRealtimeConsent();
        PlaySpeechButton.IsEnabled = !_processing && _sourcePlaybackCancellation is null && _latestSpeechAsset is not null && _speechPlayback is not null;
        var adminIdle = !_processing && _capture?.State != AudioCaptureState.Capturing && _sourcePlaybackCancellation is null;
        ClarificationPanel.IsHitTestVisible = adminIdle && _pendingClarificationRevision is not null;
        SearchCurrentInformationButton.IsEnabled = !_processing && _currentInfoCancellation is null && _sourcePlaybackCancellation is null && _capture?.State != AudioCaptureState.Capturing && _currentInformation is not null && !CloudNotPermittedException.IsBlocked(selectedPrivacyMode) && (_session is null || !CloudNotPermittedException.IsBlocked(_session.PrivacyMode));
        AdminReviewButton.IsEnabled = adminIdle && _adminReview is not null && _deletion is not null;
        DeleteLatestSourceButton.IsEnabled = adminIdle && _deletion is not null && _lastSource is not null;
        WithdrawLatestSourceButton.IsEnabled = adminIdle && _withdrawal is not null && _lastSource is not null && !string.Equals(_lastSource.RecoveryStatus, "withdrawn", StringComparison.OrdinalIgnoreCase);
        PlaySourceButton.IsEnabled = adminIdle && _sourceAudioPlayback is not null && _lastSource is not null;
        ExportButton.IsEnabled = adminIdle && _adminReview is not null && _deletion is not null;
        ScopedExportButton.IsEnabled = adminIdle && _adminReview is not null && _deletion is not null;
        BackupButton.IsEnabled = adminIdle && _adminReview is not null && _deletion is not null;
        RotateBackupButton.IsEnabled = adminIdle && _adminReview is not null && _deletion is not null;
        RestoreButton.IsEnabled = adminIdle && _adminReview is not null && _deletion is not null;
        RecoverAudioButton.IsEnabled = adminIdle && _adminReview is not null && _adminActorId is not null;
        LockNowButton.IsEnabled = _applicationLock?.IsConfigured == true && adminIdle;
    }

    private bool HasGrantedCloudConsent()
        => _session is not null
           && !CloudNotPermittedException.IsBlocked(_session.PrivacyMode)
           && CloudConsentCheckBox.IsChecked == true
           && _repository.HasGrantedConsent(_session.SessionId, ConsentScope.CloudTranscription);

    private bool HasGrantedRealtimeConsent()
        => _session is not null
           && !CloudNotPermittedException.IsBlocked(_session.PrivacyMode)
           && RealtimeConsentCheckBox.IsChecked == true
           && _repository.HasGrantedConsent(_session.SessionId, ConsentScope.LiveCloudConversation);

    private PrivacyMode GetSelectedPrivacyMode()
    {
        var tag = (PrivacyModeBox.SelectedItem as ComboBoxItem)?.Tag?.ToString();
        return Enum.TryParse<PrivacyMode>(tag, out var mode) ? mode : PrivacyMode.Normal;
    }

    private void SetSelectedPrivacyMode(PrivacyMode mode)
    {
        for (var index = 0; index < PrivacyModeBox.Items.Count; index++)
        {
            if (PrivacyModeBox.Items[index] is ComboBoxItem item && string.Equals(item.Tag?.ToString(), mode.ToString(), StringComparison.Ordinal))
            {
                PrivacyModeBox.SelectedIndex = index;
                return;
            }
        }
        PrivacyModeBox.SelectedIndex = 0;
    }

    private static string PrivacyModeLabel(PrivacyMode mode)
        => mode switch
        {
            PrivacyMode.PrivateConversation => "私密對話，只保留本機",
            PrivacyMode.LocalCaptureOnly => "只本機保存",
            _ => "一般模式"
        };

    private void EndActiveTurnSafely()
    {
        if (_turn is null || _turn.EndedAt is not null) return;
        try
        {
            _turn = _repository.EndTurn(_turn);
        }
        catch
        {
            // Keep the capture/recovery path alive if the best-effort turn close fails.
        }
    }

    private void StartRetryWorkerIfAvailable()
    {
        if (_locked || _retryWorker is null || _retryTask is not null || _credentialAvailable is null) return;
        var dueJobs = 0;
        try
        {
            dueJobs = _repository.ListRetryableConversationJobs(DateTimeOffset.UtcNow).Count;
        }
        catch
        {
            // The launch path should remain usable even if this health/read
            // query fails; the worker reports later processing errors itself.
        }

        try
        {
            if (!_credentialAvailable())
            {
                if (dueJobs > 0)
                    StatusText.Text = $"有 {dueJobs} 項雲端工作等待 credential；本機錄音仍然保留。";
                return;
            }
        }
        catch
        {
            return;
        }

        if (dueJobs > 0)
            StatusText.Text = $"背景重試已啟動：{dueJobs} 項工作等待處理。";
        _retryCancellation = new CancellationTokenSource();
        _retryTask = RunRetryWorkerAsync(_retryCancellation.Token);
    }

    private async Task StopRetryWorkerAsync()
    {
        _retryCancellation?.Cancel();
        var task = _retryTask;
        if (task is not null)
        {
            try { await task.ConfigureAwait(true); }
            catch (Exception) { }
        }
        _retryTask = null;
        _retryCancellation?.Dispose();
        _retryCancellation = null;
    }

    private async Task RunRetryWorkerAsync(CancellationToken cancellationToken)
    {
        try
        {
            var progress = new Progress<ConversationWorkerRunResult>(result =>
            {
                _dispatcherQueue.TryEnqueue(() =>
                {
                    if (result.Succeeded > 0)
                        StatusText.Text = $"背景重試完成：{result.Succeeded} 項工作已處理。";
                    else if (result.Failed > 0)
                        StatusText.Text = "背景重試暫時未完成；會按重試時間再試。";
                });
            });
            await _retryWorker!.RunUntilCancelledAsync(TimeSpan.FromSeconds(30), cancellationToken, progress);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception)
        {
            _dispatcherQueue.TryEnqueue(() => StatusText.Text = "背景重試已停止；本機資料仍然保留。");
        }
        finally
        {
            if (_retryCancellation?.Token == cancellationToken)
            {
                _retryTask = null;
                _retryCancellation.Dispose();
                _retryCancellation = null;
            }
        }
    }
}
