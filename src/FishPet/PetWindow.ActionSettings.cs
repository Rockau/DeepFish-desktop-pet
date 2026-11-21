using System.IO;
using System.Text.Json;

namespace FishPet;

internal sealed partial class PetWindow
{
    private static readonly (string Id, string Label)[] InteractionActions =
    [
        ("interaction:stroke", "摸头互动"), ("interaction:foot", "挠脚互动"),
        ("interaction:waist", "挠腰互动"), ("interaction:pinch", "捏脸互动"), ("interaction:skirt", "拖裙摆")
    ];
    private ActionTuning? actionTuning;
    private string? adjustedActionName, adjustedActionSession, recentEditableAction;
    private ActionStyle? adjustedActionStyle;
    private ActionTuning ActionStyles => actionTuning ??= new ActionTuning(
        Path.Combine(Path.GetDirectoryName(Path.GetFullPath(Places.SettingsFile))!, "action-settings.json"));
    private ActionStyle ActionStyleFor(string name) => name == adjustedActionName && adjustedActionStyle != null
        ? adjustedActionStyle : ActionStyles.Get(name);

    private string? CurrentInteractionAction => stroking ? "interaction:stroke"
        : clothesMoving ? "interaction:skirt"
        : extraTouch == ExtraTouch.Foot || footTickleFace ? "interaction:foot"
        : extraTouch == ExtraTouch.Waist || tickling ? "interaction:waist"
        : extraTouch == ExtraTouch.Cheek ? "interaction:pinch" : null;

    private object ActionCommand(string command, JsonElement args)
    {
        try
        {
            string session = Conversation.Text(args, "session");
            if (command == "action_end_adjust")
            {
                if (session.Length > 0 && adjustedActionSession == session) ClearActionAdjustment();
                return Tools.Text(new { cleared = true });
            }
            string name = Conversation.Text(args, "name").Trim();
            if (name.Length == 0) name = "interaction:foot";
            bool interaction = InteractionActions.Any(action => action.Id == name);
            if (!interaction && !Behavior.Moods.ContainsKey(name)) throw new ArgumentException("没有这个可编辑动作。");
            if (command is "action_save" or "action_reset")
            {
                ActionStyles.Save(name, command == "action_reset" ? null : ReadActionStyle(args, ActionStyleFor(name)));
                if (adjustedActionName == name) ClearActionAdjustment();
            }
            if (command == "action_adjust")
            {
                if (session.Length is < 8 or > 80) throw new ArgumentException("动作试调会话无效，请重新打开面板。");
                var style = ReadActionStyle(args, ActionStyles.Get(name));
                if (!ActionTuning.Allowed(style)) throw new ArgumentException("动作设置超出可调整范围。");
                adjustedActionName = name;
                adjustedActionSession = session;
                adjustedActionStyle = style;
            }
            if (command == "action_preview")
            {
                if (stroking || rightDragging || dragging || speech.IsSaying)
                    throw new ArgumentException("等当前互动或说话结束，再试播动作。");
                var style = ReadActionStyle(args, ActionStyleFor(name));
                if (!ActionTuning.Allowed(style)) throw new ArgumentException("动作设置超出可调整范围。");
                Wake();
                YieldMutter();
                relaxAt = null;
                behavior.Show(name, DateTime.UtcNow, force: true,
                    preview: style.Apply(interaction ? InteractionPreview(name) : Behavior.Moods[name]));
            }
            var current = command == "action_adjust" ? ActionStyleFor(name) : ActionStyles.Get(name);
            var actions = Behavior.Moods.Keys.Order().Select(id => new { id, label = ActionLabel(id), kind = "action" })
                .Concat(InteractionActions.Select(action => new { id = action.Id, label = action.Label + "（右键拖动）", kind = "interaction" })).ToArray();
            return Tools.Text(new
            {
                actions, faces = ActionTuning.Faces, recentAction = CurrentInteractionAction ?? recentEditableAction,
                selected = new { name, strength = current.Strength, speed = current.Speed, face = current.Face,
                    softness = current.Softness, custom = ActionStyles.Custom(name) },
                previewed = command == "action_preview"
            });
        }
        catch (Exception error) when (error is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return Tools.Text(new { error = error is ArgumentException ? error.Message : "动作设置无法保存。" });
        }
    }

