namespace FishPet;

internal static class AffectionTouchReactions
{
    private const string TailBase = "post:Param_Angle_Rotation84";
    private const string TailBend = "post:Param_Angle_Rotation86";
    private static readonly (string On, string Off) HandToChest = ("默认手切举手", "举手切默认手");

    private static readonly Dictionary<string, (string Event, (string? Hover, string Click)[] Stages)> Regions = new()
    {
        ["head"] = ("摸头",
        [
            ("悬停头·黑脸", "点头部·黑脸"),
            ("悬停头·避开", "点头部·躲开"),
            ("悬停头·侧目", "点头部·惊讶"),
            ("悬停头·张望", "点头部·好奇"),
            ("悬停头·害羞", "点头部·害羞"),
            ("悬停头·等摸头", "摸头")
        ]),
        ["hand"] = ("摸手",
        [
            ("悬停手·留意", "点手·缩手"),
            ("悬停手·看一眼", "点手·迟疑"),
            ("悬停手·犹豫", "点手·局促"),
            ("悬停手·好奇", "点手·回应"),
            ("悬停手", "点手·牵一下"),
            ("悬停手·等你牵", "开心")
        ]),
        ["tail"] = ("摸尾巴",
        [
            ("悬停尾", "震惊"),
            ("悬停尾·避开", "点尾·缩一下"),
            ("悬停尾·提防", "点尾·怕痒"),
            ("悬停尾·回头", "点尾·痒得笑"),
            ("悬停尾·轻摆", "点尾·抿嘴笑"),
            ("悬停尾·摆给你看", "点尾·笑着摇尾")
        ]),
        ["chest"] = (Affection.ChestPokeEvent,
        [
            (null, "点胸口·厌恶"),
            (null, "点胸口·嫌弃"),
            (null, "点胸口·警觉"),
            (null, "点胸口·坏笑"),
            (null, "点胸口·害羞"),
            (null, "娇羞前倾")
        ])
    };

    internal static (string Event, string? Hover, string Click)? ForZone(string zone, int level)
    {
        if (!Regions.TryGetValue(zone, out var region)) return null;
        var (hover, click) = region.Stages[level];
        return (region.Event, hover, click);
    }

    internal static string? ClickForEvent(string name, int level)
    {
        foreach (var (zone, region) in Regions)
            if (region.Event == name) return ForZone(zone, level)?.Click;
        return null;
    }

