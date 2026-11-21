using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Threading;

namespace FishPet;

// 亲密度、番茄钟、连续工作提醒接到她身上（本机加，2026-11-08）。算分和计时在 Affection.cs，这里只管什么时候记、什么时候演。
// 要说的话都写在 events.txt（早安夸奖、熬夜念叨、想你了、升级、降级、番茄开始、番茄结束、休息结束、该休息），改台词不用改代码。
internal sealed partial class PetWindow
{
    private static string AffectionFile => Path.Combine(Path.GetDirectoryName(Path.GetFullPath(Places.SettingsFile))!, "affection.json");
    private Affection affection = Affection.Load(AffectionFile);
    private readonly FocusTimer focus = new();
    private readonly DispatcherTimer focusClock = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly WorkStreak streak = new();
    private readonly MediaPlayer chimePlayer = new();
    private DateTime nextPendingLook;

    // 用户本人有动静（点她、拖她、点托盘、发消息……）。
    private void BondSeen()
    {
        if (test) return;
        int level = affection.Level;
        foreach (var change in affection.Seen(DateTime.Now))
            QueueAffectionReaction(change.Kind, change.LevelBefore);
        UpdateAffectionReactions(level);
        affection.Save(AffectionFile);
    }

    private void BondGain(string kind, bool queueReaction = true)
    {
        if (test) return;
        int level = affection.Level;
        affection.Apply(kind, DateTime.Now);
        UpdateAffectionReactions(level);
        affection.Save(AffectionFile);
        if (queueReaction) QueueAffectionReaction(kind, level);
    }

    // 摸头彩蛋（20 秒）：「融化」一天一次 +3；当天用过了就退回普通摸头 +1（还是 60 秒一次）。
    private void BondMelt()
    {
        if (test) return;
        var now = DateTime.Now;
        int level = affection.Level;
        var (_, perDay) = affection.Rule(level, "融化");
        int used = affection.Day == Affection.DayOf(now).ToString("yyyy-MM-dd") ? affection.Counts.GetValueOrDefault("融化") : 0;
        bool exhausted = perDay > 0 && used >= perDay;
        if (exhausted)
        {
            if (DateTime.UtcNow - lastStrokeGain <= TimeSpan.FromSeconds(60)) return;
            affection.Apply("摸头", now);
        }
        else affection.Apply("融化", now);
        UpdateAffectionReactions(level);
        lastStrokeGain = DateTime.UtcNow;
        affection.Save(AffectionFile);
    }

    private void UpdateAffectionReactions(int previousLevel)
    {
        if (previousLevel != affection.Level)
        {
            behavior.ResetChestPokes();
            if (affection.Level < Affection.Levels.Length && behavior.Showing == Behavior.HeartEyesChestPoke)
                behavior.Interrupt();
        }
        if (NegativeAffection && previousLevel != 0)
        {
            Send("exp 脸红 0");
            if (positiveTouchSpeechToken == mutterToken) YieldMutter();
            RegradeActiveStroke();
        }
        else if (!NegativeAffection && previousLevel == 0)
        {
            SetNegativeIdleFace(false, default);
            if (settings.Outfit.Contains("脸红") || panelRaw.Contains("脸红") || body.Overlays.Contains("脸红"))
                Send("exp 脸红 1");
        }
        SyncOffendedPose();
    }

    private const int OffendedPoseBelow = -40;

