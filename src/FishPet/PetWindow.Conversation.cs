using System.IO;
using System.Net.Http;
using System.Text.Json;

namespace FishPet;

internal sealed partial class PetWindow
{
    private Conversation? conversation;

    private async Task<object> ConversationCommand(string name, JsonElement args, CancellationToken token)
    {
        try
        {
            var link = conversation ??= new Conversation();
            switch (name)
            {
                case "conversation_status": return Tools.Text(await link.Status(settings.VoiceLanguage, token));
                case "conversation_send":
                    var sent = await link.Send(args, token);
                    BondSeen();
                    BondGain("聊天");
                    return Tools.Text(sent);
                case "conversation_cancel": return Tools.Text(await link.Cancel(args, token));
                case "conversation_transcribe": return Tools.Text(await link.Transcribe(args, token));
                case "conversation_present":
                    if (!args.TryGetProperty("presentation", out var presentation)) throw new ArgumentException("对话回复格式无效。");
                    bool requested = args.TryGetProperty("speak", out var speak) && speak.ValueKind == JsonValueKind.True;
                    (bool spoken, string? warning) result = default;
                    await InTurn(async () =>
                    {
                        result = await PresentConversationReply(presentation, requested, token);
                    }, token);
                    return Tools.Text(new { result.spoken, result.warning });
                default: throw new ArgumentException("没有这个对话命令。");
            }
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or TimeoutException or IOException or HttpRequestException or JsonException)
        {
            return Tools.Text(new { error = error is InvalidOperationException or TimeoutException or ArgumentException ? error.Message : "连接原有语音与对话服务失败。" });
        }
    }

    private async Task<(bool Spoken, string? Warning)> PresentConversationReply(
        JsonElement presentation, bool requested, CancellationToken token)
    {
        string tagged = Conversation.PresentationText(presentation, out var reply);
        token.ThrowIfCancellationRequested();
        bool prepare = VoiceOn && requested;
        var line = BeginConversationReply(tagged, prepare);
        if (!prepare) return (false, requested ? "语音已静音，已显示文字回复。" : null);

        var playback = PlaybackSettings;
        using var request = CancellationTokenSource.CreateLinkedTokenSource(token, speech.VoiceRequest(line));
        VoiceClip? recording = null;
        try
        {
            recording = await PrepareVoice(reply, Conversation.Text(presentation, "speech_text"),
                Conversation.Text(presentation, "speech_language"), request.Token);
            request.Token.ThrowIfCancellationRequested();
            if (exiting || playback != PlaybackSettings || !speech.IsAwaitingVoice(line)) return (false, null);
            if (!speech.FinishVoice(line, recording)) return (false, "这句暂时无法配音，已显示文字回复。");
            recording = null; // Speech owns the recording.
            return (true, null);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested && request.IsCancellationRequested)
        {
            return (false, null);
        }
        catch (Exception error) when (IsVoiceFailure(error))
        {
            return (false, error is InvalidOperationException or TimeoutException ? error.Message : "原有语音服务暂时无响应。");
        }
        finally
        {
            recording?.Dispose();
            speech.FinishVoice(line, null);
        }
    }
}
