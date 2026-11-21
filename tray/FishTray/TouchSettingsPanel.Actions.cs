using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace FishTray;

public partial class TouchSettingsPanel
{
    private sealed record ActionOption(string Id, string Name, string Kind);
    private sealed record FaceOption(string Id, string Name);
    private sealed record ActionDraft(double Strength, double Speed, string Face, double Softness);
    private readonly Dictionary<string, ActionDraft> actionDrafts = [];
    private readonly Dictionary<string, ActionDraft> savedActions = [];
    private List<ActionOption> actionOptions = [];
    private ActionOption? editedAction;
    private bool actionChanging, actionBusy, actionLoaded;
    private int actionRequest;
    private readonly string actionSession = Guid.NewGuid().ToString("N");
    private readonly DispatcherTimer actionDebounce = new() { Interval = TimeSpan.FromMilliseconds(150) };
    private Task actionMutations = Task.CompletedTask;
    private long actionLiveVersion;
    private int actionSelectionVersion;
    private bool actionSessionTouched;
    private string? regionEditAction;

    private void InitializeActionEditor()
    {
        AddedCue.DisplayMemberPath = "Name";
        AddedCue.SelectedValuePath = "Id";
        AddedCue.SelectionChanged += async (_, _) =>
        {
            if (actionChanging) return;
            RememberAction();
            var selected = AddedCue.SelectedItem as ActionOption;
            int version = ++actionSelectionVersion;
            SetActionBusy(true);
            await EndActionAdjustment();
            if (closed || version != actionSelectionVersion || AddedCue.SelectedItem as ActionOption != selected) return;
            await LoadAction(selected);
        };
        ActionStrength.ValueChanged += (_, _) => ActionValueChanged();
        ActionSpeed.ValueChanged += (_, _) => ActionValueChanged();
        ActionSoftness.ValueChanged += (_, _) => ActionValueChanged();
        ActionFace.SelectionChanged += (_, _) => ActionValueChanged();
        actionDebounce.Tick += async (_, _) =>
        {
            actionDebounce.Stop();
            await AdjustAction();
        };
        SaveAction.Click += async (_, _) => await ChangeAction("action_save");
        ResetAction.Click += async (_, _) => await ChangeAction("action_reset");
        PreviewAdded.Click += async (_, _) => await ChangeAction("action_preview");
        EditRegionAction.Click += async (_, _) => await SelectEditorAction(regionEditAction, bringIntoView: true);
        EditRecentAction.Click += async (_, _) => await EditRecentInteraction();
        UpdateActionValues();
    }

    private void ActionValueChanged()
    {
        UpdateActionValues();
        if (actionChanging || actionBusy || !actionLoaded || closed || editedAction == null) return;
        RememberAction();
        ++actionLiveVersion;
        ActionStatus.Text = "正在应用调整…";
        actionDebounce.Stop();
        actionDebounce.Start();
    }

    private async Task AdjustAction()
    {
        if (closed || actionBusy || !actionLoaded || editedAction is not { } action) return;
        var draft = ReadActionDraft();
        long version = actionLiveVersion;
        actionSessionTouched = true;
        await QueueActionMutation(async () =>
        {
            if (closed || version != actionLiveVersion || editedAction?.Id != action.Id) return;
            var response = await Pet.Call("action_adjust", new
            {
                name = action.Id, strength = draft.Strength, speed = draft.Speed,
                face = draft.Face, softness = draft.Softness, session = actionSession
            }, 8);
            if (closed || version != actionLiveVersion || editedAction?.Id != action.Id) return;
            ActionStatus.Text = response is null || Read(response.Value, "error") != null
                ? Error(response, "调整没有生效，请检查桌宠连接后再试。")
                : "已临时生效，点「保存动作」可保留。";
        });
    }

    private Task QueueActionMutation(Func<Task> action)
    {
        var previous = actionMutations;
        async Task Run()
        {
            await previous;
            await action();
        }
        actionMutations = Run();
        return actionMutations;
    }

    private Task EndActionAdjustment()
    {
        ++actionLiveVersion;
        actionDebounce.Stop();
        if (!actionSessionTouched) return Task.CompletedTask;
        actionSessionTouched = false;
        return QueueActionMutation(async () =>
        {
            await Pet.Call("action_end_adjust", new { session = actionSession }, 8);
        });
    }

    private void SuspendActionEditor()
    {
        RememberAction();
        _ = EndActionAdjustment();
        if (actionLoaded && editedAction is { } action && actionDrafts.ContainsKey(action.Id))
            ActionStatus.Text = "有尚未保存的修改。重新调整可临时应用。";
    }

