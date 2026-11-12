using System;
using System.Collections.Generic;
using System.Linq;

namespace FishPet;

// 第一次真正出场时她自己说的几句欢迎（2026-11-13）：不经过模型、不花 token，每个用户第一次看到的都一样。
// 台词在插件根目录的 welcome.txt：「# 欢迎」下面写一条，「／」隔开的是一个个气泡，〔〕写动作，// 开头的是注释。
// 只说一次：说完在 settings.json 里记 Welcomed。已经有 settings.json 的老用户不会补说（见 Settings.Load）。
internal static class Welcome
{
    internal const string Name = "欢迎";

    // welcome.txt 丢了、或者里面没写，也有话说
    internal const string Fallback =
        "〔看过来 挑一下眉〕嗨，老大！我是大肥鱼，以后住在你桌面上啦／" +
        "〔竖手指〕任务栏右下角有只小鲸鱼，找不到就在“^”里，拖出来点开能调我／" +
        "〔放下手 歪一下头:右〕你的 AI 一关，我几秒后会自己退场，不是坏了／" +
        "〔微笑〕想让我先躲起来，说一声“收起来”就行／" +
        "〔抚胸 眯眼笑〕我记的事和羁绊都只存在你电脑里，不上传";

    // 取 welcome.txt 里「# 欢迎」下面的第一条；没有就用内置的
    internal static string Text(IEnumerable<string>? fileLines)
    {
        if (fileLines != null && Alarms.Parse(fileLines).TryGetValue(Name, out var lines))
            foreach (var line in lines)
                if (Bubbles(line).Length > 0) return line;
        return Fallback;
    }

    // 新装才欢迎：启动时没有 settings.json 就是新装；有（哪怕里面没有 Welcomed 这一项）就是老用户，不补说。
    internal static bool Pending(bool settingsFileExisted) => !settingsFileExisted;

    internal static string[] Bubbles(string text) =>
        text.Split('／', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