    private static readonly (string Id, double Value)[] OffendedPoseParameters =
    [
        // 黑脸与互斥的表情开关。眼镜等穿戴走原来的参数。
        ("key1", 1), ("key2", 0), ("key3", 0), ("key4", 0), ("key5", 0), ("key6", 0), ("key7", 0), ("key13", 0),

        // 侧目、压眉、抿嘴。最后再覆盖五官的物理输出，避免笑、惊吓或口型改变表情。
        ("ParamEyeLOpen", .48), ("ParamEyeROpen", .41), ("EyeSmile", 0),
        ("ParamEyeBallX", -.35), ("ParamEyeBallY", -.13), ("PupilY", 0),
        ("LEyeSquint", .42), ("REyeSquint", .49),
        ("ParamBrowLY", -.68), ("ParamBrowRY", -.61), ("BrowInnerUp", 0),
        ("ParamBrowLAngle", -.77), ("ParamBrowRAngle", -.73),
        ("ParamBrowLAngleSmoothing", -.72), ("ParamBrowRAngleSmoothing", -.71),
        ("ParamBrowLY2", -.51), ("ParamBrowRY2", -.45), ("ParamBrowLY3", -.46), ("ParamBrowRY3", -.41),
        ("ShockL", 0), ("ShockR", 0), ("Press", 0), ("Emotion2", 0),
        ("LEyes1", 0), ("LEyes2", 0), ("REyes1", 0), ("REyes2", 0),
        ("LEyeLashX1", 0), ("LEyeLashX2", 0), ("REyeLashX1", 0), ("REyeLashX2", 0),
        ("LEyeball1", 0), ("LEyeball2", 0), ("LEyeball3", 0), ("REyeball1", 0), ("REyeball2", 0), ("REyeball3", 0),
        ("LHighLight1", 0), ("LHighLight2", 0), ("LHighLight3", 0), ("RHighLight1", 0), ("RHighLight2", 0), ("RHighLight3", 0),
        ("LParamGlowA", 0), ("RParamGlowA", 0), ("LParamGlowB", 0), ("RParamGlowB", 0), ("RParamGlowC", 0),
        ("ParamMouthForm", -.94), ("ParamMouthOpenY", 0), ("JawOpen", 0), ("MouthX", .17),
        ("MouthPressLipOpen", -.35), ("Mouthpucker", 0), ("MouthFunnel", 0), ("MouthPuckerWiden", 0),
        ("MouthSmile_L", 0), ("MouthSmile_R", 0), ("MouthRollUpper", 0), ("MouthRollLower", 0), ("TongueOut", 0),
        ("CheeckPuff", 0), ("CheeckPuff2", 0), ("CheeckPuffR", 0), ("CheeckPuffL", 0),
        ("CheeksWiggleX", 0), ("CheeksWiggleY", 0), ("CheeksWiggleXL", 0), ("CheeksWiggleXR", 0),
        ("LipPhysics1", 0), ("LipPhysics2", 0), ("LipPhysics3", 0),
        ("ParamTongueUpDown", 0), ("ParamTongueWidth", 0),
        // 眉毛和眼睑也会带起耳朵，遮住笑脸时一起压住这组反应。
        ("LControl", -.24), ("RControl", -.24),
        ("LEar1", 0), ("LEar2", 0), ("LEar3", 0), ("REar1", 0), ("REar2", 0), ("REar3", 0),

        // 原模型的单手护胸位置。
        ("Movement2", 1), ("Param56", 1), ("Param57", 0),
        ("Param62", .034), ("Param59", .476), ("Param60", .86), ("Param61", .002),
        ("Movement3", 0), ("Param40", 0), ("Param36", 0), ("Param39", 0),
        ("Param41", 0), ("Param42", 0), ("Param43", 0)
    ];
    private bool offendedPose;

    private void SyncOffendedPose()
    {
        bool show = affection.Points < OffendedPoseBelow;
        if (!ready || player is not { } renderer || show == offendedPose) return;
        offendedPose = show;
        foreach (var (id, value) in OffendedPoseParameters)
            renderer.Send(show ? FormattableString.Invariant($"set pose:{id} {value:0.###}") : $"clear pose:{id}");
    }

    private static readonly (string Id, double Value)[] NegativeIdleFeatures =
    [
        ("EyeOpen", .57), ("LEyeSquint", .23), ("REyeSquint", .31),
        ("ParamBrowLY", -.44), ("ParamBrowRY", -.38), ("ParamBrowLAngle", -.48), ("ParamBrowRAngle", -.41),
        ("post:ParamBrowLAngleSmoothing", -.36), ("post:ParamBrowRAngleSmoothing", -.29),
        ("ParamEyeBallX", -.26), ("ParamEyeBallY", -.12), ("ParamMouthForm", -.72), ("MouthX", .19)
    ];

    private bool negativeIdleFace;
    private DateTime? negativeIdleShadeUntil;