    private void UpdateRegionAction()
    {
        regionEditAction = zone?.EditAction;
        EditRegionAction.Content = IsRight ? "编辑这个互动" : "编辑这个动作";
        EditRegionAction.IsEnabled = !string.IsNullOrEmpty(regionEditAction);
        if (actionOptions.Count == 0 || string.IsNullOrEmpty(regionEditAction)) return;
        RememberAction();
        if (editedAction is { } action && actionDrafts.ContainsKey(action.Id)) return;
        _ = SelectEditorAction(regionEditAction, bringIntoView: false);
    }

    private async Task SelectEditorAction(string? name, bool bringIntoView)
    {
        if (closed || actionBusy || string.IsNullOrEmpty(name)) return;
        var target = actionOptions.Find(action => action.Id == name);
        if (target == null)
        {
            ActionStatus.Text = "这个区域暂时没有可编辑的动作。";
            return;
        }
        if (editedAction?.Id != target.Id)
        {
            RememberAction();
            int version = ++actionSelectionVersion;
            SetActionBusy(true);
            actionChanging = true;
            AddedCue.SelectedItem = target;
            actionChanging = false;
            await EndActionAdjustment();
            if (closed || version != actionSelectionVersion) return;
            await LoadAction(target);
        }
        if (bringIntoView)
        {
            AddedActions.BringIntoView();
            AddedCue.Focus();
        }
    }

    private async Task EditRecentInteraction()
    {
        if (closed || actionBusy) return;
        SetActionBusy(true);
        var response = await Pet.Call("action_settings", new { }, 8);
        if (closed) return;
        SetActionBusy(false);
        string? recent = response is { } data ? Read(data, "recentAction") : null;
        if (string.IsNullOrEmpty(recent))
        {
            ActionStatus.Text = "先在桌面上互动一次，再点这里编辑。";
            return;
        }
        await SelectEditorAction(recent, bringIntoView: true);
    }

    private void UpdateActionValues()
    {
        ActionStrengthValue.Text = ActionStrength.Value.ToString("P0", CultureInfo.CurrentCulture);
        ActionSpeedValue.Text = ActionSpeed.Value.ToString("0.00", CultureInfo.CurrentCulture) + "×";
        ActionSoftnessValue.Text = ActionSoftness.Value.ToString("P0", CultureInfo.CurrentCulture);
    }

    private async Task RefreshActionEditor()
    {
        if (closed || actionBusy || actionOptions.Count > 0) return;
        SetActionBusy(true);
        int request = ++actionRequest;
        var result = await Pet.Call("action_settings", new { }, 8);
        if (closed || request != actionRequest) return;
        if (!ApplyActionCatalog(result))
        {
            ActionStatus.Text = Error(result, "动作设置暂时无法读取，点击刷新预览后重试。");
            AddedActions.Visibility = Visibility.Visible;
        }
        SetActionBusy(false);
        UpdateRegionAction();
    }

    private bool ApplyActionCatalog(JsonElement? result)
    {
        if (result is not { ValueKind: JsonValueKind.Object } data ||
            !data.TryGetProperty("actions", out var actions) || actions.ValueKind != JsonValueKind.Array) return false;
        var choices = new List<ActionOption>();
        foreach (var item in actions.EnumerateArray())
            if (Read(item, "id") is { Length: > 0 } id)
                choices.Add(new ActionOption(id, Read(item, "label") ?? id, Read(item, "kind") ?? "action"));
        if (choices.Count == 0) return false;

        string? selectedName = ReadSelectedName(data) ?? editedAction?.Id;
        actionOptions = choices;
        actionChanging = true;
        AddedCue.ItemsSource = choices;
        AddedCue.SelectedItem = choices.Find(option => option.Id == selectedName) ?? choices[0];
        var faces = new List<FaceOption> { new("", "沿用默认表情") };
        faces.AddRange(ReadNames(data, "faces").Where(face => face.Length > 0).Select(face => new FaceOption(face, face)));
        ActionFace.ItemsSource = faces;
        actionChanging = false;
        AddedActions.Visibility = Visibility.Visible;
        editedAction = AddedCue.SelectedItem as ActionOption;
        return ApplyActionSettings(data);
    }

    private async Task LoadAction(ActionOption? action)
    {
        editedAction = action;
        actionLoaded = false;
        if (action is null) { SetActionBusy(false); return; }
        SetActionBusy(true);
        if (actionDrafts.TryGetValue(action.Id, out var draft))
        {
            ApplyActionDraft(action, draft);
            ActionStatus.Text = "有尚未保存的修改。";
            SetActionBusy(false);
            return;
        }
        int request = ++actionRequest;
        var result = await Pet.Call("action_settings", new { name = action.Id }, 8);
        if (closed || request != actionRequest) return;
        if (!ApplyActionSettings(result))
            ActionStatus.Text = Error(result, "没有读到这个动作的设置，请重新选择。");
        SetActionBusy(false);
    }

