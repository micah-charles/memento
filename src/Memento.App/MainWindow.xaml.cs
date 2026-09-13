using Memento.Core.Audio;
using Memento.Core.Admin;
using Memento.Core.Conversation;
using Memento.Core.Domain;
using Memento.Core.External;
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
    private readonly DispatcherQueue _dispatcherQueue;
    private readonly string _dataRoot;
    private readonly BoundedVoiceConversationService? _voiceConversation;
    private readonly ISpeechOutputPlayback? _speechPlayback;
    private readonly ISourceAudioPlayback? _sourceAudioPlayback;
    private readonly FamilyAdminReviewService? _adminReview;
    private readonly ArchiveDeletionService? _deletion;
    private readonly ArchiveWithdrawalService? _withdrawal;
    private readonly CurrentInformationService? _currentInformation;
    private readonly string? _adminActorId;
    private readonly ConversationJobWorker? _retryWorker;
    private readonly Func<bool>? _credentialAvailable;
    private AudioCaptureController? _capture;
    private Session? _session;
    private Turn? _turn;
    private SourceMetadata? _lastSource;
    private DerivedSpeechAsset? _latestSpeechAsset;
    private bool _processing;
    private bool _recordingEnabled;
    private bool _initializing;
    private CancellationTokenSource? _retryCancellation;
    private Task? _retryTask;
    private CancellationTokenSource? _sourcePlaybackCancellation;
    private CancellationTokenSource? _currentInfoCancellation;

    public MainWindow(ArchiveRepository repository, string audioRoot, int recoverableAudioCount = 0, BoundedVoiceConversationService? voiceConversation = null, ISpeechOutputPlayback? speechPlayback = null, FamilyAdminReviewService? adminReview = null, string? adminActorId = null, ConversationJobWorker? retryWorker = null, Func<bool>? credentialAvailable = null, string? dataRoot = null, ArchiveDeletionService? deletion = null, ArchiveWithdrawalService? withdrawal = null, ISourceAudioPlayback? sourceAudioPlayback = null, CurrentInformationService? currentInformation = null)
    {
        _repository = repository;
        _audioRoot = audioRoot;
        _recoverableAudioCount = recoverableAudioCount;
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
        _dataRoot = dataRoot is null ? Path.GetFullPath(Path.Combine(audioRoot, "..", "..")) : Path.GetFullPath(dataRoot);
        _voiceConversation = voiceConversation;
        _speechPlayback = speechPlayback;
        _sourceAudioPlayback = sourceAudioPlayback;
        _adminReview = adminReview;
        _deletion = deletion;
        _withdrawal = withdrawal;
        _currentInformation = currentInformation;
        _adminActorId = adminActorId;
        _retryWorker = retryWorker;
        _credentialAvailable = credentialAvailable;
        InitializeComponent();
        _initializing = true;
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
                    ConsentCheckBox.IsChecked = _repository.HasGrantedConsent(_session.SessionId, ConsentScope.LocalCapture);
                    CloudConsentCheckBox.IsChecked = _session.PrivacyMode != PrivacyMode.LocalCaptureOnly
                        && _repository.HasGrantedConsent(_session.SessionId, ConsentScope.CloudTranscription);
                }
            }
            _latestSpeechAsset = _repository.GetLatestDerivedSpeechAsset();
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
        if (_session is not null && _session.PrivacyMode != PrivacyMode.LocalCaptureOnly)
            _repository.AddConsent(_session.SessionId, ConsentScope.CloudTranscription, _session.PrivacyMode, CloudConsentCheckBox.IsChecked == true, "privacy-1");
        if (CloudConsentCheckBox.IsChecked != true)
            _currentInfoCancellation?.Cancel();
        UpdateRecordControl();
    }

    private void RecordButton_Click(object sender, RoutedEventArgs e)
    {
        if (_capture?.State != AudioCaptureState.Capturing && ConsentCheckBox.IsChecked != true)
        {
            StatusText.Text = "請先同意本機錄音。";
            UpdateRecordControl();
            return;
        }

        if (_capture?.State == AudioCaptureState.Capturing)
        {
            try
            {
                _lastSource = _capture.Stop();
                if (_turn is not null)
                    _turn = _repository.EndTurn(_turn, _lastSource.FinalizedAt ?? DateTimeOffset.UtcNow);
                if (_session is not null)
                    _session = _repository.EndSession(_session);
                StatusText.Text = "已儲存本機錄音 · Local archive";
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
            var startedAt = DateTimeOffset.UtcNow;
            _session = _repository.AddSession(startedAt, privacyMode);
            _turn = _repository.AddTurn(_session.SessionId, _repository.GetNextTurnSequence(_session.SessionId), "participant", startedAt);
            _repository.AddConsent(_session.SessionId, ConsentScope.LocalCapture, privacyMode, true, "privacy-1");
            if (privacyMode != PrivacyMode.LocalCaptureOnly)
                _repository.AddConsent(_session.SessionId, ConsentScope.CloudTranscription, privacyMode, true, "privacy-1");
            _capture = new AudioCaptureController(_repository, _audioRoot);
            _capture.CaptureFailed += CaptureFailed;
            _capture.Start(_session.SessionId, _turn.TurnId, ConsentCheckBox.IsChecked == true, format => new WaveInAudioInput(format), startedAt);
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
            {
                try { _session = _repository.EndSession(_session); } catch { }
            }
            EndActiveTurnSafely();
            _session = null;
            _lastSource = null;
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

    private void SearchBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != Windows.System.VirtualKey.Enter) return;
        SearchArchiveButton_Click(sender, new RoutedEventArgs());
        e.Handled = true;
    }

    private void SearchArchiveButton_Click(object sender, RoutedEventArgs e)
    {
        var query = SearchBox.Text.Trim();
        if (query.Length == 0)
        {
            SearchResultsText.Text = "請輸入要搜尋嘅字詞。";
            return;
        }

        try
        {
            var hits = new ArchiveSearchService(_repository.Archive).Search(query, 20);
            SearchResultsText.Text = hits.Count == 0
                ? "未找到符合嘅本機記錄。"
                : string.Join(Environment.NewLine, hits.Select(hit => $"[{hit.RecordType}] {hit.Content}"));
            StatusText.Text = $"本機搜尋完成：{hits.Count} 項。";
        }
        catch (Exception)
        {
            SearchResultsText.Text = "未能完成本機搜尋。";
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
        if (_session?.PrivacyMode == PrivacyMode.LocalCaptureOnly)
        {
            CurrentInfoResultsText.Text = "本次對話設定為只保留本機，未能使用雲端目前資訊查詢。";
            return;
        }
        if (CloudConsentCheckBox.IsChecked != true)
        {
            CurrentInfoResultsText.Text = "請先同意使用雲端目前資訊查詢。";
            return;
        }

        var cancellation = new CancellationTokenSource();
        _currentInfoCancellation?.Dispose();
        _currentInfoCancellation = cancellation;
        SearchCurrentInformationButton.IsEnabled = false;
        CurrentInfoResultsText.Text = "查詢中…";
        try
        {
            var result = await _currentInformation.SearchAsync(query, cancellation.Token);
            if (CloudConsentCheckBox.IsChecked != true || _session?.PrivacyMode == PrivacyMode.LocalCaptureOnly)
            {
                CurrentInfoResultsText.Text = "雲端同意已撤回，未顯示目前資訊結果。";
                return;
            }
            CurrentInfoResultsText.Text = result.Sources.Count == 0
                ? "未收到 allowlisted source。"
                : string.Join(Environment.NewLine + Environment.NewLine, result.Sources.Select(source => $"{source.Title}\n{source.Snippet}\n{source.Url}"));
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
        IReadOnlyList<ClaimEvidence> claimEvidence;
        try
        {
            claimEvidence = _repository.ListEvidenceForClaim(claim.MemoryClaimId);
        }
        catch (Exception)
        {
            StatusText.Text = "未能讀取候選記憶嘅原始證據。";
            return;
        }

        var evidenceSummary = claimEvidence.Count == 0
            ? "未有可顯示嘅 supporting Evidence。"
            : string.Join(Environment.NewLine + Environment.NewLine, claimEvidence.Select(item =>
                $"[{item.Relationship}] {item.Evidence.Statement}" +
                $"\n確定程度：{item.Evidence.ParticipantCertainty}；講者確認：{(item.Evidence.SpeakerConfirmed ? "是" : "未有")}" +
                $"\nSource：{item.Evidence.SourceId}"));
        var reviewContent = new TextBlock
        {
            Text = $"候選記憶：{claim.Statement}\n\n原始證據：\n{evidenceSummary}",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 16
        };
        var dialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = "家庭管理審閱",
            Content = new ScrollViewer { Content = reviewContent, MaxHeight = 420 },
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

        try
        {
            var sourceId = _lastSource.SourceId;
            var result = _deletion.DeleteSource(_adminActorId, sourceId, "participant requested deletion");
            _lastSource = null;
            _session = null;
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

        try
        {
            var result = _withdrawal.WithdrawSource(_adminActorId, _lastSource.SourceId, "participant requested future cloud processing withdrawal");
            _lastSource = _repository.GetSource(result.SourceId);
            StatusText.Text = "已停止此錄音日後雲端處理；歷史資料及原始錄音仍然保留。";
            UpdateRecordControl();
        }
        catch (Exception)
        {
            StatusText.Text = "未能停止日後雲端處理；原有資料仍然保留。";
        }
    }

    private void HealthCheckButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var report = ArchiveHealthCheck.Run(_repository.Archive, _audioRoot);
            StatusText.Text = report.Findings.Count == 0
                ? $"健康檢查完成：SQLite {report.SchemaVersion}，未發現問題。"
                : $"健康檢查發現 {report.Findings.Count} 項：{string.Join("；", report.Findings)}";
        }
        catch (Exception)
        {
            StatusText.Text = "未能完成本機資料健康檢查。";
        }
    }

    private void RebuildSearchButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var count = new ArchiveSearchService(_repository.Archive).Rebuild();
            StatusText.Text = $"本機搜尋索引已修復：{count} 項。";
        }
        catch (Exception)
        {
            StatusText.Text = "未能修復本機搜尋索引；原有資料仍然保留。";
        }
    }

    private void ExportButton_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureAdminForOperation()) return;
        try
        {
            var result = ArchiveExporter.Export(_repository.Archive, Path.Combine(_dataRoot, "exports"), includeMedia: true);
            _adminReview!.RecordAdminOperation(_adminActorId!, "export");
            StatusText.Text = $"已匯出本機資料：{result.ExportDirectory}";
        }
        catch (Exception)
        {
            StatusText.Text = "未能匯出本機資料；原有資料仍然保留。";
        }
    }

    private async void BackupButton_Click(object sender, RoutedEventArgs e)
    {
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
        try
        {
            var export = ArchiveExporter.Export(_repository.Archive, temporaryRoot, includeMedia: true);
            var destination = Path.Combine(_dataRoot, "backups", "memento-" + DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmssfff", System.Globalization.CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N") + ".memento");
            ArchiveBackupProtector.EncryptDirectory(export.ExportDirectory, destination, passwordBox.Password);
            _adminReview!.RecordAdminOperation(_adminActorId!, "encrypted_backup");
            StatusText.Text = $"已建立加密備份：{destination}";
        }
        catch (Exception)
        {
            StatusText.Text = "未能建立加密備份；原有資料仍然保留。";
        }
        finally
        {
            passwordBox.Password = string.Empty;
            if (Directory.Exists(temporaryRoot))
                Directory.Delete(temporaryRoot, recursive: true);
        }
    }

    private async void RestoreButton_Click(object sender, RoutedEventArgs e)
    {
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
        try
        {
            var result = ArchiveBackupProtector.DecryptDirectory(backupPath.Text.Trim(), restoreRoot, passwordBox.Password);
            _adminReview!.RecordAdminOperation(_adminActorId!, "restore_verification");
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
    }

    private void CaptureFailed(object? sender, Exception error)
    {
        _dispatcherQueue.TryEnqueue(() =>
        {
            if (_session is not null && _session.EndedAt is null)
            {
                try { _session = _repository.EndSession(_session); } catch { }
            }
            EndActiveTurnSafely();
            _session = null;
            _lastSource = null;
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
        RecordButton.IsEnabled = _recordingEnabled && ConsentCheckBox.IsChecked == true && ConsentCheckBox.IsEnabled && !_processing && _sourcePlaybackCancellation is null;
        ProcessButton.IsEnabled = !_processing && _sourcePlaybackCancellation is null && _voiceConversation is not null && _capture?.State != AudioCaptureState.Capturing && _lastSource?.FilePath is not null && !string.Equals(_lastSource.RecoveryStatus, "withdrawn", StringComparison.OrdinalIgnoreCase) && _session?.EndedAt is not null && _session.PrivacyMode != PrivacyMode.LocalCaptureOnly && CloudConsentCheckBox.IsChecked == true;
        PlaySpeechButton.IsEnabled = !_processing && _sourcePlaybackCancellation is null && _latestSpeechAsset is not null && _speechPlayback is not null;
        var adminIdle = !_processing && _capture?.State != AudioCaptureState.Capturing && _sourcePlaybackCancellation is null;
        SearchCurrentInformationButton.IsEnabled = !_processing && _currentInfoCancellation is null && _sourcePlaybackCancellation is null && _capture?.State != AudioCaptureState.Capturing && _currentInformation is not null;
        AdminReviewButton.IsEnabled = _adminReview is not null && _deletion is not null;
        DeleteLatestSourceButton.IsEnabled = adminIdle && _deletion is not null && _lastSource is not null;
        WithdrawLatestSourceButton.IsEnabled = adminIdle && _withdrawal is not null && _lastSource is not null && !string.Equals(_lastSource.RecoveryStatus, "withdrawn", StringComparison.OrdinalIgnoreCase);
        PlaySourceButton.IsEnabled = adminIdle && _sourceAudioPlayback is not null && _lastSource is not null;
        ExportButton.IsEnabled = adminIdle && _adminReview is not null && _deletion is not null;
        BackupButton.IsEnabled = adminIdle && _adminReview is not null && _deletion is not null;
        RestoreButton.IsEnabled = adminIdle && _adminReview is not null && _deletion is not null;
    }

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
        if (_retryWorker is null || _retryTask is not null || _credentialAvailable is null) return;
        try
        {
            if (!_credentialAvailable()) return;
        }
        catch
        {
            return;
        }

        _retryCancellation = new CancellationTokenSource();
        _retryTask = RunRetryWorkerAsync(_retryCancellation.Token);
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
    }
}
