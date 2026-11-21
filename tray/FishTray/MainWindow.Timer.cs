using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;

namespace FishTray;

public partial class MainWindow
{
    private sealed record TimerChoice(string Id, string Label);
    private static readonly TimerChoice[] TimerChoices =
    [
        new("countdown", "倒计时"),
        new("stopwatch", "正计时"),
        new("deadline", "定时结束"),
        new("alarm", "闹钟"),
    ];
    private readonly DispatcherTimer focusClock = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private string focusPhase = "none", focusMode = "countdown";
    private DateTime? focusStarted, focusEnds;
    private int focusMinutes = 25, lastStopwatchSeconds;
    private bool focusRequestPending;
    private string? focusMessage;

    private bool Timing => focusPhase != "none";
    private string SelectedTimer => FocusModePicker.SelectedValue as string ?? "countdown";

    private void WireFocusTimer()
    {
        FocusModePicker.ItemsSource = TimerChoices;
        FocusModePicker.SelectedValue = "countdown";
        FocusModePicker.SelectionChanged += (_, _) =>
        {
            if (!Timing && (SelectedTimer is "deadline" or "alarm") && FocusHourInput.Text.Length == 0 && FocusMinuteInput.Text.Length == 0)
                ShowClockTime(DateTime.Now.AddMinutes(5));
            focusMessage = null;
            DrawFocus();
        };
        FocusMinutesInput.TextChanged += (_, _) => { if (!Timing) focusMessage = null; DrawFocus(); };
        WireClockPart(FocusHourInput, FocusHourUp, FocusHourDown, 24);
        WireClockPart(FocusMinuteInput, FocusMinuteUp, FocusMinuteDown, 60);
        FocusButton.Click += async (_, _) => await ChangeTimer();
        FocusMinutesInput.KeyDown += async (_, e) =>
        {
            if (e.Key != Key.Enter || Timing || focusRequestPending) return;
            e.Handled = true;
            if (TryFocusMinutes(out int minutes))
                await SendFocus(new { op = "focus", action = "duration", minutes }, savingDuration: true);
        };
        focusClock.Tick += (_, _) => DrawFocus();
        IsVisibleChanged += (_, _) => UpdateFocusClock();
        Closed += (_, _) => focusClock.Stop();
        DrawFocus();
    }

    private void WireClockPart(TextBox input, RepeatButton increase, RepeatButton decrease, int cycle)
    {
        void Step(int amount)
        {
            int current = TryClockPart(input.Text, cycle, out int value) ? value : 0;
            input.Text = ((current + cycle + amount) % cycle).ToString("00", CultureInfo.InvariantCulture);
        }

        increase.Click += (_, _) => Step(1);
        decrease.Click += (_, _) => Step(-1);
        input.PreviewKeyDown += (_, e) =>
        {
            if (e.Key is not (Key.Up or Key.Down)) return;
            Step(e.Key == Key.Up ? 1 : -1);
            e.Handled = true;
        };
        input.KeyDown += FocusInputEnter;
        input.TextChanged += (_, _) =>
        {
            if (!Timing) focusMessage = null;
            DrawFocus();
        };
    }

