using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace FishTray;

public partial class AffectionSettingsPanel : UserControl
{
    private sealed record LevelOption(int Id, string Name);
    private sealed record EventOption(string Id, string Name);
    private sealed record ActionOption(string Id, string Name);
    private sealed record RuleDraft(string Delta, string PerDay, string Action);
    private sealed record LimitsDraft(string Gain, string Loss);
    private readonly Dictionary<(int Level, string Event), RuleDraft> drafts = [];
    private readonly Dictionary<int, LimitsDraft> limitDrafts = [];
    private readonly Dictionary<(int Level, string Event), RuleDraft> savedRules = [];
    private readonly Dictionary<int, LimitsDraft> savedLimits = [];
    private int selectedLevel = -1, minimum, maximum = 200;
    private string selectedEvent = "";
    private bool busy, changing, disposed;

    internal event EventHandler? Changed;

    public AffectionSettingsPanel()
    {
        InitializeComponent();
        Loaded += async (_, _) => await RefreshIfVisible();
        IsVisibleChanged += async (_, _) => { if (IsVisible) await RefreshIfVisible(); };
        RefreshButton.Click += async (_, _) => await LoadSettings();
        SetPointsButton.Click += async (_, _) => await SetPoints();
        SaveButton.Click += async (_, _) => await SaveRule();
        ResetButton.Click += async (_, _) => await ResetRule(all: false);
        ResetLevelButton.Click += async (_, _) => await ResetRule(all: true);
        LevelPicker.SelectionChanged += async (_, _) => await ChangeSelection();
        EventPicker.SelectionChanged += async (_, _) => await ChangeSelection();
        LimitsFold.Click += (_, _) => LimitsFields.Visibility = LimitsFold.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }

    internal void Dispose() => disposed = true;

    internal void ShowCurrentScore(int points, string rankName)
        => CurrentPoints.Text = $"当前 {points} 点 · {rankName}";

    private async Task RefreshIfVisible()
    {
        if (!disposed && IsLoaded && IsVisible) await LoadSettings();
    }

    private void SetBusy(bool value)
    {
        busy = value;
        RefreshButton.IsEnabled = PointsInput.IsEnabled = SetPointsButton.IsEnabled = !value;
        LevelPicker.IsEnabled = EventPicker.IsEnabled = !value;
        RuleFields.IsEnabled = !value && selectedLevel >= 0 && selectedEvent.Length > 0;
    }

    private void RememberEdits()
    {
        if (changing || busy || selectedLevel < 0 || selectedEvent.Length == 0) return;
        var key = (selectedLevel, selectedEvent);
        var currentRule = new RuleDraft(DeltaInput.Text, PerDayInput.Text, ActionPicker.SelectedValue as string ?? "");
        var currentLimits = new LimitsDraft(DailyGainInput.Text, DailyLossInput.Text);
        if (savedRules.GetValueOrDefault(key) == currentRule) drafts.Remove(key);
        else drafts[key] = currentRule;
        if (savedLimits.GetValueOrDefault(selectedLevel) == currentLimits) limitDrafts.Remove(selectedLevel);
        else limitDrafts[selectedLevel] = currentLimits;
    }

    private async Task ChangeSelection()
    {
        if (changing || busy || disposed) return;
        RememberEdits();
        int? level = LevelPicker.SelectedValue is int id ? id : null;
        string? kind = EventPicker.SelectedValue as string;
        await LoadSettings(level, kind);
    }

    private async Task LoadSettings(int? level = null, string? kind = null)
    {
        if (busy || disposed) return;
        RememberEdits();
        SetBusy(true);
        var request = new Dictionary<string, object>();
        if ((level ?? selectedLevel) >= 0) request["level"] = level ?? selectedLevel;
        if ((kind ?? selectedEvent).Length > 0) request["event"] = kind ?? selectedEvent;
        var response = await Pet.Call("affection_settings", request, 8);
        if (disposed) return;
        if (!ApplySettings(response))
        {
            changing = true;
            LevelPicker.SelectedValue = selectedLevel;
            EventPicker.SelectedValue = selectedEvent;
            changing = false;
            Status.Text = Error(response, "暂时无法读取关系设置，请检查桌宠连接后刷新。");
        }
        SetBusy(false);
    }

