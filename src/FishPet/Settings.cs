using System;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace FishPet;

// What is kept between starts, in settings.json beside the plugin. Only the agent changes it (the tools
// in Control.cs); there is no settings window.
internal sealed class Settings
{
    public string Shape { get; set; } = "small";
    public string[] Outfit { get; set; } = [];
    public int Fps { get; set; } = 60;
    public double Scale { get; set; } = 1;
    // 立绘清晰度：贴图缩放档位（1 = 高清 4096 原图、0.5 = 标准、0.25 = 流畅）。改档位要重启渲染器才生效。
    public double TexScale { get; set; } = 0.5;
    // Her speech bubble, words and all, apart from her own size.
    public double BubbleScale { get; set; } = 1;
    // How long the bubble stays after her last word: 0 by how much she said (5 to 20 s), -1 until her next line or a click.
    public int BubbleSeconds { get; set; }
    // Out of the way while something else fills the screen, drawing nothing meanwhile (as the BigBlueFish pet does).
    public bool HideOnFullscreen { get; set; } = true;
    // Whether she looks on GitHub for a newer plugin when she starts and twice a day (Updates).
    public bool CheckUpdates { get; set; } = false;
    public bool FollowMouse { get; set; } = true;
    // 小剧场演出期间暂时关掉视线跟随鼠标（演完自动恢复）；在 settings.json 里改，不进 update_settings。（本机加，2026-11-06）
    public bool SkitPausesFollow { get; set; } = true;
    // 只有这些音乐软件在出声才跟着跳（进程名，逗号分隔；2026-11-10 用户要的白名单）
    public string MusicApps { get; set; } = "cloudmusic,qqmusic,qqmusicclient,spotify,applemusic,itunes,music,kugou,kuwo,qishui,musicbee,foobar2000,aimp";
    // 听到电脑在放音乐就跟着跳（本机加，2026-11-10）：在 settings.json 里改成 false 就只放歌不跳舞。
    public bool MusicDance { get; set; } = true;
    // 启动时先收着：桌宠程序在运行，但她不露面，等用户叫她（show / say / coax）才出场。
    public bool StartHidden { get; set; }
    // 拖到屏幕边上会弹回来，脸始终留在工作区里。默认关：能拖出屏幕，只留一小截在外面能拖回来。
    public bool KeepOnScreen { get; set; } = false;
    // Seconds between the small things she does by herself when nobody talks to her.
    public int IdleSeconds { get; set; } = 180;
    // 到点叫人（本机加，2026-11-08）：按这台电脑的时钟，00:30 起四层哄睡、11:30 午饭提醒。默认关，托盘“更多”里能开。
    public bool CoaxSleep { get; set; } = false;
    public bool CoaxLunch { get; set; } = false;
    // 用户的生日（MM-dd，比如 10-21）：那天她会说生日快乐、演生日小剧场（mutter.txt、skits.txt 里写着 @生日 的）。空着就没有。
    public string Birthday { get; set; } = "";
    // 待机自言自语：桌宠自己调大模型现编嘀咕。
    public string LlmApiKey { get; set; } = "";
    public string LlmBaseUrl { get; set; } = "https://api.deepseek.com";
    public string LlmModel { get; set; } = "deepseek-chat";
    public double MutterChance { get; set; } = 0.5;
    public double? Left { get; set; }
    public double? Top { get; set; }

    internal static readonly int[] FpsChoices = [30, 60, 90, 120];
    private static readonly JsonSerializerOptions Format = new() { WriteIndented = true };

    internal static Settings Load()
    {
        try
        {
            if (File.Exists(Places.SettingsFile) && JsonSerializer.Deserialize<Settings>(File.ReadAllText(Places.SettingsFile)) is { } loaded)
                return loaded.Checked();
        }
        catch (Exception error) { Places.Log("设置读不出来，用默认的：" + error.Message); }
        return new Settings();
    }

    internal Settings Checked()
    {
        Shape = FishPet.Shape.Find(Shape).Id;
        Outfit = (Outfit ?? []).Where(item => FishPet.Outfit.Expression(item) != null).Select(item => FishPet.Outfit.Expression(item)!).Distinct().ToArray();
        if (!FpsChoices.Contains(Fps)) Fps = 60;
        Scale = Math.Clamp(double.IsFinite(Scale) ? Scale : 1, .5, 2);
        TexScale = TexScale >= 1 ? 1 : TexScale >= 0.5 ? 0.5 : 0.25;
        BubbleScale = Math.Clamp(double.IsFinite(BubbleScale) && BubbleScale > 0 ? BubbleScale : 1, .5, 2);
        MusicApps = (MusicApps ?? "").Trim();
        BubbleSeconds = StayFor(BubbleSeconds);
        IdleSeconds = Math.Clamp(IdleSeconds, 15, 600);
        Birthday = Condition.NormalBirthday(Birthday) ?? "";
        MutterChance = Math.Clamp(double.IsFinite(MutterChance) ? MutterChance : 0.5, 0, 1);
        return this;
    }

    internal static int StayFor(int seconds) => seconds is -1 or 0 ? seconds : Math.Clamp(seconds, 5, 180);

    internal void Save()
    {
        try { File.WriteAllText(Places.SettingsFile, JsonSerializer.Serialize(this, Format)); }
        catch (Exception error) { Places.Log("设置存不下来：" + error.Message); }
    }
}