    private static bool OverridesIdleFace(string command) =>
        command.StartsWith("set ", StringComparison.Ordinal) || command.StartsWith("live ", StringComparison.Ordinal) ||
        command.StartsWith("anim ", StringComparison.Ordinal) || command.StartsWith("animloop ", StringComparison.Ordinal) ||
        command.StartsWith("motion ", StringComparison.Ordinal) || command.StartsWith("clear ", StringComparison.Ordinal) ||
        command.StartsWith("exp 黑脸 ", StringComparison.Ordinal) || command.StartsWith("expamount 黑脸 ", StringComparison.Ordinal) ||
        command == "clearall";

    private void SyncNegativeIdleFace(DateTime now)
    {
        bool resting = NegativeAffection && ready && !exiting && !standby && !hiding && !hiddenForFullscreen &&
            !dragging && !rightDragging && !stroking && !behavior.Touching && afterStroke == null && !Dancing &&
            behavior.Showing == null && !body.Posing && !panelHold && silent == null && !textIdleActive &&
            !PlayingImportedCombination && !coaxAutoReturn && !speech.IsSaying && !speech.IsShowingReply && !mutterSpeech.IsSaying;
        SetNegativeIdleFace(resting, now);
    }

    private void SetNegativeIdleFace(bool show, DateTime now)
    {
        if (player is not { } renderer) return;
        if (show != negativeIdleFace)
        {
            negativeIdleFace = show;
            foreach (var (id, value) in NegativeIdleFeatures)
                renderer.Send(show ? FormattableString.Invariant($"set {id} {value:0.###}") : $"clear {id}");
            if (show && !offendedPose)
            {
                negativeIdleShadeUntil = now.AddSeconds(3);
                renderer.Send("expamount 黑脸 0.65");
            }
        }
        if (negativeIdleShadeUntil is { } until && (!show || now >= until))
        {
            negativeIdleShadeUntil = null;
            renderer.Send("exp 黑脸 0");
        }
    }

    private void QueueAffectionReaction(string kind, int level)
    {
        var cue = EffectiveAffectionRule(level, kind).Action;
        if (cue is { Length: > 0 } && affectionReactions.Count < 8 && !affectionReactions.Contains(cue))
            affectionReactions.Enqueue(cue);
    }

    // 外面的事件（hooks）：用户发了消息算聊天；定时任务自己开的会话（auto）不算用户在。返回现在该不该提醒“起来动一动”。
    private bool BondEvent(string name, bool auto)
    {
        if (auto || test) return false;
        BondSeen();
        if (name == "收到") BondGain("聊天");
        bool due = streak.Note(DateTime.UtcNow);
        return due && focus.Now is FocusTimer.Phase.None or FocusTimer.Phase.Alarm;
    }

    // 演 events.txt 里一段她自己要说的话（不经过冷却、不挑忙闲：调用的人自己看时机）。{等级} 换成现在的等级名。
    private bool PlayOwn(string name)
    {
        events.Load(Path.Combine(Places.Root, "events.txt"));
        if (!events.TryGet(name, out var def) || def.Lines.Length == 0) return false;
        var line = events.Choose(def, random);
        PlayEvent(line with { Text = line.Text.Replace("{等级}", affection.LevelName) });
        return true;
    }

    // 她闲着、露着面的时候，才把攒着的话说出来。
    private bool FreeToTalk => !standby && !hiding && !hiddenForFullscreen && !test && ready && !MutterBlocked && behavior.Showing == null && !body.Posing;

    // 桌宠收起或被全屏盖住时也要响铃，所以计时与立绘刷新分开。
    private void FocusTick()
    {
        var now = DateTime.UtcNow;
        bool visible = IsVisible && !standby && !hiding && !hiddenForFullscreen;
        switch (focus.Tick(now))
        {
            case FocusTimer.Turn.WorkDone:
                Chime(rising: true);
                behavior.Quiet = false;
                behavior.Activity("专注", false);
                BondGain("番茄");
                YieldMutter();
                if (visible) { PlayOwn("番茄结束"); RestSkitLater(); }
                break;
            case FocusTimer.Turn.BreakDone:
                Chime(rising: false);
                streak.Rested(now);
                if (visible) { YieldMutter(); PlayOwn("休息结束"); }
                break;
            case FocusTimer.Turn.DeadlineDone:
                behavior.Quiet = false;
                behavior.Activity("专注", false);
                Chime(rising: true);
                if (visible) { YieldMutter(); _ = SayMutter("专注时间到了，起来活动一下吧。"); DismissTimerBubble(mutterToken); }
                break;
            case FocusTimer.Turn.AlarmDone:
                Chime(rising: true);
                break;
        }
        if (focus.Now == FocusTimer.Phase.None) focusClock.Stop();
    }

