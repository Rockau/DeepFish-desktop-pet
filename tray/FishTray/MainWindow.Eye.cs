using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;

namespace FishTray;

// 更多设置里的「陪玩陪看」（本机加，2026-11-18）：开关鱼眼、调她话多话少。
// 鱼眼是插件里 fisheye 文件夹中的独立小程序（Python，Fisheye 分支，装法见 fisheye\README.md）：看最前面的窗口，本机视觉模型看懂画面，DeepSeek 写吐槽，
// 再通过网页接口让她说出来。托盘不直接管它，只和它传两张小纸条：
//   control.json（托盘写）：{"enabled": 开没开, "chattiness": 话量}，鱼眼每秒读一次，关掉了它自己收尾退出；
//   status.json（鱼眼写）：{"pid","time","state","detail","said","error"}，每两秒更新，托盘靠 time 新不新判断它还在不在。
public partial class MainWindow
{
    // 托盘编在 插件	raypp\，往上两层就是插件文件夹
    private static readonly string EyeDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "fisheye"));
    private static string EyePython => Path.Combine(EyeDir, ".venv", "Scripts", "pythonw.exe");
    private static string EyeControl => Path.Combine(EyeDir, "control.json");
    private static string EyeStatus => Path.Combine(EyeDir, "status.json");

    private readonly DispatcherTimer eyeTimer = new() { Interval = TimeSpan.FromSeconds(1.5) };
    private readonly DispatcherTimer eyeSave = new() { Interval = TimeSpan.FromMilliseconds(400) };
    private bool eyeLoading;
    // 刚点了开/关：这几秒里开关照点的显示，不让还没更新的状态把它弹回去
    private DateTime eyeClicked = DateTime.MinValue;

    private static bool EyeInstalled => File.Exists(EyePython) && File.Exists(Path.Combine(EyeDir, "fisheye.py"));

    private static string ChatText(double v) =>
        (v < 0.8 ? "安静" : v < 1.25 ? "正常" : v < 1.75 ? "话多" : "话痨") + $" {v:0.0}×";

    private void InitEye()
    {
        EyeFold.IsChecked = ui.Eye; EyePanel.Visibility = ui.Eye ? Visibility.Visible : Visibility.Collapsed;
        EyeFold.Click += (_, _) => { ui.Eye = EyeFold.IsChecked == true; EyePanel.Visibility = ui.Eye ? Visibility.Visible : Visibility.Collapsed; ui.Save(); ShowEye(); };

        eyeLoading = true;
        EyeSlider.Value = Math.Clamp(ReadEyeControl().Chattiness, 0.5, 2);
        eyeLoading = false;
        SetupSlider(EyeSlider, EyeSliderMarks, EyeSliderValue, ChatText, (0.5, "安静"), (1, "默认"), (1.5, "话多"), (2, "话痨"));
        // 拖滑块时不每一格都写：停下来 0.4 秒再存，鱼眼开着的话一秒内就生效
        EyeSlider.ValueChanged += (_, _) => { if (!eyeLoading) { eyeSave.Stop(); eyeSave.Start(); } };
        eyeSave.Tick += (_, _) =>
        {
            eyeSave.Stop();
            WriteEyeControl(ReadEyeControl().Enabled && SwEye.IsChecked == true, EyeSlider.Value);
            Notice("她的话量：" + ChatText(EyeSlider.Value));
        };

        SwEye.Click += (_, _) => ToggleEye(SwEye.IsChecked == true);
        eyeTimer.Tick += (_, _) => ShowEye();
        EyeCard.IsVisibleChanged += (_, _) => { if (EyeCard.IsVisible) { ShowEye(); eyeTimer.Start(); } else eyeTimer.Stop(); };
        ShowEye();
    }

    private void ToggleEye(bool on)
    {
        eyeClicked = DateTime.UtcNow;
        if (on && !EyeInstalled)
        {
            SwEye.IsChecked = false;
            Notice("鱼眼还没装，先运行 fisheye 里的 setup.ps1");
            ShowEye();
            return;
        }
        WriteEyeControl(on, EyeSlider.Value);
        if (on && !EyeRunning(out _))
        {
            try
            {
                Process.Start(new ProcessStartInfo(EyePython, "-X utf8 fisheye.py")
                    { WorkingDirectory = EyeDir, UseShellExecute = false, CreateNoWindow = true })?.Dispose();
                Notice("鱼眼开起来了，加载视觉模型要几秒");
            }
            catch (Exception error)
            {
                SwEye.IsChecked = false;
                WriteEyeControl(false, EyeSlider.Value);
                Notice("鱼眼没开起来：" + error.Message);
            }
        }
        else if (!on) Notice("鱼眼关了，她会说一声再停");
        ShowEye();
    }

    // 状态：鱼眼两秒写一次 status.json；进程还在、而且最近 8 秒内写过，才算开着
    private sealed record EyeState(int Pid, double Time, string State, string Detail, string Said, string Error);

    // 鱼眼正好在换 status.json 的那一瞬间读，可能读不到或读到半截：这时用上一次读到的，别让开关闪一下
    private static EyeState? lastEyeStatus;

    private static EyeState? ReadEyeStatus()
    {
        var read = ReadEyeStatusOnce();
        if (read is not null) lastEyeStatus = read;
        return read ?? lastEyeStatus;
    }

    private static EyeState? ReadEyeStatusOnce()
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(EyeStatus, Encoding.UTF8));
            var s = doc.RootElement;
            string T(string k) => s.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
            int pid = s.TryGetProperty("pid", out var p) && p.TryGetInt32(out var x) ? x : 0;
            double time = s.TryGetProperty("time", out var t) && t.TryGetDouble(out var y) ? y : 0;
            return new EyeState(pid, time, T("state"), T("detail"), T("said"), T("error"));
        }
        catch { return null; }
    }

    private static bool EyeRunning(out EyeState? state)
    {
        state = ReadEyeStatus();
        if (state is null || state.Pid <= 0) return false;
        if (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0 - state.Time > 10) return false;
        try { using var process = Process.GetProcessById(state.Pid); return !process.HasExited; }
        catch { return false; }
    }

    private static (bool Enabled, double Chattiness) ReadEyeControl()
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(EyeControl, Encoding.UTF8));
            var c = doc.RootElement;
            bool enabled = c.TryGetProperty("enabled", out var e) && e.ValueKind == JsonValueKind.True;
            double chat = c.TryGetProperty("chattiness", out var v) && v.TryGetDouble(out var x) ? x : 1;
            return (enabled, chat);
        }
        catch { return (false, 1); }
    }

    private static void WriteEyeControl(bool enabled, double chattiness)
    {
        if (!Directory.Exists(EyeDir)) return;
        try
        {
            var temp = EyeControl + ".tray.tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(new { enabled, chattiness = Math.Round(chattiness, 1) }), new UTF8Encoding(false));
            File.Move(temp, EyeControl, overwrite: true);
        }
        catch { }
    }

    private void ShowEye()
    {
        bool installed = EyeInstalled;
        bool running = EyeRunning(out var status);
        bool clickedJustNow = (DateTime.UtcNow - eyeClicked).TotalSeconds < 6;
        if (!clickedJustNow) SwEye.IsChecked = running;
        bool on = SwEye.IsChecked == true;
        SwEye.IsEnabled = installed;
        EyeSliderBox.Opacity = on ? 1 : 0.6;

        // 标题右边的小字：开着写“已开启”；还在加载、按快捷键暂停时照实写
        string tag = !running ? (on ? "正在打开" : "") : status!.State is "正在打开" or "暂停中" ? status.State : "已开启";
        bool animate = IsLoaded && danceTags.ContainsKey("eye.on");
        SetDanceTag("eye.on", on, lit: true, animate, EyeTags, tag.Length > 0 ? tag : "正在打开");
        SetDanceTag("eye.off", !on, lit: false, animate, EyeTags, installed ? "已关闭" : "没装");

        if (!installed)
            EyeNote.Text = "鱼眼还没装：在插件的 fisheye 文件夹里运行 setup.ps1（要 NVIDIA 显卡），装法见 fisheye 里的 README.md。";
        else if (running)
        {
            var lines = status!.Detail;
            if (status.Said.Length > 0) lines += (lines.Length > 0 ? "\n" : "") + "刚说：" + status.Said;
            EyeNote.Text = lines.Length > 0 ? lines : "陪着你呢";
        }
        else if (on)
            EyeNote.Text = "正在打开，加载视觉模型要几秒……";
        else if (status is { Error.Length: > 0 })
            EyeNote.Text = "上次出错了：" + status.Error;
        else
            EyeNote.Text = "打开后，她会看你最前面的窗口，陪你打游戏、看视频、看网页，看到有意思的地方吐槽一句。截图只在本机看，不上传。\n" +
                           "开着时会先关掉「别的程序全屏时藏起来」，关掉鱼眼时改回去。快捷键：Ctrl+Alt+F9 暂停，Ctrl+Alt+F10 让她说一句。";
    }
}