    internal static IReadOnlyDictionary<string, Mood> Moods { get; } = new Dictionary<string, Mood>
    {
        ["悬停头·黑脸"] = new(["黑脸"], [("EyeOpen", .52), ("ParamMouthForm", -.86), ("ParamBrowLY", -.66), ("ParamBrowRY", -.66),
                                               ("ParamEyeBallX", -.55), ("MouthX", -.28)],
            2.6, [("FaceAngleX", "0:0 .32:-14 .63:-17 1.86:-15 2.6:0"), ("LeanInput", "0:0 .4:5 1.8:5 2.6:0")], Pushed: true),
        ["悬停头·避开"] = new([], [("EyeOpen", .68), ("ParamMouthForm", -.62), ("ParamEyeBallX", -.43), ("ParamBrowLY", -.38)],
            2.45, [("FaceAngleX", "0:0 .34:-12 .57:-13 1.72:-12 2.45:0"), ("FaceAngleZ", "0:0 .45:-5 1.71:-4 2.45:0")], Pushed: true),
        ["悬停头·侧目"] = new([], [("EyeOpen", .86), ("ParamMouthForm", -.28), ("ParamBrowLY", .22)],
            2.6, [("FaceAngleX", "0:0 .39:5 .82:-7 1.9:-6 2.6:0"), ("ParamEyeBallX", "0:0 .25:.47 .73:-.44 1.9:-.35 2.6:0")], Pushed: true),
        ["悬停头·张望"] = new([], [("EyeOpen", 1.04), ("BrowInnerUp", .5), ("ParamMouthOpenY", .16), ("MouthFunnel", .24)],
            3.35, [("FaceAngleX", "0:0 .42:12 1.0:-13 1.52:9 2.05:1 3.35:0"),
                   ("ParamEyeBallX", "0:0 .26:.63 .83:-.65 1.38:.46 1.94:0 3.35:0"),
                   ("FaceAngleZ", "0:0 .56:4 1.15:-5 2:6 2.62:5 3.35:0")], Pushed: true),
        ["悬停头·害羞"] = new(["脸红"], [("EyeOpen", .67), ("EyeSmile", .25), ("ParamMouthForm", .53), ("ParamEyeBallY", -.46),
                                              ("MouthPressLipOpen", .3), ("BrowInnerUp", .35)],
            3.5, [("FaceAngleX", "0:0 .45:6 .89:-9 2.47:-7 3.5:0"), ("FaceAngleY", "0:0 .75:-10 2.57:-9 3.5:0"),
                  ("FaceAngleZ", "0:0 .65:-8 2.55:-7 3.5:0")], Pushed: true, BlushStrength: .55),
        ["悬停头·等摸头"] = new(["脸红"], [("EyeSmile", 1), ("ParamMouthForm", .9), ("MouthPressLipOpen", .21)],
            4.3, [("LeanInput", "0:0 .28:-5 .78:-18 3.22:-18 4.3:0"),
                  ("FaceAngleY", "0:0 .28:-3 .78:-13 3.22:-13 4.3:0"),
                  ("FaceAngleZ", "0:0 .66:6 3.12:5 4.3:0"),
                  ("EyeOpen", "0:0 .22:0 .76:-1 3.3:-1 4.3:0"),
                  ("ViewSquash", "0:0 .78:.022 3.22:.022 4.3:0")], BlushStrength: .76),

        ["点头部·黑脸"] = new(["黑脸"], [("EyeOpen", .51), ("ParamMouthForm", -.95), ("ParamBrowLY", -.76), ("ParamBrowRY", -.76),
                                               ("ParamBrowLAngle", -.64), ("ParamBrowRAngle", -.64), ("ParamEyeBallX", -.6)],
            2.85, [("FaceAngleX", "0:0 .18:-17 .53:-13 1.92:-12 2.85:0"), ("LeanInput", "0:0 .19:10 .59:7 1.97:7 2.85:0")],
            Pushed: true, HoldMouth: true),
        ["点头部·躲开"] = new([], [("EyeOpen", .69), ("ParamMouthForm", -.76), ("ParamEyeBallX", -.45), ("MouthX", -.36)],
            2.55, [("FaceAngleX", "0:0 .2:-12 .58:-8 1.65:-7 2.55:0"), ("FaceAngleY", "0:0 .19:6 .63:3 2.55:0"),
                   ("LeanInput", "0:0 .24:7 1.62:5 2.55:0")], Pushed: true, HoldMouth: true),
        ["点头部·惊讶"] = new([], [("EyeOpen", 1.15), ("BrowInnerUp", .62), ("ParamMouthOpenY", .24), ("MouthFunnel", .37)],
            2.7, [("FaceAngleY", "0:0 .19:9 .53:3 1.5:0 2.7:0"), ("FaceAngleZ", "0:0 .28:7 1.52:4 2.7:0"),
                  ("LeanInput", "0:0 .21:4 .54:2 1.4:0 2.7:0")], Pushed: true),
        ["点头部·好奇"] = new([], [("EyeOpen", .86), ("EyeSmile", .42), ("ParamMouthForm", .58), ("ParamEyeBallX", .34),
                                       ("ParamBrowLY", .46)],
            2.9, [("FaceAngleX", "0:0 .24:7 .65:4 2:4 2.9:0"), ("FaceAngleZ", "0:0 .34:8 2:6 2.9:0"),
                  ("LeanInput", "0:0 .49:-4 1.91:-3 2.9:0")], Pushed: true),
        ["点头部·害羞"] = new(["脸红"], [("EyeOpen", .63), ("EyeSmile", .53), ("ParamMouthForm", .65), ("ParamEyeBallY", -.41),
                                             ("MouthPressLipOpen", .39)],
            3.1, [("FaceAngleX", "0:0 .23:5 .62:-8 2.28:-7 3.1:0"), ("FaceAngleY", "0:0 .38:-9 2.3:-7 3.1:0"),
                  ("FaceAngleZ", "0:0 .45:-8 2.27:-6 3.1:0")], Pushed: true, BlushStrength: .72),

        ["悬停手·留意"] = new([], [("EyeOpen", .79), ("ParamEyeBallX", .44), ("ParamEyeBallY", -.37), ("ParamMouthForm", -.43)],
            2.45, [("FaceAngleX", "0:0 .44:6.5 1.68:4 2.45:0"), ("FaceAngleY", "0:0 .48:-6 1.65:-5 2.45:0")], Pushed: true),
        ["悬停手·看一眼"] = new([], [("EyeOpen", .94), ("ParamEyeBallX", .35), ("ParamEyeBallY", -.25), ("ParamMouthForm", -.19)],
            2.35, [("FaceAngleX", "0:0 .46:-3.5 1.55:-3 2.35:0")], Pushed: true),
        ["悬停手·犹豫"] = new([], [("EyeOpen", .94), ("BrowInnerUp", .4), ("ParamEyeBallX", .35), ("ParamMouthForm", -.18), ("MouthX", .2)],
            2.65, [("FaceAngleZ", "0:0 .56:-6 1.9:-4 2.65:0"), ("ParamEyeBallX", "0:0 .72:0 1.13:-.4 1.9:-.4 2.65:0")], Pushed: true),
        ["悬停手·好奇"] = new([], [("EyeOpen", 1.04), ("ParamBrowLY", .4), ("ParamMouthOpenY", .18), ("MouthFunnel", .26)],
            2.7, [("FaceAngleZ", "0:0 .55:8 2.04:5 2.7:0"), ("LeanInput", "0:0 .65:-4 2:-3 2.7:0")], Pushed: true),
        ["悬停手·等你牵"] = new(["脸红"], [("EyeOpen", .25), ("EyeSmile", 1), ("ParamMouthForm", .85), ("FaceAngleZ", 4)],
            3.05, [("LeanInput", "0:0 .55:-6 2.2:-5 3.05:0"), ("FaceAngleY", "0:0 .56:3 2.2:2 3.05:0")], HandToChest, BlushStrength: .58),

        ["点手·缩手"] = new([], [("EyeOpen", 1.05), ("ParamMouthForm", -.68), ("BrowInnerUp", .4), ("ParamBrowLY", -.4)],
            2.5, [("LeanInput", "0:0 .17:9 .5:7 1.66:4 2.5:0"), ("FaceAngleX", "0:0 .22:-11 .66:-6 2.5:0")],
            HandToChest, Pushed: true, HoldMouth: true),
        ["点手·迟疑"] = new([], [("EyeOpen", .88), ("BrowInnerUp", .33), ("ParamMouthForm", -.44), ("ParamEyeBallY", -.32)],
            2.55, [("LeanInput", "0:0 .27:5 .62:3 1.72:3 2.55:0"), ("FaceAngleX", "0:0 .31:-6 1.63:-4 2.55:0")],
            Pushed: true, HoldMouth: true),
        ["点手·局促"] = new([], [("EyeOpen", .82), ("BrowInnerUp", .62), ("ParamEyeBallY", -.41), ("MouthX", .4), ("ParamMouthForm", -.37)],
            2.7, [("FaceAngleY", "0:0 .28:-8 1.88:-5 2.7:0"), ("FaceAngleZ", "0:0 .36:-6 1.83:-3 2.7:0")], Pushed: true),
        ["点手·回应"] = new([], [("EyeOpen", .67), ("EyeSmile", .72), ("ParamMouthForm", .66), ("ParamMouthOpenY", .23)],
            2.9, [("FaceAngleY", "0:0 .28:-6 .57:2 .92:-5 1.26:0 2.9:0"), ("FaceAngleZ", "0:0 .65:4 2:3 2.9:0")], HandToChest),
        ["点手·牵一下"] = new(["脸红"], [("EyeOpen", .37), ("EyeSmile", .85), ("ParamMouthForm", .82), ("ParamMouthOpenY", .21)],
            3.25, [("LeanInput", "0:0 .38:-7 2.5:-5 3.25:0"), ("FaceAngleZ", "0:0 .42:-8 2.35:-6 3.25:0")], HandToChest, BlushStrength: .7),

        ["悬停尾·避开"] = new([], [("EyeOpen", .86), ("ParamMouthForm", -.23), ("ParamEyeBallX", .47), ("ParamEyeBallY", -.33)],
            2.45, [("FaceAngleX", "0:0 .44:4 1.68:3 2.45:0"), (TailBend, "0:0 .49:-2.5 1.61:-2 2.45:0")], Pushed: true),
        ["悬停尾·提防"] = new([], [("EyeOpen", .73), ("LEyeSquint", .25), ("REyeSquint", .25), ("ParamEyeBallX", .63),
                                     ("ParamEyeBallY", -.45), ("ParamMouthForm", -.45)],
            2.6, [("FaceAngleX", "0:0 .4:7 1.8:6 2.6:0"), (TailBend, "0:0 .43:-4 1.85:-3 2.6:0")], Pushed: true),
        ["悬停尾·回头"] = new([], [("EyeOpen", 1), ("ParamBrowLY", .45), ("ParamEyeBallX", .6), ("ParamEyeBallY", -.42), ("ParamMouthOpenY", .14)],
            2.7, [("FaceAngleX", "0:0 .45:10 1.9:7 2.7:0"), ("FaceAngleZ", "0:0 .5:6 1.94:4 2.7:0"), (TailBend, "0:0 .65:4 1.5:-2 2.7:0")], Pushed: true),
        ["悬停尾·轻摆"] = new([], [("EyeOpen", .48), ("EyeSmile", .8), ("ParamMouthForm", .6), ("ParamEyeBallY", -.24)],
            2.95, [("FaceAngleZ", "0:0 .58:5 2.18:4 2.95:0"), (TailBase, "0:0 .58:3 1.42:-3 2.2:2 2.95:0"),
                   (TailBend, "0:0 .78:5 1.6:-4 2.44:3 2.95:0")]),
        ["悬停尾·摆给你看"] = new(["脸红"], [("EyeOpen", .16), ("EyeSmile", 1), ("ParamMouthForm", .87)],
            3.15, [("FaceAngleZ", "0:0 .6:-5 1.48:3 2.53:-3 3.15:0"), (TailBase, "0:0 .52:5 1.35:-4 2.27:4 3.15:0"),
                   (TailBend, "0:0 .69:7 1.52:-6 2.42:5 3.15:0")], BlushStrength: .53),

        ["点尾·缩一下"] = new([], [("EyeOpen", 1.03), ("BrowInnerUp", .43), ("ParamMouthForm", -.44), ("ParamEyeBallY", -.25)],
            2.45, [("LeanInput", "0:0 .22:5 .62:3 2.45:0"), ("FaceAngleX", "0:0 .25:6 .8:3 2.45:0"),
                   (TailBase, "0:0 .17:-4 .52:2 1.18:0"), (TailBend, "0:0 .28:-4.5 .66:2.5 1.5:0")], Pushed: true),
        ["点尾·怕痒"] = new(["＞＜"], [("ParamMouthForm", -.72), ("MouthX", .3), ("BrowInnerUp", .48)],
            2.65, [("FaceAngleZ", "0:0 .15:-10 .49:6 .85:-3 1.48:0"), ("LeanInput", "0:0 .18:9 .7:4 2.65:0"),
                   (TailBase, "0:0 .16:-6 .55:4 1.05:-2 1.68:0"), (TailBend, "0:0 .24:-8 .62:5 1.13:-3 1.86:0")], Pushed: true, HoldMouth: true),
        ["点尾·痒得笑"] = new([], [("EyeOpen", .4), ("EyeSmile", .9), ("ParamMouthForm", .65), ("ParamMouthOpenY", .39), ("JawOpen", .2)],
            2.85, [("FaceAngleZ", "0:0 .18:-6 .48:6 .86:-4 1.65:0"), (TailBase, "0:0 .19:-4 .54:5 .94:-3 1.8:0"),
                   (TailBend, "0:0 .28:-6 .67:7 1.07:-4 2.07:0")], Pushed: true),
        ["点尾·抿嘴笑"] = new(["脸红"], [("EyeOpen", .32), ("EyeSmile", .92), ("ParamMouthForm", .83), ("MouthPressLipOpen", .22)],
            3.1, [("FaceAngleY", "0:0 .27:-5 .64:2 1.47:0 3.1:0"), (TailBase, "0:0 .27:3 .78:-5 1.44:4 2.34:0"),
                  (TailBend, "0:0 .4:4 .96:-6 1.61:6 2.65:0")], BlushStrength: .62, HoldMouth: true),
        ["点尾·笑着摇尾"] = new(["脸红"], [("EyeOpen", 0), ("EyeSmile", 1), ("ParamMouthForm", 1), ("ParamMouthOpenY", .38), ("JawOpen", .2)],
            3.4, [("FaceAngleZ", "0:0 .32:-6 .98:5 1.68:-4 2.42:3 3.4:0"), ("LeanInput", "0:0 .55:-5 2.4:-4 3.4:0"),
                  (TailBase, "0:0 .3:-5 .79:6 1.34:-6 1.98:5 2.65:-3 3.4:0"), (TailBend, "0:0 .42:-7 .93:8 1.49:-8 2.14:7 2.79:-4 3.4:0")], BlushStrength: .78),

        ["点胸口·厌恶"] = new(["黑脸"], [("EyeOpen", .38), ("LEyeSquint", .42), ("REyeSquint", .48), ("ParamMouthForm", -1),
                                    ("MouthX", -.29), ("ParamEyeBallX", -.32), ("ParamEyeBallY", -.33),
                                    ("ParamBrowLY", -.86), ("ParamBrowRY", -.86), ("ParamBrowLAngle", -1), ("ParamBrowRAngle", -1),
                                    ("post:ParamBrowLAngleSmoothing", -.78), ("post:ParamBrowRAngleSmoothing", -.78)],
            3.2, [("LeanInput", "0:0 .22:12 .68:9 2.38:8 3.2:0"), ("FaceAngleX", "0:0 .29:-9 .7:-8 2.22:-7 3.2:0"),
                  ("FaceAngleY", "0:0 .25:-9 .67:-12 2.26:-11 3.2:0")],
            HandToChest, Pushed: true, HoldMouth: true),
        ["点胸口·嫌弃"] = new([], [("EyeOpen", .64), ("ParamMouthForm", -.75), ("MouthX", -.26),
                                    ("ParamEyeBallX", -.39), ("ParamBrowLY", -.52), ("ParamBrowRY", -.35)],
            2.8, [("LeanInput", "0:0 .3:7 1.94:5 2.8:0"), ("FaceAngleX", "0:0 .38:-6 1.95:-5 2.8:0"),
                  ("FaceAngleY", "0:0 .42:-6 1.92:-4 2.8:0")], HandToChest, Pushed: true, HoldMouth: true),
        ["点胸口·警觉"] = new(["黑脸"], [("EyeOpen", .82), ("ParamBrowLY", -.5), ("ParamBrowRY", -.36), ("ParamMouthForm", -.64),
                                    ("ParamEyeBallX", .28), ("MouthX", .27)],
            2.95, [("LeanInput", "0:0 .3:8 1.9:5 2.95:0"), ("FaceAngleZ", "0:0 .32:-6 1.9:-4 2.95:0")],
            HandToChest, Pushed: true, ExpressionStrength: .55, HoldMouth: true),
        ["点胸口·坏笑"] = new([], [("EyeOpen", .53), ("EyeSmile", .28), ("LEyeSquint", .39), ("REyeSquint", .6),
                                ("ParamBrowLY", .5), ("ParamBrowRY", -.25), ("ParamMouthForm", 1), ("ParamMouthOpenY", .23),
                                ("MouthSmile_R", .72), ("MouthX", .46), ("MouthPressLipOpen", .43)],
            3.15, [("LeanInput", "0:0 .46:-6 2.2:-4 3.15:0"), ("FaceAngleZ", "0:0 .38:-8 1.05:-6 2.15:-5 3.15:0")], HoldMouth: true),
        ["点胸口·害羞"] = new(["脸红"], [("EyeOpen", .59), ("EyeSmile", .52), ("ParamMouthForm", .53), ("MouthX", .24),
                                     ("MouthPressLipOpen", .24), ("ParamEyeBallY", -.45), ("BrowInnerUp", .57)],
            3.3, [("FaceAngleX", "0:0 .38:-10 1.64:-7 2.32:-3 3.3:0"), ("FaceAngleY", "0:0 .46:-6 2.52:-5 3.3:0")],
            HandToChest, Pushed: true, BlushStrength: .72, HoldMouth: true),
    };
}