    private async void DismissTimerBubble(int token)
    {
        await Task.Delay(4000);
        if (!exiting && token == mutterToken) mutterSpeech.Stop();
    }

    private void BondTick()
    {
        var now = DateTime.UtcNow;
        if (affectionReactions.Count > 0 && FreeToTalk && focus.Now != FocusTimer.Phase.Work)
        {
            var cue = affectionReactions.Dequeue();
            YieldMutter();
            PlayTouchCue(cue);
            return;
        }
        if (!CanInitiate || now < nextPendingLook || affection.Pending.Count == 0 || focus.Now == FocusTimer.Phase.Work || !FreeToTalk) return;
        nextPendingLook = now.AddSeconds(3);
        var said = affection.Pending[0];
        affection.Pending.RemoveAt(0);
        affection.Save(AffectionFile);
        if (PlayOwn(said)) initiativeSpeechToken = mutterToken;
    }

    // 专注陪伴正在进行（Work 段）：这时候只剩「摸她」和「工作区进展」会出声，
    // 嘀咕、固定闲聊、小剧场（连同语音）都停掉（本机加，2026-11-09）。
    internal bool FocusWork => focus.Now == FocusTimer.Phase.Work;

    // 专注期间也允许的工作事件：授权/等你（要你处理）、完成、出错、测试结果、该休息、番茄钟自己那几句。
    private static readonly HashSet<string> FocusEvents =
    [
        "授权", "等你", "完成", "长任务完成", "子任务完成", "出错", "编译失败", "测试通过", "该休息",
        "番茄开始", "番茄结束1分钟内", "番茄结束5分钟内", "番茄结束10分钟", "番茄结束20分钟", "番茄结束", "休息结束",
    ];

    internal static bool FocusEvent(string name) => FocusEvents.Contains(name);

    // 番茄结束那段话说完，休息里演一个平常的小剧场（不挑带条件、彩蛋的）。
    private async void RestSkitLater()
    {
        int token = mutterToken;
        await Task.Delay(7000);
        if (!CanInitiate || exiting || standby || focus.Now != FocusTimer.Phase.Break || speech.IsSaying) return;
        var plain = autoPlaySkits.Where(name => !skitConds.ContainsKey(name)).ToArray();
        if (plain.Length > 0 && (token == mutterToken || !textIdleActive))
        {
            PlaySkit(plain[random.Next(plain.Length)], force: true);
            if (SkitOn) initiativeSpeechToken = mutterToken;
        }
    }

    private object FocusCommand(string action, JsonElement args)
    {
        var now = DateTime.UtcNow;
        switch (action)
        {
            case "start":
                bool wasWorking = FocusWork;
                string? mode = args.TryGetProperty("mode", out var requestedMode) && requestedMode.ValueKind != JsonValueKind.String
                    ? null : Conversation.Text(args, "mode");
                switch (mode)
                {
                    case "":
                    case "countdown":
                        int count = args.TryGetProperty("minutes", out _) ? ReadFocusMinutes(args) : settings.FocusMinutes;
                        if (!SaveFocusMinutes(count)) return Tools.Text(new { error = "专注时长无法保存。" });
                        focus.Start(now, count);
                        break;
                    case "stopwatch":
                        focus.StartStopwatch(now);
                        break;
                    case "deadline":
                    case "alarm":
                        if (!TimeOnly.TryParseExact(Conversation.Text(args, "time"), ["H:mm", "HH:mm"], CultureInfo.InvariantCulture,
                                DateTimeStyles.None, out var time))
                            throw new ArgumentException("请输入有效的 24 小时时刻，例如 18:30。");
                        DateTime deadline;
                        try { deadline = FocusTimer.NextTime(now, time, TimeZoneInfo.Local); }
                        catch (ArgumentException) { return Tools.Text(new { error = "本地时钟会跳过这个时刻，请另选一个时间。" }); }
                        focus.StartAt(now, deadline, alarm: mode == "alarm");
                        break;
                    default:
                        throw new ArgumentException("请选择正计时、倒计时、定时结束或闹钟。");
                }
                if (FocusWork)
                {
                    Wake();
                    YieldMutter();
                    behavior.Quiet = true;
                    behavior.Activity("专注", true);
                    PlayOwn("番茄开始");
                }
                else if (wasWorking)
                {
                    behavior.Quiet = false;
                    behavior.Activity("专注", false);
                }
                focusClock.Start();
                break;
            case "stop":
                bool working = FocusWork;
                var spent = focus.Elapsed(now);
                focus.Stop(now);
                focusClock.Stop();
                if (working)
                {
                    behavior.Quiet = false;
                    behavior.Activity("专注", false);
                    if (!standby) { YieldMutter(); PlayOwn(spent < TimeSpan.FromMinutes(1) ? "番茄结束1分钟内" : spent < TimeSpan.FromMinutes(5) ? "番茄结束5分钟内" : spent < TimeSpan.FromMinutes(10) ? "番茄结束10分钟" : "番茄结束20分钟"); }
                }
                break;
            case "duration":
                if (!SaveFocusMinutes(ReadFocusMinutes(args))) return Tools.Text(new { error = "专注时长无法保存。" });
                break;
            default:
                throw new ArgumentException("action 请选择 start（开始）、stop（结束）或 duration（设置时长）。");
        }
        return Tools.Text(FocusState());
    }

