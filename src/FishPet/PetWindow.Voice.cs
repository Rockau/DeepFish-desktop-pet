using System;
using System.ComponentModel;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace FishPet;

internal sealed partial class PetWindow
{
    private readonly RecordedVoice recordedVoice = RecordedVoice.Load(Path.Combine(Places.Root, "voice"));
    private readonly SemaphoreSlim voiceSynthesis = new(1);
    private CancellationTokenSource voiceSettingLifetime = new();
    private bool VoiceEnabled => settings.VoiceEnabled && settings.TtsEnabled;
    private bool VoiceOn => VoiceEnabled && settings.VoiceVolume > 0;
    private (bool Enabled, double Volume, string Language, CancellationToken Token) PlaybackSettings =>
        (VoiceEnabled, settings.VoiceVolume, settings.VoiceLanguage, voiceSettingLifetime.Token);

    private void InvalidatePreparedVoice()
    {
        var previous = voiceSettingLifetime;
        voiceSettingLifetime = new CancellationTokenSource();
        previous.Cancel();
        previous.Dispose();
    }

    // Keep the same built-in lines voiced. The old index selects lines while Fujita supplies the audio.
    private bool HasBuiltInVoice(string text) => VoiceOn && !exiting && recordedVoice.Find(text) != null;
    private bool CanVoiceMutter => !speech.IsSaying && lines.IsCompleted;

    private Task<double> SayMutter(string text, bool plainFace = false)
    {
        var line = new Line(text, (cue, spoken, tone) => body.Apply(cue, spoken, tone, plainFace));
        bool prepare = CanVoiceMutter && HasBuiltInVoice(line.Plain);
        mutterSpeech.Say(line, preparingVoice: prepare);
        return prepare ? AttachBuiltInVoice(mutterSpeech, line, ambient: true) : Task.FromResult(0d);
    }

    private async Task<double> AttachBuiltInVoice(Speech channel, Line line, bool ambient)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(channel.VoiceRequest(line));
        timeout.CancelAfter(TimeSpan.FromSeconds(18));
        var playback = PlaybackSettings;
        VoiceClip? clip = null;
        try
        {
            clip = await PrepareVoice(line.Plain, "", "", timeout.Token, opportunistic: true);
            if (clip == null || exiting || playback != PlaybackSettings || (ambient && !CanVoiceMutter)) return 0;
            if (!channel.FinishVoice(line, clip)) return 0;
            double seconds = clip.Seconds;
            clip = null; // Speech owns the recording.
            return seconds;
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested) { return 0; }
        catch (Exception error) when (IsVoiceFailure(error))
        {
            Places.Log("固定台词配音转为文字：" + error.GetType().Name);
            return 0;
        }
        finally
        {
            clip?.Dispose();
            channel.FinishVoice(line, null);
        }
    }

    private async Task<VoiceClip?> PrepareAgentVoice(string text, string spoken, CancellationToken token)
    {
        if (!VoiceOn || exiting) return null;
        if (Body.Strip(spoken.Length == 0 ? text : spoken).Trim().Length > 120) return null;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        try { return await PrepareVoice(text, spoken, "ja", timeout.Token); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return null; }
        catch (Exception error) when (IsVoiceFailure(error))
        {
            Places.Log("实时语音转为文字：" + error.GetType().Name);
            return null;
        }
    }

    private async Task<VoiceClip?> PrepareVoice(string text, string draft, string draftLanguage,
        CancellationToken token, bool opportunistic = false)
    {
        if (!VoiceOn || exiting) return null;
        string subtitle = Body.Strip(text).Trim();
        if (subtitle.Length == 0) return null;
        var playback = PlaybackSettings;
        using var request = CancellationTokenSource.CreateLinkedTokenSource(token, playback.Token);
        bool entered = false;
        try
        {
            entered = await voiceSynthesis.WaitAsync(opportunistic ? 0 : Timeout.Infinite, request.Token);
            if (!entered || exiting || playback != PlaybackSettings) return null;
            var wav = await (conversation ??= new Conversation()).Speak(subtitle, Body.Strip(draft).Trim(),
                draftLanguage, playback.Language, request.Token);
            if (exiting || playback != PlaybackSettings) return null;
            return await VoiceClip.LoadAsync(wav, request.Token, playback.Volume);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested && playback.Token.IsCancellationRequested)
        { return null; }
        finally { if (entered) voiceSynthesis.Release(); }
    }

    private static bool IsVoiceFailure(Exception error) => error is InvalidOperationException or TimeoutException
        or IOException or InvalidDataException or HttpRequestException or JsonException or ArgumentException or Win32Exception;

    private async Task SpeakBubbles(List<string> bubbles, string spoken,
        TaskCompletionSource started, CancellationToken token)
    {
        try
        {
            await InTurn(async () =>
            {
                var playback = PlaybackSettings;
                var next = PrepareAgentVoice(bubbles[0], spoken, token);
                for (int i = 0; i < bubbles.Count; i++)
                {
                    if (i > 0) await WaitForLine(CancellationToken.None);
                    var clip = await next;
                    try
                    {
                        if (i == 0) token.ThrowIfCancellationRequested();
                        if (exiting) throw new InvalidOperationException("你正在告别，收起之后再叫你出来。");
                        if (playback != PlaybackSettings)
                        {
                            clip?.Dispose();
                            clip = null;
                        }
                        SayLine(bubbles[i], recording: clip, voiceBuiltIn: false);
                        clip = null; // Speech owns the clip after starting the line.
                    }
                    finally { clip?.Dispose(); }

                    started.TrySetResult();
                    if (i + 1 < bubbles.Count)
                    {
                        playback = PlaybackSettings;
                        next = PrepareAgentVoice(bubbles[i + 1], "", CancellationToken.None);
                    }
                }
            }, token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { started.TrySetCanceled(token); }
        catch (Exception error)
        {
            if (!started.TrySetException(error)) Places.Log("后续语音气泡中止：" + error.GetType().Name);
        }
    }

    private Line BeginConversationReply(string taggedText, bool preparingVoice)
    {
        if (exiting) throw new InvalidOperationException("你正在告别，收起之后再叫你出来。");
        Wake();
        YieldMutter();
        relaxAt = null;
        panelHold = false;
        var reply = new Line(taggedText, body.Apply);
        StartFilming(reply.Plain);
        speech.BeginReply(reply, preparingVoice);
        return reply;
    }

    private async Task<object> Say(string text, string spoken, string audio, CancellationToken token)
    {
        if (exiting) throw new InvalidOperationException("你正在告别，收起之后再叫你出来。");
        Wake();
        YieldMutter();
        bool single = spoken.Length > 0 || audio.Length > 0;
        var bubbles = single ? new List<string> { text } : Line.Split(text);
        if (bubbles.Count == 0) throw new ArgumentException("请填写要说的话。");
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = SpeakBubbles(bubbles, spoken, started, token);
        await started.Task;
        var note = Heard(bubbles);
        if (audio.Length > 0) note += "配音统一使用藤田琴音。";
        if (!single && Line.HasSlashBreak(text)) note += "这句里的“／”或“ / ”我按换行分成了几个气泡；下次一句一行，直接换行，别写斜杠。";
        var said = Body.Strip(text);
        return Tools.Text(note.Length == 0 ? new { started = true, bubbles = bubbles.Count, said }
                : (object)new { started = true, bubbles = bubbles.Count, said, note });
    }
}
