using System.Text.Json;
using Memento.Core.Companion;
using Memento.Core.Conversation;
using Memento.Core.Audio;
using Memento.Core.Domain;
using Memento.Core.Security;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NAudio.Wave;

namespace Memento.App;

public sealed partial class MainWindow
{
    private ConversationCoordinator? _companion;
    private bool _companionStarting;
    private string CompanionWorkspace => Path.Combine(_dataRoot, "companion-workspace");
    private CompanionArchive CompanionArchive => new(_repository);

    private void InitializeCompanion()
    {
        var voices = WindowsSpeechOutput.Voices();
        CompanionVoiceBox.ItemsSource = voices;
        // The owner explicitly approved Mandarin as a temporary voice on 2026-09-15.
        CompanionVoiceBox.SelectedItem = voices.FirstOrDefault(v => v.Id == _repository.GetSetting("companion_voice"))
            ?? voices.FirstOrDefault(v => v.Language.Equals("zh-HK", StringComparison.OrdinalIgnoreCase))
            ?? voices.FirstOrDefault(v => v.Language.Equals("zh-CN", StringComparison.OrdinalIgnoreCase))
            ?? voices.FirstOrDefault(v => v.Language.Equals("zh-TW", StringComparison.OrdinalIgnoreCase));
        if (CompanionVoiceBox.SelectedItem is LocalVoice selectedVoice && selectedVoice.Language != "zh-HK")
            CompanionSetupStatus.Text = $"暫用普通話聲音：{selectedVoice.Name} ({selectedVoice.Language})。廣東話語音仍待測試。";
        CompanionModelBox.ItemsSource = new[] { _repository.GetSetting("companion_model") ?? "gpt-5.6-luna" };
        CompanionModelBox.SelectedIndex = 0;
        CompanionConsentCheckBox.IsChecked = _repository.GetSetting("companion_cloud_consent") == "1";
        if (double.TryParse(_repository.GetSetting("companion_silence"), System.Globalization.CultureInfo.InvariantCulture, out var silence)) CompanionSilenceBox.Value = Math.Clamp(silence, 0.8, 5);
        ParticipantSettingsPanel.Visibility = Visibility.Visible;
        if (_repository.GetSetting("optional_paid_api_enabled") != "1")
        {
            CloudConsentCheckBox.Visibility = Visibility.Collapsed;
            RealtimeConsentCheckBox.Visibility = Visibility.Collapsed;
        }
        UpdateCompanionControls();
    }
    private void UpdateCompanionControls()
    {
        if (CompanionConsentCheckBox is null) return;
        var active = _companion is not null || _companionStarting;
        RecordButton.Content = "開始傾偈";
        RecordButton.IsEnabled = !_locked && !active && !_processing && _sourcePlaybackCancellation is null;
        EndConversationButton.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
        InterruptCompanionButton.Visibility = _companion is not null ? Visibility.Visible : Visibility.Collapsed;
        ParticipantSettingsButton.IsEnabled = !active;
        FamilyAdminButton.IsEnabled = !active;
        if (active) { ParticipantSettingsPanel.Visibility = Visibility.Collapsed; FamilyAdminPanel.Visibility = Visibility.Collapsed; }
        LockNowButton.IsEnabled = !active && _applicationLock?.IsConfigured == true;
        SendCompanionTextButton.IsEnabled = _companion?.State == CompanionState.Listening;
    }
    private CodexCompanionBackend NewCompanionBackend()
    {
        var executable = CodexRpc.FindExecutable() ?? throw new InvalidOperationException("搵唔到 Codex。請先安裝並登入 Codex，或設定 MEMENTO_CODEX_PATH。");
        return new(new CodexRpc(executable, CompanionWorkspace), CompanionWorkspace);
    }
    private async void ProbeCompanion_Click(object sender, RoutedEventArgs e)
    {
        if (_locked || _companion is not null) return;
        try
        {
            CompanionSetupStatus.Text = "檢查緊…";
            await using var backend = NewCompanionBackend();
            var capabilities = await backend.ProbeAsync();
            var model = CompanionModelBox.SelectedItem?.ToString();
            CompanionModelBox.ItemsSource = capabilities.Models.Where(m => m.Efforts.Contains("low")).Select(m => m.Id).ToArray();
            CompanionModelBox.SelectedItem = capabilities.Models.Any(m => m.Id == model) ? model : capabilities.Models.FirstOrDefault(m => m.Id == "gpt-5.6-luna")?.Id;
            var local = File.Exists(Path.Combine(_dataRoot, "speech", "local-speech.json"));
            CompanionSetupStatus.Text = $"{capabilities.Status}\n本機辨識：{(local ? "已安裝" : "未安裝，請執行 Setup-MementoLocalSpeech.ps1")}\n廣東話聲音：{(WindowsSpeechOutput.Voices().Any(v => v.Language == "zh-HK") ? "可用" : "未安裝；已獲同意暫用普通話，請確認下方所選聲音。")}";
        }
        catch (Exception error) { CompanionSetupStatus.Text = error.Message; }
    }
    private async void RecordButton_Click(object sender, RoutedEventArgs e)
    {
        if (_locked || _companion is not null || _companionStarting) return;
        _companionStarting = true; UpdateCompanionControls();
        try
        {
            if (GetSelectedPrivacyMode() != PrivacyMode.Normal || CompanionConsentCheckBox.IsChecked != true) throw new InvalidOperationException("AI 傾偈需要一般模式及雲端對話同意。只本機保存可以使用家庭管理內嘅錄音診斷。");
            var textOnly = CompanionTextMode.IsChecked == true;
            if (!textOnly && (!_recordingEnabled || ConsentCheckBox.IsChecked != true)) throw new InvalidOperationException("請先啟用本機錄音並同意錄音。");
            var model = CompanionModelBox.SelectedItem?.ToString() ?? throw new InvalidOperationException("請先檢查設定並選擇模型。");
            var voice = CompanionVoiceBox.SelectedItem as LocalVoice;
            if (!textOnly && voice is null) throw new InvalidOperationException("請先安裝及選擇本機說話聲音，或使用文字測試。");
            WhisperLocalTranscription? stt = null;
            if (!textOnly)
            {
                var file = Path.Combine(_dataRoot, "speech", "local-speech.json");
                if (!File.Exists(file)) throw new InvalidOperationException("請先執行 Setup-MementoLocalSpeech.ps1 安裝本機辨識。");
                using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(file));
                stt = new(doc.RootElement.GetProperty("executable").GetString()!, doc.RootElement.GetProperty("model").GetString()!, Path.Combine(_dataRoot, "speech", "scratch"));
            }
            _repository.SetSetting("companion_model", model);
            _repository.SetSetting("companion_cloud_consent", "1");
            if (voice is not null) _repository.SetSetting("companion_voice", voice.Id);
            var silence = double.IsFinite(CompanionSilenceBox.Value) ? Math.Clamp(CompanionSilenceBox.Value, 0.8, 5) : 1.8;
            _repository.SetSetting("companion_silence", silence.ToString(System.Globalization.CultureInfo.InvariantCulture));
            var store = new DerivedAudioStore(_repository, Path.Combine(_dataRoot, "derived", "audio"));
            _companion = new(CompanionArchive, NewCompanionBackend(), _audioRoot, store, stt, textOnly ? null : new WindowsSpeechOutput(voice!.Id), textOnly ? null : new WaveFileSpeechOutputPlayback(store), new EnergyUtteranceDetector(silence));
            _companion.StatusChanged += ShowCompanionStatus;
            CompanionTextPanel.Visibility = textOnly ? Visibility.Visible : Visibility.Collapsed;
            ParticipantSettingsPanel.Visibility = Visibility.Collapsed;
            FamilyAdminPanel.Visibility = Visibility.Collapsed;
            PrivacyIndicatorText.Text = textOnly ? "文字經 Codex 送到雲端" : "● 錄音中 · 文字經 Codex 送到雲端";
            await _companion.StartAsync(model, PrivacyMode.Normal, !textOnly, true, textOnly ? null : () => new WaveInAudioInput(ContinuousCapture.Format));
        }
        catch (Exception error)
        {
            if (_companion is not null) await StopCompanionAsync();
            CompanionSetupStatus.Text = error.Message; ConversationResponseText.Text = error.Message;
            ParticipantSettingsPanel.Visibility = Visibility.Visible;
        }
        finally { _companionStarting = false; UpdateCompanionControls(); }
    }
    private void ShowCompanionStatus(CompanionStatus status) => _dispatcherQueue.TryEnqueue(() =>
    {
        ConversationStateText.Text = status.State switch { CompanionState.Listening => "正在聆聽…", CompanionState.Thinking => "諗緊…", CompanionState.Transcribing => "聽緊你講嘅意思…", CompanionState.Speaking => "MEMENTO 正在講嘢…", CompanionState.Ended => "已保存", CompanionState.RecoverableError => "暫時需要幫手", _ => "準備中…" };
        ConversationResponseText.Text = status.Message;
        UpdateCompanionControls();
    });
    private async Task StopCompanionAsync()
    {
        var companion = _companion;
        if (companion is null) return;
        try { await companion.DisposeAsync(); }
        finally
        {
            companion.StatusChanged -= ShowCompanionStatus;
            _companion = null;
            _lastSource = _repository.GetLatestFinalizedSource();
            _dispatcherQueue.TryEnqueue(() => { PrivacyIndicatorText.Text = "錄音已停止"; UpdateRecordControl(); });
        }
    }
    private async void InterruptCompanion_Click(object sender, RoutedEventArgs e) { if (_companion is not null) await _companion.InterruptAsync(); }
    private async void SendCompanionText_Click(object sender, RoutedEventArgs e)
    {
        if (_companion is null) return;
        try { var text = CompanionTextInput.Text; CompanionTextInput.Text = ""; await _companion.SendTextAsync(text); }
        catch (Exception error) { ConversationResponseText.Text = error.Message; }
    }
    private bool IsCompanionAdmin()
    {
        var authorizer = new WindowsAdministratorAuthorizer();
        return !_locked && _companion is null && authorizer.IsAuthorized(authorizer.GetCurrentActorId());
    }
    private void RefreshCompanionTimeline_Click(object sender, RoutedEventArgs e)
    {
        if (!IsCompanionAdmin()) { StatusText.Text = "需要家庭管理員權限，並先結束對話。"; return; }
        using var c = _repository.Archive.OpenConnection(); using var q = c.CreateCommand();
        q.CommandText = "SELECT c.session_id,s.started_at FROM companion_sessions c JOIN sessions s ON s.session_id=c.session_id ORDER BY s.started_at DESC";
        using var r = q.ExecuteReader(); var items = new List<ComboBoxItem>();
        while (r.Read()) items.Add(new ComboBoxItem { Tag = r.GetString(0), Content = r.GetString(1) });
        CompanionSessionBox.ItemsSource = items; CompanionSessionBox.SelectedIndex = items.Count > 0 ? 0 : -1;
    }
    private void CompanionSession_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!IsCompanionAdmin() || CompanionSessionBox.SelectedItem is not ComboBoxItem item) return;
        CompanionTimelineList.ItemsSource = CompanionArchive.Timeline((string)item.Tag);
    }
    private void CompanionTimeline_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (CompanionTimelineList.SelectedItem is not CompanionTimelineItem item) return;
        CompanionCorrectionText.Text = item.Text;
        var spans = CompanionArchive.Spans(item.MessageId);
        CompanionRevisionInfo.Text = $"{item.Status} · {item.Kind} · {item.Author}\n{item.Reason}\n原聲片段：{spans.Count}（可能包含其他人或 AI 播放聲音）";
    }
    private void SaveCompanionRevision_Click(object sender, RoutedEventArgs e)
    {
        if (!IsCompanionAdmin() || CompanionTimelineList.SelectedItem is not CompanionTimelineItem item) return;
        try
        {
            var actor = new WindowsAdministratorAuthorizer().GetCurrentActorId();
            CompanionArchive.AddRevision(item.MessageId, CompanionCorrectionText.Text, "family-correction", actor, CompanionCorrectionReason.Text, item.RevisionId);
            CompanionSession_Changed(sender, null!);
        }
        catch (Exception error) { StatusText.Text = error.Message; }
    }
    private async void PlayCompanionOriginal_Click(object sender, RoutedEventArgs e)
    {
        if (!IsCompanionAdmin() || CompanionTimelineList.SelectedItem is not CompanionTimelineItem item || _sourcePlaybackCancellation is not null) return;
        _sourcePlaybackCancellation = new(); UpdateRecordControl();
        try
        {
            using var wav = new MemoryStream();
            var spans = CompanionArchive.Spans(item.MessageId);
            var bytes = await Task.Run(() =>
            {
                using var pcm = new MemoryStream();
                foreach (var span in spans)
                {
                    var source = _repository.GetSource(span.SourceId) ?? throw new InvalidDataException("原聲已刪除。");
                    if (source.RecoveryStatus == "withdrawn") throw new InvalidOperationException("原聲已撤回。");
                    var checkedSource = new Memento.Core.Companion.SourceSpanReader(_repository, _audioRoot);
                    pcm.Write(checkedSource.Read(span));
                }
                return pcm.ToArray();
            });
            if (bytes.Length == 0) { StatusText.Text = "呢段係文字或 AI 回覆，冇參與者原聲。"; return; }
            using (var writer = new WaveFileWriter(wav, new WaveFormat(48000, 16, 1))) { writer.Write(bytes); writer.Flush(); }
            using var playbackStream = new MemoryStream(wav.ToArray());
            using var reader = new WaveFileReader(playbackStream); using var output = new WaveOutEvent();
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            output.PlaybackStopped += (_, args) => { if (args.Exception is null) done.TrySetResult(); else done.TrySetException(args.Exception); };
            output.Init(reader);
            using var registration = _sourcePlaybackCancellation.Token.Register(() => { done.TrySetCanceled(); output.Stop(); });
            output.Play(); await done.Task;
        }
        catch (Exception error) { StatusText.Text = error is OperationCanceledException ? "播放已停止。" : error.Message; }
        finally { _sourcePlaybackCancellation.Dispose(); _sourcePlaybackCancellation = null; UpdateRecordControl(); }
    }
}