    private bool ApplySettings(JsonElement? response)
    {
        if (response is not { ValueKind: JsonValueKind.Object } data ||
            !data.TryGetProperty("levels", out var levels) || levels.ValueKind != JsonValueKind.Array ||
            !data.TryGetProperty("events", out var events) || events.ValueKind != JsonValueKind.Array ||
            !data.TryGetProperty("selected", out var selected) || selected.ValueKind != JsonValueKind.Object ||
            !TryInteger(selected, "level", out int level) || Text(selected, "event") is not { Length: > 0 } kind)
            return false;

        var ranks = levels.EnumerateArray().Where(item => TryInteger(item, "id", out _))
            .Select(item => new LevelOption(item.GetProperty("id").GetInt32(), Text(item, "name") ?? "")).ToArray();
        var triggers = events.EnumerateArray().Where(item => Text(item, "id") is { Length: > 0 })
            .Select(item => new EventOption(Text(item, "id")!, Text(item, "label") ?? Text(item, "id")!)).ToArray();
        if (!ranks.Any(rank => rank.Id == level) || !triggers.Any(trigger => trigger.Id == kind)) return false;
        if (!TryInteger(selected, "delta", out int delta) || !TryInteger(selected, "perDay", out int perDay) ||
            !TryInteger(selected, "dailyGain", out int dailyGain) || !TryInteger(selected, "dailyLoss", out int dailyLoss)) return false;

        int points = Integer(data, "points");
        minimum = Integer(data, "min");
        maximum = TryInteger(data, "max", out int limit) ? limit : 200;
        string defaultLabel = Text(selected, "defaultActionLabel") is { Length: > 0 } label
            ? $"默认（{label}）" : "沿用默认反应";
        var choices = new List<ActionOption> { new("default", defaultLabel), new("", "不播放回应动作") };
        if (data.TryGetProperty("actions", out var actions) && actions.ValueKind == JsonValueKind.Array)
            choices.AddRange(actions.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String)
                .Select(item => item.GetString()!).Where(name => name.Length > 0 && name != "default")
                .Distinct().Select(name => new ActionOption(name, name)));
        string savedAction = Text(selected, "action") ?? "default";
        var savedRule = new RuleDraft(delta.ToString(CultureInfo.CurrentCulture), perDay.ToString(CultureInfo.CurrentCulture), savedAction);
        var savedCaps = new LimitsDraft(dailyGain.ToString(CultureInfo.CurrentCulture), dailyLoss.ToString(CultureInfo.CurrentCulture));
        savedRules[(level, kind)] = savedRule;
        savedLimits[level] = savedCaps;
        var rule = drafts.GetValueOrDefault((level, kind)) ?? savedRule;
        var caps = limitDrafts.GetValueOrDefault(level) ?? savedCaps;

