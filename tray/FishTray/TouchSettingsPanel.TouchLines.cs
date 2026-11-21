using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;

namespace FishTray;

public partial class TouchSettingsPanel
{
    private sealed record TouchLineStage(int Id, string Label);
    private sealed record TouchLineOption(string Id, string Label);
    private sealed record TouchLineBox(string Shape, string Button, string Zone);
    private sealed record TouchLineSelection(TouchLineBox Box, int Level, string Reaction);
    private sealed record TouchLineDraft(string Text, string Name);
    private readonly Dictionary<TouchLineSelection, TouchLineDraft> lineDrafts = new();
    private TouchLineSelection? touchLineSelection;
    private TouchLineDraft savedTouchLine = new("", "");
    private bool touchLineBusy;
    private bool reloadTouchLines;

    private TouchLineBox? SelectedTouchLineBox() => zone is { IsNew: false } region
        ? new(shape, button, region.Id) : null;

    private void WireTouchLines()
    {
        TouchLinesFold.Click += (_, _) => RefreshTouchLineEditor();
        TouchLineLevel.SelectionChanged += async (_, _) =>
        {
            if (!touchLineBusy) await LoadTouchLines("touch_lines");
        };
        TouchLineReaction.SelectionChanged += async (_, _) =>
        {
            if (!touchLineBusy) await LoadTouchLines("touch_lines");
        };
        TouchLineInput.TextChanged += (_, _) => MarkTouchLineDraft();
        TouchLineName.TextChanged += (_, _) => MarkTouchLineDraft();
        SaveTouchLines.Click += async (_, _) => await LoadTouchLines("touch_lines_save");
        ResetTouchLines.Click += async (_, _) => await LoadTouchLines("touch_lines_reset");
    }

    internal void OpenTouchLineEditor()
    {
        TouchLinesFold.IsChecked = true;
        RefreshTouchLineEditor();
    }

    private void RefreshTouchLineEditor()
    {
        var selectedBox = SelectedTouchLineBox();
        bool open = selectedBox != null && TouchLinesFold.IsChecked == true;
        TouchLinesFold.Visibility = selectedBox != null ? Visibility.Visible : Visibility.Collapsed;
        TouchLinesPanel.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        if (open)
        {
            SetTouchLineControls(touchLineBusy);
            _ = LoadTouchLines("touch_lines");
        }
    }

    private TouchLineDraft CurrentTouchLine() => new(TouchLineInput.Text, TouchLineName.Text);

    private void MarkTouchLineDraft()
    {
        if (touchLineBusy || touchLineSelection is not { Reaction.Length: > 0 } active || active.Box != SelectedTouchLineBox()) return;
        SaveTouchLines.IsEnabled = CurrentTouchLine() != savedTouchLine;
        TouchLineStatus.Text = SaveTouchLines.IsEnabled ? "有尚未保存的修改。" : "";
    }

    private void SetTouchLineControls(bool loading)
    {
        touchLineBusy = loading;
        bool selected = touchLineSelection is { } active && active.Box == SelectedTouchLineBox();
        bool canEdit = selected && touchLineSelection!.Reaction.Length > 0;
        TouchLineLevel.IsEnabled = selected && !loading;
        TouchLineReaction.IsEnabled = ResetTouchLines.IsEnabled = canEdit && !loading;
        TouchLineFields.Visibility = canEdit ? Visibility.Visible : Visibility.Collapsed;
        TouchLineFields.IsEnabled = !loading;
        SaveTouchLines.IsEnabled = canEdit && !loading && CurrentTouchLine() != savedTouchLine;
    }

    private async Task LoadTouchLines(string command)
    {
        var selectedBox = SelectedTouchLineBox();
        bool reading = command == "touch_lines";
        if (closed || selectedBox == null || TouchLinesFold.IsChecked != true) return;
        if (touchLineBusy)
        {
            reloadTouchLines |= reading;
            return;
        }
        if (!reading && (touchLineSelection?.Box != selectedBox || touchLineSelection.Reaction.Length == 0)) return;

        var edited = CurrentTouchLine();
        bool renamed = edited.Name != savedTouchLine.Name;
        if (touchLineSelection is { Reaction.Length: > 0 } previous)
        {
            if (edited == savedTouchLine) lineDrafts.Remove(previous);
            else lineDrafts[previous] = edited;
        }

        var args = new Dictionary<string, object>
        {
            ["shape"] = selectedBox.Shape,
            ["button"] = selectedBox.Button,
            ["zone"] = selectedBox.Zone,
            ["reaction"] = TouchLineReaction.SelectedValue as string ?? ""
        };
        if (TouchLineLevel.SelectedValue is int level) args["level"] = level;
        if (command == "touch_lines_save")
        {
            if (edited.Text != savedTouchLine.Text) args["text"] = edited.Text;
            if (renamed) args["displayName"] = edited.Name;
        }
        SetTouchLineControls(loading: true);
        TouchLineStatus.Text = reading ? "正在读取本框台词…" : "";
        try
        {
            var result = await Pet.Call(command, args);
            bool matchesView = !closed && SelectedTouchLineBox() == selectedBox && TouchLinesFold.IsChecked == true;
            if (result is not { } data || !data.TryGetProperty("levels", out var levels))
            {
                if (matchesView)
                {
                    TouchLineStatus.Text = Error(result, "无法读取互动台词，请刷新预览。");
                    if (touchLineSelection is { } current && current.Box == selectedBox) SelectTouchLine(current);
                }
                return;
            }

            var selected = new TouchLineSelection(selectedBox, data.GetProperty("level").GetInt32(), Read(data, "reaction") ?? "");
            var saved = new TouchLineDraft(Read(data, "text") ?? "", Read(data, "displayName") ?? "");
            if (!reading) lineDrafts.Remove(selected);
            if (command == "touch_lines_reset" && renamed && edited.Name != saved.Name)
                lineDrafts[selected] = saved with { Name = edited.Name };
            if (!matchesView)
            {
                if (!reading && touchLineSelection == selected) touchLineSelection = null;
                return;
            }

            touchLineSelection = selected;
            savedTouchLine = saved;
            TouchLineLevel.ItemsSource = levels.EnumerateArray().Select(option =>
                new TouchLineStage(option.GetProperty("id").GetInt32(), Read(option, "label") ?? "")).ToArray();
            TouchLineReaction.ItemsSource = data.GetProperty("reactions").EnumerateArray().Select(option =>
                new TouchLineOption(Read(option, "id") ?? "", Read(option, "label") ?? "")).ToArray();
            SelectTouchLine(selected);
            var shown = lineDrafts.GetValueOrDefault(selected) ?? saved;
            TouchLineInput.Text = shown.Text;
            TouchLineName.Text = shown.Name;
            TouchLineStatus.Text = shown != saved ? "有尚未保存的修改。" : command switch
            {
                "touch_lines_save" => "已保存，下次触发时使用。",
                "touch_lines_reset" => "已恢复本档台词。",
                _ when selected.Reaction.Length == 0 => "这个判定框在本档没有可以编辑台词的反应。",
                _ => data.GetProperty("custom").GetBoolean() ? "本档使用自定义台词。" : ""
            };
        }
        finally
        {
            SetTouchLineControls(loading: false);
            if (reloadTouchLines)
            {
                reloadTouchLines = false;
                _ = LoadTouchLines("touch_lines");
            }
        }
    }

    private void SelectTouchLine(TouchLineSelection selection)
    {
        TouchLineLevel.SelectedValue = selection.Level;
        TouchLineReaction.SelectedValue = selection.Reaction;
    }
}
