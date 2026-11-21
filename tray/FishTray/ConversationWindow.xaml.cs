using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace FishTray;

public partial class ConversationPanel : UserControl, IDisposable
{
    private sealed record Provider(string Id, string Name);
    private sealed record Turn(
        [property: JsonPropertyName("role")] string Role,
        [property: JsonPropertyName("content")] string Content);

    private readonly CancellationTokenSource lifetime = new();
    private readonly List<Turn> history = [];
    private readonly DispatcherTimer recordLimit = new() { Interval = TimeSpan.FromSeconds(ConversationRecording.SecondsLimit) };
    private CancellationTokenSource? generation;
    private ConversationRecording? recording;
    private string? requestId;
    private bool ready;
    private bool voiceInputReady;
    private bool busy;
    private bool refreshing;
    private bool expanded;
    private bool disposed;

    public ConversationPanel()
    {
        InitializeComponent();
        Loaded += (_, _) => ConnectWhenVisible();
        Unloaded += (_, _) => StopHiddenRecording();
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible) ConnectWhenVisible();
            else StopHiddenRecording();
        };
        RefreshButton.Click += async (_, _) => await RefreshStatus();
        Prompt.TextChanged += (_, _) => UpdateControls();
        ProviderPicker.SelectionChanged += (_, _) => UpdateControls();
        Prompt.PreviewKeyDown += async (_, e) =>
        {
            if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None && SendButton.IsEnabled)
            {
                e.Handled = true;
                await Send();
            }
        };
        SendButton.Click += async (_, _) => await Send();
        CancelButton.Click += (_, _) => CancelGeneration();
        RecordButton.Click += async (_, _) =>
        {
            if (recording is null) StartRecording();
            else await TranscribeRecording();
        };
        recordLimit.Tick += async (_, _) => await TranscribeRecording();
    }

    internal void SetExpanded(bool value)
    {
        if (disposed) return;
        expanded = value;
        if (expanded) ConnectWhenVisible();
        else StopHiddenRecording();
    }

    private void ConnectWhenVisible()
    {
        if (!disposed && expanded && IsLoaded && IsVisible && !ready) _ = RefreshStatus();
    }

    private void StopHiddenRecording()
    {
        recordLimit.Stop();
        if (recording is not { } capture) return;
        recording = null;
        capture.Dispose();
        ActivityStatus.Text = "录音已停止。";
        UpdateControls();
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        StopHiddenRecording();
        CancelGeneration();
        lifetime.Cancel();
        lifetime.Dispose();
    }

    private void UpdateControls()
    {
        bool available = !busy && !refreshing;
        bool idle = available && recording is null;
        RefreshButton.IsEnabled = idle;
        ProviderPicker.IsEnabled = idle && ProviderPicker.HasItems;
        SpeakToggle.IsEnabled = idle;
        LanguagePicker.IsEnabled = idle && voiceInputReady;
        RecordButton.IsEnabled = available && voiceInputReady;
        RecordButton.Content = recording is null ? "录音" : "停止录音";
        Prompt.IsReadOnly = !idle;
        SendButton.IsEnabled = idle && ready && ProviderPicker.SelectedValue is string && !string.IsNullOrWhiteSpace(Prompt.Text);
        CancelButton.Visibility = requestId is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private static string? Text(JsonElement? data, string key) => data is { ValueKind: JsonValueKind.Object } value &&
        value.TryGetProperty(key, out var field) && field.ValueKind == JsonValueKind.String ? field.GetString() : null;

    private static bool Flag(JsonElement? data, string key) => data is { ValueKind: JsonValueKind.Object } value &&
        value.TryGetProperty(key, out var field) && field.ValueKind == JsonValueKind.True;

    private async Task RefreshStatus()
    {
        if (refreshing || busy || recording is not null) return;
        refreshing = true;
        UpdateControls();
        ConnectionStatus.Text = "正在检查连接…";
        var selectedId = ProviderPicker.SelectedValue as string;
        var status = await Pet.ConversationCall("conversation_status", new { }, 65, lifetime.Token);
        if (lifetime.IsCancellationRequested) return;

        ready = Flag(status, "ready");
        voiceInputReady = Flag(status, "voiceInputReady");
        var providers = new List<Provider>();
        if (status is { ValueKind: JsonValueKind.Object } info && info.TryGetProperty("providers", out var options) && options.ValueKind == JsonValueKind.Array)
        {
            foreach (var option in options.EnumerateArray())
                if (Text(option, "id") is { Length: > 0 } id) providers.Add(new Provider(id, Text(option, "name") ?? id));
        }
        ProviderPicker.ItemsSource = providers;
        ProviderPicker.SelectedItem = providers.FirstOrDefault(p => p.Id == selectedId)
            ?? providers.FirstOrDefault(p => p.Id == Text(status, "provider")) ?? providers.FirstOrDefault();
        string voice = Flag(status, "voiceReady") ? "藤田琴音可用" : "藤田琴音未连接";
        string input = voiceInputReady ? "录音识别可用" : "录音识别未连接";
        ConnectionStatus.Text = Text(status, "error") ?? (ready ? $"对话已连接 · {voice} · {input}"
            : Text(status, "message") ?? "没有连接到之前的对话服务，启动后点刷新。");
        refreshing = false;
        UpdateControls();
    }

    private async Task Send()
    {
        if (!SendButton.IsEnabled || ProviderPicker.SelectedValue is not string provider) return;
        string text = Prompt.Text.Trim();
        var recent = history.TakeLast(10).Append(new Turn("user", text)).ToList();
        while (recent.Count > 1 && recent.Sum(turn => Encoding.UTF8.GetByteCount(turn.Content)) > 48_000)
            recent.RemoveRange(0, 2);
        bool speak = SpeakToggle.IsChecked == true;
        var id = Guid.NewGuid().ToString("N");
        using var call = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        generation = call;
        requestId = id;
        busy = true;
        ActivityStatus.Text = "等待大肥鱼回复…";
        UpdateControls();

        try
        {
            var result = await Pet.ConversationCall("conversation_send", new { provider, request_id = id, history = recent }, 225, call.Token);
            generation = null;
            requestId = null;
            if (lifetime.IsCancellationRequested) return;
            UpdateControls();
            if (call.IsCancellationRequested) return;
            if (Text(result, "reply") is not { Length: > 0 } reply)
            {
                ActivityStatus.Text = Text(result, "error") ?? "没有收到回复，可以重试。";
                return;
            }

            history.Add(new Turn("user", text));
            history.Add(new Turn("assistant", reply.Length <= 2000 ? reply : reply[..2000]));
            if (history.Count > 12) history.RemoveRange(0, history.Count - 12);
            Transcript.AppendText($"你\n{text}\n\n大肥鱼\n{reply}\n\n");
            Transcript.ScrollToEnd();
            Prompt.Clear();
            if (result is { ValueKind: JsonValueKind.Object } message && message.TryGetProperty("presentation", out var presentation))
            {
                ActivityStatus.Text = speak ? "正在准备语音…" : "正在显示桌面回复…";
                var shown = await Pet.ConversationCall("conversation_present", new { presentation, speak }, 385, lifetime.Token);
                if (lifetime.IsCancellationRequested) return;
                ActivityStatus.Text = Text(shown, "warning") ?? Text(shown, "error") ??
                    (shown is null ? "已收到回复，桌面播放暂时未连接。" : Flag(shown, "spoken") ? "已朗读回复" : "收到回复");
            }
            else ActivityStatus.Text = "收到回复";
        }
        finally
        {
            generation = null;
            requestId = null;
            busy = false;
            if (!lifetime.IsCancellationRequested) { UpdateControls(); Prompt.Focus(); }
        }
    }

    private void CancelGeneration()
    {
        if (generation is null || generation.IsCancellationRequested || requestId is not { } id) return;
        _ = Pet.ConversationCall("conversation_cancel", new { request_id = id }, 6);
        generation.Cancel();
        ActivityStatus.Text = "已取消等待";
    }

    private void StartRecording()
    {
        try { recording = new ConversationRecording(); }
        catch (Exception)
        {
            ActivityStatus.Text = "无法使用麦克风，请检查输入设备和系统权限。";
            return;
        }
        ActivityStatus.Text = "录音中，点击停止录音完成识别。最长 20 秒。";
        recordLimit.Start();
        UpdateControls();
    }

    private async Task TranscribeRecording()
    {
        if (recording is not { } capture) return;
        recordLimit.Stop();
        recording = null;
        busy = true;
        UpdateControls();
        ActivityStatus.Text = "正在识别录音…";
        var language = LanguagePicker.SelectedValue as string ?? "zh";
        try
        {
            var bytes = await capture.Finish().WaitAsync(TimeSpan.FromSeconds(4), lifetime.Token);
            if (bytes.Length == 0) { ActivityStatus.Text = "录音太短，请重试。"; return; }
            var result = await Pet.ConversationCall("conversation_transcribe", new { audio = Convert.ToBase64String(bytes), language }, 185, lifetime.Token);
            if (lifetime.IsCancellationRequested) return;
            if (Text(result, "text") is not { Length: > 0 } words)
            {
                ActivityStatus.Text = Text(result, "error") ?? "没有识别到文字，请重试。";
                return;
            }
            string combined = string.IsNullOrWhiteSpace(Prompt.Text) ? words.Trim() : Prompt.Text.TrimEnd() + "\n" + words.Trim();
            Prompt.Text = combined.Length <= Prompt.MaxLength ? combined : combined[..Prompt.MaxLength];
            Prompt.CaretIndex = Prompt.Text.Length;
            ActivityStatus.Text = "已填入识别文字，可以编辑后发送。";
        }
        catch (Exception)
        {
            if (!lifetime.IsCancellationRequested) ActivityStatus.Text = "录音结束失败，请重新录音。";
        }
        finally
        {
            capture.Dispose();
            busy = false;
            if (!lifetime.IsCancellationRequested) { UpdateControls(); Prompt.Focus(); }
        }
    }
}