        changing = true;
        selectedLevel = level;
        selectedEvent = kind;
        LevelPicker.ItemsSource = ranks;
        LevelPicker.SelectedValue = level;
        EventPicker.ItemsSource = triggers;
        EventPicker.SelectedValue = kind;
        ActionPicker.ItemsSource = choices;
        ActionPicker.SelectedValue = rule.Action;
        if (ActionPicker.SelectedIndex < 0) ActionPicker.SelectedValue = "default";
        DeltaInput.Text = rule.Delta;
        PerDayInput.Text = rule.PerDay;
        DailyGainInput.Text = caps.Gain;
        DailyLossInput.Text = caps.Loss;
        PointsInput.Text = points.ToString(CultureInfo.CurrentCulture);
        string rankName = Text(data, "name") ?? "";
        ShowCurrentScore(points, rankName);
        PointsHint.Text = $"可填写 {minimum} 到 {maximum}，关系阶段随分数更新。";
        string condition = drafts.ContainsKey((level, kind)) || limitDrafts.ContainsKey(level)
            ? "有尚未保存的修改。" : Flag(selected, "custom") ? "当前使用自定义设置。" : "当前使用默认设置。";
        RuleHint.Text = condition;
        changing = false;
        Status.Text = "";
        return true;
    }

    private async Task SetPoints()
    {
        if (busy || disposed) return;
        if (!ParseInteger(PointsInput.Text, out int points) || points < minimum || points > maximum)
        {
            Status.Text = $"请填写 {minimum} 到 {maximum} 的整数。";
            return;
        }
        RememberEdits();
        SetBusy(true);
        object request = selectedEvent.Length > 0 ? new { points, @event = selectedEvent } : new { points };
        var response = await Pet.Call("affection_set", request, 8);
        if (disposed) return;
        if (ApplySettings(response))
        {
            Status.Text = "已设置关系值。";
            Changed?.Invoke(this, EventArgs.Empty);
        }
        else Status.Text = Error(response, "没有设置成功，请检查桌宠连接。");
        SetBusy(false);
    }

    private async Task SaveRule()
    {
        if (busy || disposed || selectedLevel < 0 || selectedEvent.Length == 0) return;
        if (!ParseInteger(DeltaInput.Text, out int delta) || delta < -maximum || delta > maximum ||
            !ParseInteger(PerDayInput.Text, out int perDay) || perDay is < 0 or > 1000 ||
            !ParseInteger(DailyGainInput.Text, out int gain) || gain < 0 || gain > maximum ||
            !ParseInteger(DailyLossInput.Text, out int loss) || loss < 0 || loss > maximum)
        {
            Status.Text = $"每次变化请填 {-maximum} 到 {maximum} 的整数，次数填 0 到 1000，每日上限填 0 到 {maximum}。";
            return;
        }
        RememberEdits();
        int level = selectedLevel;
        string kind = selectedEvent;
        SetBusy(true);
        var response = await Pet.Call("affection_save", new
        {
            level, @event = kind, delta, perDay, action = ActionPicker.SelectedValue as string ?? "",
            dailyGain = gain, dailyLoss = loss
        }, 8);
        if (disposed) return;
        if (IsSettings(response))
        {
            drafts.Remove((level, kind));
            limitDrafts.Remove(level);
        }
        if (ApplySettings(response))
        {
            Status.Text = "已保存本阶段的这项互动。";
            Changed?.Invoke(this, EventArgs.Empty);
        }
        else Status.Text = Error(response, "没有保存成功，请检查填写内容或桌宠连接。");
        SetBusy(false);
    }

    private async Task ResetRule(bool all)
    {
        if (busy || disposed || selectedLevel < 0 || selectedEvent.Length == 0) return;
        if (all && MessageBox.Show("恢复这个关系阶段的全部互动和计分上限？", "恢复本级默认",
            MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel) != MessageBoxResult.OK) return;
        RememberEdits();
        int level = selectedLevel;
        string kind = selectedEvent;
        SetBusy(true);
        object request = all ? new { level } : new { level, @event = kind };
        var response = await Pet.Call("affection_reset", request, 8);
        if (disposed) return;
        if (IsSettings(response))
        {
            if (all)
            {
                foreach (var key in drafts.Keys.Where(key => key.Level == level).ToArray()) drafts.Remove(key);
                limitDrafts.Remove(level);
            }
            else drafts.Remove((level, kind));
        }
        if (ApplySettings(response))
        {
            Status.Text = all ? "已恢复本阶段默认设置。" : "已恢复本项默认设置。";
            Changed?.Invoke(this, EventArgs.Empty);
        }
        else Status.Text = Error(response, "没有恢复成功，请检查桌宠连接。");
        SetBusy(false);
    }

    private static bool IsSettings(JsonElement? response) => response is { ValueKind: JsonValueKind.Object } data &&
        data.TryGetProperty("selected", out var selected) && selected.ValueKind == JsonValueKind.Object && !data.TryGetProperty("error", out _);
    private static bool ParseInteger(string text, out int value) => int.TryParse(text, NumberStyles.Integer, CultureInfo.CurrentCulture, out value);
    private static bool TryInteger(JsonElement data, string key, out int value)
    {
        value = 0;
        return data.TryGetProperty(key, out var field) && field.ValueKind == JsonValueKind.Number && field.TryGetInt32(out value);
    }
    private static int Integer(JsonElement data, string key) => TryInteger(data, key, out int value) ? value : 0;
    private static string? Text(JsonElement data, string key) => data.TryGetProperty(key, out var field) &&
        field.ValueKind == JsonValueKind.String ? field.GetString() : null;
    private static bool Flag(JsonElement data, string key) => data.TryGetProperty(key, out var field) && field.ValueKind == JsonValueKind.True;
    private static string Error(JsonElement? response, string fallback) => response is { } data ? Text(data, "error") ?? fallback : fallback;
}
