using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace FishPet;

internal sealed class Conversation
{
    internal const string VoiceName = "藤田琴音";
    private static readonly HttpClient Client = new(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false })
    {
        BaseAddress = new Uri("http://127.0.0.1:18763"),
        Timeout = Timeout.InfiniteTimeSpan
    };
    private static readonly JsonSerializerOptions LocalJson = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private readonly ConversationServices services = ConversationServices.Open();

    internal async Task<object> Status(string voiceLanguage, CancellationToken token)
    {
        await services.EnsureBridge(token);
        if (services.LocalAvailable)
        {
            try { await services.EnsureLocalModel(token); }
            catch (Exception error) when (error is InvalidOperationException or TimeoutException) { }
        }
        var menu = await Json("providers", null, token);
        var providers = menu.GetProperty("providers").EnumerateArray()
            .Select(p => new { id = Text(p, "id"), name = Text(p, "name") })
            .Where(p => ProviderId(p.id))
            .Select(p => new { p.id, name = p.id == "companion_codex" ? "Codex（使用原有默认模型，联网）" : p.name })
            .ToArray();
        var provider = providers.FirstOrDefault(p => p.id == "companion_codex")?.id ?? providers.FirstOrDefault()?.id ?? "";
        return new { ready = providers.Length > 0, voiceReady = services.VoiceAvailable,
            voiceInputReady = services.AsrAvailable, providers, provider, voiceLanguage,
            message = providers.Length == 0 ? "原有对话服务未提供可用模型。" : "已连接原有语音与对话服务。" };
    }

    internal async Task<object> Send(JsonElement args, CancellationToken token)
    {
        string provider = Text(args, "provider");
        if (!ProviderId(provider)) throw new ArgumentException("请从菜单选择对话模型。");
        string requestId = RequestId(args);
        if (!args.TryGetProperty("history", out var history) || history.ValueKind != JsonValueKind.Array || history.GetArrayLength() is < 1 or > 12)
            throw new ArgumentException("请输入一条消息。");
        var messages = history.EnumerateArray().Select(item => new { role = Text(item, "role"), content = Text(item, "content").Trim() }).ToArray();
        if (messages[^1].role != "user" || messages.Any(m => m.role is not ("user" or "assistant") || m.content.Length is < 1 or > 2000))
            throw new ArgumentException("对话内容太长或格式不正确。");

        var lengths = messages.Select(m => JsonSerializer.SerializeToUtf8Bytes(m, LocalJson).Length).ToArray();
        int first = 0, total = lengths.Sum();
        while (first < messages.Length - 1 && (total > 60_000 || messages[first].role == "assistant"))
            total -= lengths[first++];

        await services.EnsureBridge(token);
        if (provider == "local_qwen" || provider.StartsWith("ollama:", StringComparison.Ordinal)) await services.EnsureLocalModel(token);
        var result = await Json("chat", new { provider, request_id = requestId, history = messages[first..], available_actions = Array.Empty<object>() }, token);
        string reply = Text(result, "text").Trim();
        if (reply.Length == 0) throw new InvalidOperationException("所选模型没有返回回复。");
        reply = Limit(reply, 1950);
        return new { reply, presentation = new { text = reply, speech_text = Limit(Text(result, "speech_text"), 1200),
            speech_language = Text(result, "speech_language"), voice_style = Text(result, "voice_style"),
            emotion = Text(result, "emotion"), gesture = Text(result, "gesture") } };
    }

    internal async Task<object> Cancel(JsonElement args, CancellationToken token)
    {
        var result = await Json("cancel", new { request_id = RequestId(args) }, token);
        return new { stopped = result.TryGetProperty("stopped", out var stopped) && stopped.ValueKind == JsonValueKind.True };
    }

    internal async Task<object> Transcribe(JsonElement args, CancellationToken token)
    {
        string audio = Text(args, "audio");
        if (audio.Length is < 60 or > 880_000 || !audio.StartsWith("UklGR", StringComparison.Ordinal))
            throw new ArgumentException("请录下不超过 20 秒的语音。");
        await services.EnsureBridge(token);
        await services.EnsureAsr(token);
        var result = await Json("asr", new { audio, language = Language(Text(args, "language")) }, token);
        return new { text = Text(result, "text").Trim() };
    }

    internal async Task<byte[]> Speak(string subtitle, string draft, string draftLanguage,
        string preference, CancellationToken token)
    {
        string language = draftLanguage is "ja" or "zh" ? draftLanguage : DetectLanguage(draft);
        // A generated draft tagged as Japanese must contain Japanese text to be used for Japanese output.
        bool useDraft = draft.Length > 0 && (preference == "auto" ||
            (preference == language && (language != "ja" || HasKana(draft))));
        string text = Limit(useDraft ? draft : subtitle, 1200);
        if (!useDraft) language = DetectLanguage(text);
        // Mixed-language subtitles can contain kana even when their sentences are Chinese.
        if (preference != "auto" && (preference == "ja" || language != preference || HasKana(text)))
        {
            text = await services.Translate(text, preference, token);
            language = preference;
        }

        await services.EnsureBridge(token);
        await services.EnsureVoice(token);
        // Use Fujita's natural reference for the desktop pet's lines.
        var settings = new { text_lang = language, preset = "neutral" };
        using var response = await Request("tts", new { text = Limit(text, 1200), voice = VoiceName, settings }, token);
        const int max = 24 * 1024 * 1024;
        if (response.Content.Headers.ContentLength > max) throw new InvalidOperationException("朗读录音过长。");
        using var audio = new MemoryStream();
        using var stream = await response.Content.ReadAsStreamAsync(token);
        var buffer = new byte[32768];
        int read;
        while ((read = await stream.ReadAsync(buffer, token)) != 0)
        {
            if (audio.Length + read > max) throw new InvalidOperationException("朗读录音过长。");
            audio.Write(buffer, 0, read);
        }
        return audio.ToArray();
    }

    internal static string PresentationText(JsonElement presentation, out string reply)
    {
        string text = Text(presentation, "text").Replace('〔', '（').Replace('〕', '）').Replace('／', ' ').Replace(" / ", " ");
        reply = Limit(string.Join(" ", text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)).Trim(), 1950);
        if (reply.Length == 0) throw new ArgumentException("回复为空，无法播放。");
        string emotion = Text(presentation, "emotion") switch
        {
            "happy" => "眯眼笑", "shy" => "脸红", "angry" => "皱眉", "sad" => "八字眉", "surprised" => "睁大", _ => ""
        };
        string gesture = Text(presentation, "gesture") switch
        {
            "nod" => "点头", "shake" => "摇头", "lean" => "前倾", "sway" => "晃头", "shy_shift" => "缩一下", _ => ""
        };
        string cue = (emotion + " " + gesture).Trim();
        return cue.Length == 0 ? reply : "〔" + cue + "〕" + reply;
    }

    private async Task<JsonElement> Json(string route, object? data, CancellationToken token)
    {
        using var response = await Request(route, data, token);
        using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(token), cancellationToken: token);
        return json.RootElement.Clone();
    }

    private async Task<HttpResponseMessage> Request(string route, object? data, CancellationToken token)
    {
        string? payload = data == null ? null : JsonSerializer.Serialize(data, LocalJson);
        for (int attempt = 0; ; attempt++)
        {
            using var request = new HttpRequestMessage(payload == null ? HttpMethod.Get : HttpMethod.Post, route);
            request.Headers.Add("X-Companion-Token", services.Token);
            if (payload != null) request.Content = new StringContent(payload, Encoding.UTF8, "application/json");
            var response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
            if (response.IsSuccessStatusCode) return response;
            int status = (int)response.StatusCode;
            response.Dispose();
            // The shared local service can still be finishing a synthesis abandoned by another request.
            if (route == "tts" && status == 409 && attempt < 12)
            {
                await Task.Delay(500, token);
                continue;
            }
            string task = route switch { "chat" => "对话", "asr" => "语音识别", "tts" => "语音合成", _ => "原有服务" };
            throw new InvalidOperationException(status switch
            {
                403 => "原有服务拒绝了当前请求。请检查离线模式和所选模型。",
                409 => task + "正在处理上一条请求，请稍后重试。",
                499 => task + "已停止。",
                _ => task + "失败（" + status + "）。"
            });
        }
    }

    internal static string Text(JsonElement args, string name) => args.ValueKind == JsonValueKind.Object
        && args.TryGetProperty(name, out var item) && item.ValueKind == JsonValueKind.String ? item.GetString()! : "";

    private static bool ProviderId(string value) => value == "companion_codex" || value == "local_qwen"
        || (value.Length <= 160 && (value.StartsWith("codex:gpt-", StringComparison.Ordinal) || value.StartsWith("ollama:", StringComparison.Ordinal)));

    private static string RequestId(JsonElement args) => Guid.TryParse(Text(args, "request_id"), out var id)
        ? id.ToString("N") : throw new ArgumentException("对话请求编号无效。");

    private static string Language(string value) => value == "ja" ? "ja" : "zh";

    internal static bool HasKana(string text) =>
        text.Any(c => c is >= '\u3041' and <= '\u3096' or >= '\u30a1' and <= '\u30fa' or >= '\uff66' and <= '\uff9d');

    private static string DetectLanguage(string text) => HasKana(text) ? "ja" : "zh";

    private static string Limit(string value, int max) => value.Length <= max ? value : value[..(char.IsHighSurrogate(value[max - 1]) ? max - 1 : max)];
}