    private static int ReadFocusMinutes(JsonElement args)
    {
        if (!args.TryGetProperty("minutes", out var duration) || duration.ValueKind != JsonValueKind.Number
            || !duration.TryGetInt32(out int minutes) || minutes is < FocusTimer.MinMinutes or > FocusTimer.MaxMinutes)
            throw new ArgumentException($"专注时长须为 {FocusTimer.MinMinutes} 到 {FocusTimer.MaxMinutes} 分钟。");
        return minutes;
    }

    private bool SaveFocusMinutes(int minutes)
    {
        int previous = settings.FocusMinutes;
        if (minutes == previous) return true;
        settings.FocusMinutes = minutes;
        if (settings.TrySave()) return true;
        settings.FocusMinutes = previous;
        return false;
    }

    private object FocusState()
    {
        var now = DateTime.UtcNow;
        return new
        {
            phase = focus.Now.ToString().ToLowerInvariant(),
            mode = focus.Kind.ToString().ToLowerInvariant(),
            seconds_left = (int)Math.Ceiling(focus.Left(now).TotalSeconds),
            seconds_total = focus.Now == FocusTimer.Phase.None ? settings.FocusMinutes * 60 : (int)focus.Length.TotalSeconds,
            seconds_elapsed = (int)focus.Elapsed(now).TotalSeconds,
            started_at = focus.Started == default ? (DateTime?)null : focus.Started,
            ends_at = focus.Ends == default ? (DateTime?)null : focus.Ends,
            minutes = settings.FocusMinutes,
            rounds_today = focus.RoundsToday,
        };
    }

    private object AffectionState() => new
    {
        points = affection.Points, min = Affection.Min, max = Affection.Max, level = affection.Level, name = affection.LevelName,
        from = affection.LevelFrom, next = affection.NextAt,
        next_name = affection.Level < Affection.Levels.Length ? Affection.Levels[affection.Level].Name : null,
        today = affection.TodayNet,
    };

    // ---- 提示音：自己合成两声柔和的铃（正弦波加一点泛音，快起慢落），不带任何音频文件 ----

    private static readonly Dictionary<bool, string> ChimeFiles = [];

    private void Chime(bool rising)
    {
        if (test) return;
        try
        {
            if (!ChimeFiles.TryGetValue(rising, out var file) || !File.Exists(file))
            {
                file = Path.Combine(Path.GetTempPath(), $"fishpet-chime-{(rising ? "up" : "down")}.wav");
                File.WriteAllBytes(file, ChimeSound.Wave(rising ? [880, 1318.5] : [1318.5, 987.8]));
                ChimeFiles[rising] = file;
            }
            chimePlayer.Open(new Uri(file));
            chimePlayer.Volume = 0.3; // 固定音量，不跟语音设置走（语音功能已去掉，2026-11-11）
            chimePlayer.Play();
        }
        catch (Exception error) { Places.Log("提示音放不出来：" + error.Message); }
    }
}