    private static bool TryClockPart(string text, int cycle, out int number)
    {
        string digits = string.Concat(text.Trim().Select(c => c is >= '０' and <= '９' ? (char)('0' + c - '０') : c));
        return int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out number) && number < cycle;
    }

    private bool TryClockTime(out TimeOnly time)
    {
        time = default;
        if (!TryClockPart(FocusHourInput.Text, 24, out int hour) || !TryClockPart(FocusMinuteInput.Text, 60, out int minute))
            return false;
        time = new TimeOnly(hour, minute);
        return true;
    }

    private void ShowClockTime(DateTime time)
    {
        FocusHourInput.Text = time.Hour.ToString("00", CultureInfo.InvariantCulture);
        FocusMinuteInput.Text = time.Minute.ToString("00", CultureInfo.InvariantCulture);
    }

    private async void FocusInputEnter(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || Timing) return;
        e.Handled = true;
        await ChangeTimer();
    }

    private async Task ChangeTimer()
    {
        if (focusRequestPending) return;
        var args = new Dictionary<string, object> { ["op"] = "focus", ["action"] = Timing ? "stop" : "start" };
        string mode = SelectedTimer;
        if (!Timing)
        {
            args["mode"] = mode;
            if (mode == "countdown")
            {
                if (!TryFocusMinutes(out int minutes)) return;
                args["minutes"] = minutes;
            }
            else if (mode is "deadline" or "alarm")
            {
                if (!TryClockTime(out var time))
                {
                    focusMessage = "小时可填 0 到 23，分钟可填 0 到 59。";
                    DrawFocus();
                    return;
                }
                args["time"] = time.ToString("HH:mm", CultureInfo.InvariantCulture);
            }
        }
        await SendFocus(args);
    }

    private bool TryFocusMinutes(out int minutes)
    {
        if (int.TryParse(FocusMinutesInput.Text, out minutes) && minutes is >= 1 and <= 180) return true;
        focusMessage = "请输入 1 到 180 分钟。";
        DrawFocus();
        return false;
    }

    private async Task SendFocus(object args, bool savingDuration = false)
    {
        focusMessage = null;
        focusRequestPending = true;
        DrawFocus();
        try
        {
            var result = await Pet.Panel(args);
            if (result is { } state && state.TryGetProperty("phase", out _))
            {
                ApplyFocus(state);
                if (savingDuration) focusMessage = $"倒计时已设为 {focusMinutes} 分钟。";
            }
            else focusMessage = result is { } error && error.TryGetProperty("error", out var message)
                ? message.GetString() : "无法连接桌宠。";
        }
        finally
        {
            focusRequestPending = false;
            DrawFocus();
        }
    }

    private void ApplyFocus(JsonElement state)
    {
        focusPhase = state.GetProperty("phase").GetString() ?? "none";
        focusMode = state.TryGetProperty("mode", out var mode) ? mode.GetString() ?? "countdown" : "countdown";
        int ReadSeconds(string name) => state.TryGetProperty(name, out var value) && value.TryGetInt32(out int seconds) ? seconds : 0;
        DateTime? ReadTime(string name) => state.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            && value.TryGetDateTime(out var time) ? time.ToUniversalTime() : null;

        focusStarted = ReadTime("started_at");
        if (ReadTime("ends_at") is { } deadline) focusEnds = deadline;
        else if (Timing && focusMode != "stopwatch")
        {
            var estimated = DateTime.UtcNow.AddSeconds(ReadSeconds("seconds_left"));
            if (focusEnds == null || Math.Abs((estimated - focusEnds.Value).TotalSeconds) > 1.5) focusEnds = estimated;
        }
        else focusEnds = null;
        lastStopwatchSeconds = focusMode == "stopwatch" ? ReadSeconds("seconds_elapsed") : 0;

        if (state.TryGetProperty("minutes", out var m) && m.TryGetInt32(out int minutes) && minutes != focusMinutes)
        {
            if (FocusMinutesInput.Text == focusMinutes.ToString() || !FocusMinutesInput.IsKeyboardFocusWithin)
                FocusMinutesInput.Text = minutes.ToString();
            focusMinutes = minutes;
        }
        if (Timing) FocusModePicker.SelectedValue = focusMode;
        if (Timing && (focusMode is "deadline" or "alarm") && focusEnds is { } scheduled)
            ShowClockTime(scheduled.ToLocalTime());
        UpdateFocusClock();
        DrawFocus();
    }

    private void UpdateFocusClock()
    {
        if (IsVisible && Timing) focusClock.Start();
        else focusClock.Stop();
    }

    private void DrawFocus()
    {
        string mode = Timing ? focusMode : SelectedTimer;
        bool clockTime = mode is "deadline" or "alarm";
        var now = DateTime.UtcNow;
        int seconds = (mode, Timing) switch
        {
            ("stopwatch", true) when focusStarted is { } start => (int)(now - start).TotalSeconds,
            ("stopwatch", _) => lastStopwatchSeconds,
            (_, true) when focusEnds is { } end => (int)Math.Ceiling((end - now).TotalSeconds),
            ("countdown", false) when int.TryParse(FocusMinutesInput.Text, out int minutes) && minutes is >= 1 and <= 180 => minutes * 60,
            ("countdown", false) => focusMinutes * 60,
            _ => 0,
        };
        var time = TimeSpan.FromSeconds(Math.Max(0, seconds));
        FocusTime.Text = !Timing && clockTime
            ? TryClockTime(out var target) ? target.ToString("HH:mm", CultureInfo.InvariantCulture) : "–:––"
            : time.TotalHours >= 1 ? $"{(int)time.TotalHours}:{time.Minutes:00}:{time.Seconds:00}" : $"{time.Minutes:00}:{time.Seconds:00}";
        FocusTitle.Text = focusPhase switch
        {
            "work" => "专注中",
            "break" => "休息中",
            "alarm" => "闹钟已设定",
            _ when mode == "alarm" => "闹钟",
            _ => "专注计时",
        };
        FocusButton.Content = Timing ? (focusPhase == "alarm" ? "取消" : "结束")
            : mode == "alarm" ? "设闹钟" : "开始";
        FocusButton.IsEnabled = !focusRequestPending;
        FocusModePicker.IsEnabled = FocusMinutesInput.IsEnabled = FocusClockRow.IsEnabled = !Timing && !focusRequestPending;
        FocusMinutesRow.Visibility = mode == "countdown" ? Visibility.Visible : Visibility.Collapsed;
        FocusClockRow.Visibility = clockTime ? Visibility.Visible : Visibility.Collapsed;
        string hint = focusMessage ?? (Timing && clockTime && focusEnds is { } at
            ? $"到点时间 {at.ToLocalTime():M月d日 HH:mm}"
            : "");
        FocusHint.Text = hint;
        FocusHint.Visibility = hint.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }
}