    private void ClearActionAdjustment()
    {
        adjustedActionName = adjustedActionSession = null;
        adjustedActionStyle = null;
    }

    private static string ActionLabel(string name) => name.StartsWith("悬停", StringComparison.Ordinal)
        ? name + "（鼠标停留触发）"
        : name == "摸头" ? "摸头（左键点击动作）" : name;

    private string? RegionEditableAction(TouchRegion region, bool right)
    {
        if (right) return region.Gesture switch
        {
            "stroke" => "interaction:stroke", "foot" => "interaction:foot", "waist" => "interaction:waist",
            "pinch" => "interaction:pinch", "skirt" => "interaction:skirt",
            "action" when region.Action is { Length: > 0 } cue && Behavior.Moods.ContainsKey(cue) => cue,
            _ => null
        };
        if (region.Id == "face" && region.Action != "")
        {
            var cue = EffectiveAffectionRule(affection.Level, "戳脸").Action
                ?? (region.Action is null or "戳脸" ? Behavior.FacePokeActions(affection.Level).First() : region.Action);
            return Behavior.Moods.ContainsKey(cue) ? cue : null;
        }
        if (region.Action != "" && AffectionTouchReactions.ForZone(region.Id, affection.Level) is { } touch)
        {
            var cue = EffectiveAffectionRule(affection.Level, touch.Event).Action ?? region.Action ?? touch.Click;
            return Behavior.Moods.ContainsKey(cue) ? cue : null;
        }
        if (region.Action is { } action) return Behavior.Moods.ContainsKey(action) ? action : null;
        return region.Id switch
        {
            "skirt" => "护裙", "foot" => null, _ => "心虚"
        };
    }

    private static ActionStyle ReadActionStyle(JsonElement args, ActionStyle current)
    {
        double Number(string key, double fallback)
        {
            if (!args.TryGetProperty(key, out var value)) return fallback;
            if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var number))
                throw new ArgumentException("请填写有效的动作数值。");
            return number;
        }
        string face = current.Face;
        if (args.TryGetProperty("face", out var choice))
        {
            if (choice.ValueKind != JsonValueKind.String) throw new ArgumentException("请选择有效的表情。");
            face = choice.GetString()!.Trim();
        }
        return new ActionStyle(Number("strength", current.Strength), Number("speed", current.Speed), face,
            Number("softness", current.Softness));
    }

    // A short demonstration of the saved interaction response. Real dragging also follows the user's hand.
    private Mood InteractionPreview(string name) => name switch
    {
        "interaction:stroke" => HeadStrokeFace.Preview(affection.Level),
        "interaction:pinch" => Behavior.Moods["戳脸"],
        "interaction:skirt" => new([], [], 2.4,
            [("post:SkirtX1", "0:0 .5:.35 1.2:-.3 1.9:.15 2.4:0"),
             ("post:ParamBodyAngleZ", "0:0 .5:6 1.2:-5 1.9:2 2.4:0")]),
        _ => new(["＞＜", "脸红"], [("ParamMouthForm", .8), ("ParamMouthOpenY", .5)], 2.8,
            [("post:ParamBodyAngleZ", "0:0 .4:5 .9:-4 1.4:3 2:-2 2.8:0"),
             ("post:ParamHipAngleZ", "0:0 .45:-4 1:3 1.5:-2 2.8:0"),
             ("FaceAngleZ", "0:0 .4:-3 1:3 1.6:-2 2.8:0")])
    };
}