    private bool ApplyActionSettings(JsonElement? result)
    {
        if (result is not { ValueKind: JsonValueKind.Object } data ||
            !data.TryGetProperty("selected", out var selected) || selected.ValueKind != JsonValueKind.Object ||
            Read(selected, "name") is not { } name || editedAction?.Id != name) return false;
        double strength = Number(selected, "strength"), speed = Number(selected, "speed"), softness = Number(selected, "softness");
        if (!double.IsFinite(strength) || !double.IsFinite(speed)) return false;
        var draft = new ActionDraft(strength, speed, Read(selected, "face") ?? "", double.IsFinite(softness) ? softness : .5);
        savedActions[name] = draft;
        ApplyActionDraft(editedAction, draft);
        ActionStatus.Text = Flag(selected, "custom") ? "当前使用自定义动作，可恢复默认。" : "当前使用默认动作。";
        return true;
    }

    private void ApplyActionDraft(ActionOption action, ActionDraft draft)
    {
        actionLoaded = true;
        actionChanging = true;
        ActionStrength.Value = draft.Strength;
        ActionSpeed.Value = draft.Speed;
        ActionSoftness.Value = draft.Softness;
        ActionFace.SelectedValue = draft.Face;
        if (ActionFace.SelectedIndex < 0) ActionFace.SelectedIndex = 0;
        bool interaction = action.Kind != "action";
        ActionTarget.Text = "当前调整「" + action.Name + "」";
        ActionSoftnessFields.Visibility = interaction ? Visibility.Visible : Visibility.Collapsed;
        ActionHint.Text = interaction
            ? "幅度会一起调整身体和表情强度。滑块临时生效，保存后可保留。"
            : "幅度会一起调整动作和表情强度。滑块临时生效，保存后可保留。";
        actionChanging = false;
        ActionFields.IsEnabled = !actionBusy;
        UpdateActionValues();
    }

    private ActionDraft ReadActionDraft() => new(ActionStrength.Value, ActionSpeed.Value,
        ActionFace.SelectedValue as string ?? "", ActionSoftness.Value);

    private void RememberAction()
    {
        if (actionChanging || actionBusy || !actionLoaded || editedAction is not { } action) return;
        var current = ReadActionDraft();
        if (savedActions.TryGetValue(action.Id, out var saved) && current == saved)
            actionDrafts.Remove(action.Id);
        else actionDrafts[action.Id] = current;
    }

    private void SetActionBusy(bool value)
    {
        actionBusy = value;
        AddedCue.IsEnabled = !value;
        ActionFields.IsEnabled = !value && actionLoaded;
        PreviewAdded.IsEnabled = !value && actionLoaded;
        EditRecentAction.IsEnabled = !value;
        EditRegionAction.IsEnabled = !value && !string.IsNullOrEmpty(regionEditAction);
    }

    private async Task ChangeAction(string command)
    {
        if (actionBusy || !actionLoaded || editedAction is not { } action) return;
        var draft = ReadActionDraft();
        SetActionBusy(true);
        await EndActionAdjustment();
        object args = command == "action_reset" ? new { name = action.Id } : new
        {
            name = action.Id, strength = draft.Strength, speed = draft.Speed,
            face = draft.Face, softness = draft.Softness
        };
        int request = ++actionRequest;
        JsonElement? response = null;
        await QueueActionMutation(async () => { response = await Pet.Call(command, args, 8); });
        if (closed || request != actionRequest) return;
        bool failed = response is null || Read(response.Value, "error") != null;
        if (failed)
            ActionStatus.Text = Error(response, "这次操作没有成功，请检查桌宠连接后重试。");
        else if (command == "action_preview")
            ActionStatus.Text = "已试播当前设置。";
        else if (ApplyActionSettings(response))
        {
            actionDrafts.Remove(action.Id);
            ActionStatus.Text = command == "action_reset" ? "已恢复这个动作的默认设置。" : "已保存这个动作。";
        }
        else ActionStatus.Text = "桌宠返回的动作设置无效，请刷新后重试。";
        SetActionBusy(false);
    }

    private static string? ReadSelectedName(JsonElement data) =>
        data.TryGetProperty("selected", out var selected) && selected.ValueKind == JsonValueKind.Object ? Read(selected, "name") : null;
}
