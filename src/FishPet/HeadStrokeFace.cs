namespace FishPet;

internal readonly record struct HeadStrokeFace(int Level, int Side, bool OnFace, bool LongStroke,
    double Melt, double Blush, double MouthOpening)
{
    private const double OpeningDelay = .15;
    private const double OpeningTime = 1.8;
    private const double LongReleaseTime = 8;
    private const double DisgustAfter = 5;
    internal const string NegativeReleaseMood = "摸太久·黑脸";
    internal const string MeltReleaseMood = "摸后融化";
    private static readonly double[] PreviewMouthTimes = [0, .4, .75, 1.1, 1.45, OpeningDelay + OpeningTime, 2.5];
    private static readonly string[] FamiliarReleaseMoods = ["摸后失落", "摸后不舍", "摸后腻歪"];
    private static readonly (double BriefAnger, double LongAnger, string? AngryMood, string[] OtherMoods)[] Releases =
    [
        (.04, .75, "摸太久生气", ["摸后嫌弃", "摸后嫌弃", "摸后放松"]),
        (.02, .45, "摸太久生气", ["摸后别扭", "摸后放松", "摸后放松"]),
        (0, 0, null, FamiliarReleaseMoods),
        (.50, 0, "没摸够不满", FamiliarReleaseMoods),
        (.75, 0, "没摸够生气", FamiliarReleaseMoods)
    ];

    private static readonly (double BlushAfter, bool SqueezedEyes, (string Id, double Value)[] Parameters)[] Looks =
    [
        (5, false, [("EyeOpen", .88), ("BrowInnerUp", .18), ("ParamMouthForm", -.65),
                    ("MouthX", .54), ("Mouthpucker", .23), ("MouthSmile_L", .23),
                    ("ParamMouthOpenY", .18), ("JawOpen", .08)]),
        (3.5, false, [("EyeOpen", 1.05), ("BrowInnerUp", .37), ("ParamMouthOpenY", .55),
                      ("JawOpen", .29), ("MouthFunnel", .48), ("MouthPuckerWiden", .28)]),
        (2.4, true, [("ParamMouthForm", -.25), ("ParamMouthOpenY", .69), ("JawOpen", .38),
                     ("MouthFunnel", .4), ("MouthPuckerWiden", .3)]),
        (1.4, true, [("ParamMouthForm", -.75), ("MouthX", .56), ("Mouthpucker", .44),
                     ("CheeckPuff", .38), ("MouthSmile_L", .26), ("ParamMouthOpenY", .22), ("JawOpen", .1)]),
        // 羁绊沿用原版摸头的笑眼和微笑，嘴随着抚摸轻轻张开。
        (.7, false, [("EyeOpen", .45), ("EyeSmile", 1), ("ParamMouthForm", 1),
                     ("ParamMouthOpenY", .18), ("JawOpen", .08)])
    ];

    internal static HeadStrokeFace At(int level, int side, bool onFace, double seconds, double melt)
    {
        if (level == 0) return new(level, side, onFace, seconds >= DisgustAfter, 0, 0, 0);
        double timeAfter = seconds - Looks[level - 1].BlushAfter;
        double blush = timeAfter >= 0 ? Math.Min(1, .25 + timeAfter * .42) : 0;
        if (melt >= 1) blush = 1;
        // 开口按有效抚摸计时，量化到 60 档以限制每秒发送的参数变化。
        double opening = Math.Round(OpeningAt(seconds) * 60) / 60;
        return new(level, side, onFace, seconds >= 8, melt, Math.Round(blush * 40) / 40, opening);
    }

    internal (Dictionary<string, double> Parameters, string[] Expressions) Build()
    {
        if (Level == 0)
        {
            var wary = new Dictionary<string, double>
            {
                ["EyeOpen"] = LongStroke ? .47 : OnFace ? .58 : .71,
                ["ParamMouthForm"] = LongStroke ? -.98 : -.72,
                ["MouthX"] = .30,
                ["ParamBrowLY"] = LongStroke ? -.82 : -.41,
                ["ParamBrowRY"] = LongStroke ? -.82 : -.35,
                ["LeanInput"] = LongStroke ? 8 : 0
            };
            if (LongStroke)
            {
                wary["ParamBrowLAngle"] = -.85;
                wary["ParamBrowRAngle"] = -.85;
                wary["post:ParamBrowLAngleSmoothing"] = -.68;
                wary["post:ParamBrowRAngleSmoothing"] = -.68;
            }
            return (wary, LongStroke ? ["黑脸"] : []);
        }
        var look = Looks[Level - 1];
        bool originalSmile = Level == Looks.Length;
        var parameters = new Dictionary<string, double>(look.Parameters.Length + 3);
        foreach (var (id, value) in look.Parameters)
        {
            double amount = OpensWithMouth(id) ? value * MouthOpening : value;
            parameters[id] = id is "EyeOpen" or "EyeSmile" ? amount : amount * (1 - Melt);
        }
        bool squeeze = look.SqueezedEyes && Melt < .35;
        if (Melt > 0)
        {
            // 17–20 秒渐渐收起这档嘴型，接到原有的前倾、闭眼笑。
            parameters["ParamMouthForm"] = parameters.GetValueOrDefault("ParamMouthForm") + Melt;
            parameters["LeanInput"] = -17 * Melt;
        }
        if (!squeeze)
        {
            double lid = parameters.GetValueOrDefault("EyeOpen", .45) * (1 - Melt);
            if (OnFace) lid *= originalSmile ? 0 : .9 - .55 * Melt;
            parameters["EyeOpen"] = lid;
            if (Side != 0) parameters[Side > 0 ? "ParamEyeLOpen" : "ParamEyeROpen"] = originalSmile ? 0 : .76 * (1 - Melt);
            if (Melt > 0 && !originalSmile) parameters["EyeSmile"] = Melt;
        }
        if (LongStroke)
        {
            parameters["BrowInnerUp"] = .5;
            if (originalSmile) parameters["MouthPressLipOpen"] = .4;
        }
        var expressions = new List<string>(2);
        if (squeeze) expressions.Add("＞＜");
        if (Blush > 0) expressions.Add("脸红");
        return (parameters, expressions.ToArray());
    }

    private static bool OpensWithMouth(string id) =>
        id is "ParamMouthOpenY" or "JawOpen" or "MouthFunnel" or "MouthPuckerWiden" or "MouthPressLipOpen";

    private static double OpeningAt(double seconds)
    {
        double progress = Math.Clamp((seconds - OpeningDelay) / OpeningTime, 0, 1);
        return progress * progress * (3 - 2 * progress);
    }

    internal static double ReleaseAngerChance(int level, double seconds)
    {
        if (level == 0) return seconds >= DisgustAfter ? 1 : 0;
        var release = Releases[level - 1];
        double progress = Math.Clamp((seconds - 1) / (LongReleaseTime - 1), 0, 1);
        return release.BriefAnger + (release.LongAnger - release.BriefAnger) * progress;
    }

    internal static (string Mood, string Key) ReleaseAt(int level, double seconds, double roll, string? meltLine = null)
    {
        if (level == 0) return seconds >= DisgustAfter ? (NegativeReleaseMood, "摸太久生气") : ("摸后嫌弃", "摸后嫌弃");
        var release = Releases[level - 1];
        // 慢慢摸也能触发融化，达到彩蛋时按长摸计算生气概率。
        if (roll < ReleaseAngerChance(level, meltLine == null ? seconds : LongReleaseTime) && release.AngryMood is { } angry)
            return (angry, angry);
        if (meltLine != null) return (MeltReleaseMood, meltLine);
        string mood = release.OtherMoods[seconds >= LongReleaseTime ? 2 : seconds >= 3 ? 1 : 0];
        string key = mood switch
        {
            "摸后失落" => "抚摸头1",
            "摸后不舍" => "抚摸头2",
            "摸后腻歪" => "抚摸头3",
            _ => mood
        };
        return (mood, key);
    }

    internal static bool HoldsReleaseMouth(string? mood) =>
        mood is NegativeReleaseMood or "摸后嫌弃" or "摸后别扭" or "摸后放松" or "摸太久生气" or "没摸够不满" or "没摸够生气";

    internal static Mood Preview(int level)
    {
        var stroke = At(level, side: 0, onFace: level == Looks.Length, seconds: 2.5, melt: 0);
        var (parameters, expressions) = stroke.Build();
        var pose = Behavior.Moods["摸头"];
        var mouthMotion = parameters.Where(p => OpensWithMouth(p.Key)).Select(p =>
        {
            var keys = PreviewMouthTimes.Select(time => FormattableString.Invariant($"{time:0.##}:{p.Value * OpeningAt(time):0.###}"));
            return (p.Key, string.Join(' ', keys.Append(FormattableString.Invariant($"{pose.Seconds:0.##}:0"))));
        });
        return pose with
        {
            Expressions = expressions,
            Parameters = parameters.Select(p => (p.Key, OpensWithMouth(p.Key) ? 0 : p.Value)).ToArray(),
            Motion = [.. pose.Motion ?? [], .. mouthMotion],
            BlushStrength = stroke.Blush
        };
    }
}
