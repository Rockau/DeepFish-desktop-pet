using System.IO;
using System.Text.Json;

namespace FishPet;

internal sealed partial class PetWindow
{
    private sealed record AffectionEventRule(int Level, string Event, int Delta, int PerDay, string? Action);
    private sealed record AffectionLevelLimits(int Level, int Gain, int Loss);
    private sealed record AffectionConfiguration(int Version, List<AffectionEventRule> Rules, List<AffectionLevelLimits> Limits);
    private const string LegacySkirtPokeEvent = "戳裙子";
    private static readonly JsonSerializerOptions AffectionJson = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static string AffectionRulesFile => Path.Combine(Path.GetDirectoryName(Path.GetFullPath(Places.SettingsFile))!, "affection-rules.json");
    private AffectionConfiguration? affectionConfiguration;
    private AffectionConfiguration AffectionConfigurationNow => affectionConfiguration ??= ReadAffectionConfiguration();
    private readonly Queue<string> affectionReactions = new();

    private bool AffectionActionValid(string? action) => action == null || action == "" ||
        LibraryCue(action) || Behavior.Moods.ContainsKey(action) || ImportedActions.Combinations.ContainsKey(action);
    private static string AffectionEventId(string kind) =>
        kind is "戳胸" or LegacySkirtPokeEvent ? Affection.ChestPokeEvent : kind;

    private bool AffectionRuleValid(AffectionEventRule rule) => rule.Level >= 0 && rule.Level <= Affection.Levels.Length &&
        rule.Event is not null && Affection.Rules.ContainsKey(AffectionEventId(rule.Event)) && rule.Delta is >= -200 and <= 200 &&
        rule.PerDay is >= 0 and <= 1000 && AffectionActionValid(rule.Action);
    private static bool AffectionLimitsValid(AffectionLevelLimits limits) => limits.Level >= 0 && limits.Level <= Affection.Levels.Length &&
        limits.Gain is >= 0 and <= 200 && limits.Loss is >= 0 and <= 200;

    private AffectionConfiguration ReadAffectionConfiguration()
    {
        if (!File.Exists(AffectionRulesFile)) return new(1, [], []);
        try
        {
            var config = JsonSerializer.Deserialize<AffectionConfiguration>(File.ReadAllText(AffectionRulesFile), AffectionJson);
            if (config is not { Version: 1, Rules: not null, Limits: not null }) return new(1, [], []);
            return config with
            {
                Rules = config.Rules.Where(rule => rule != null && AffectionRuleValid(rule)).ToList(),
                Limits = config.Limits.Where(limits => limits != null && AffectionLimitsValid(limits)).ToList()
            };
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            Places.Log("无法读取好感规则：" + error.GetType().Name);
            return new(1, [], []);
        }
    }

    private AffectionEventRule EffectiveAffectionRule(int level, string kind)
    {
        if (!Affection.Rules.TryGetValue(kind, out var original)) throw new ArgumentException("没有这个好感事件。");
        return SavedAffectionRule(level, kind)
            ?? new AffectionEventRule(level, kind, original.Points, original.PerDay, null);
    }

    private AffectionEventRule? SavedAffectionRule(int level, string kind)
    {
        var rules = AffectionConfigurationNow.Rules;
        return rules.LastOrDefault(rule => rule.Level == level && rule.Event == kind)
            ?? (kind == Affection.ChestPokeEvent
                ? rules.LastOrDefault(rule => rule.Level == level && AffectionEventId(rule.Event) == kind) : null);
    }

    private AffectionLevelLimits EffectiveAffectionLimits(int level) =>
        AffectionConfigurationNow.Limits.LastOrDefault(limits => limits.Level == level)
        ?? new AffectionLevelLimits(level, Affection.DailyGain, Affection.DailyLoss);

    private void ConfigureAffection()
    {
        affection.RuleFor = (level, kind) =>
        {
            var rule = EffectiveAffectionRule(level, kind);
            // 旧规则继续使用当天原有次数，避免把改名之前的点击漏掉或算到别的额度里。
            return (rule.Delta, rule.PerDay, rule.Event == LegacySkirtPokeEvent ? rule.Event : kind);
        };
        affection.DailyLimitsFor = level =>
        {
            var limits = EffectiveAffectionLimits(level);
            return (limits.Gain, limits.Loss);
        };
    }

    private void SaveAffectionConfiguration(AffectionConfiguration config)
    {
        string temporary = AffectionRulesFile + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(config, AffectionJson));
        File.Move(temporary, AffectionRulesFile, overwrite: true);
        affectionConfiguration = config;
    }

    private object AffectionCommand(string command, JsonElement args)
    {
        try
        {
            if (args.ValueKind != JsonValueKind.Object) throw new ArgumentException("好感设置格式无效。");
            int Integer(string key, int fallback)
            {
                if (!args.TryGetProperty(key, out var value)) return fallback;
                if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var number))
                    throw new ArgumentException("请填写整数。");
                return number;
            }
            int level = Integer("level", affection.Level);
            if (level < 0 || level > Affection.Levels.Length) throw new ArgumentException("请选择有效的好感等级。");
            string kind = command == "affection_reset" ? "" : "摸头";
            if (args.TryGetProperty("event", out var eventArg))
            {
                if (eventArg.ValueKind != JsonValueKind.String || eventArg.GetString()?.Trim() is not { Length: > 0 } eventName ||
                    !Affection.Rules.ContainsKey(AffectionEventId(eventName)))
                    throw new ArgumentException("没有这个好感事件。");
                kind = AffectionEventId(eventName);
            }
            if (command == "affection_set")
            {
                if (!args.TryGetProperty("points", out _)) throw new ArgumentException("请输入要设置的好感分数。");
                int points = Integer("points", affection.Points);
                if (points is < Affection.Min or > Affection.Max) throw new ArgumentException($"好感分数须为 {Affection.Min} 到 {Affection.Max}。");
                var before = JsonSerializer.Deserialize<Affection>(JsonSerializer.Serialize(affection))!;
                affection.SetPoints(points, DateTime.Now);
                affection.Pending.RemoveAll(item => item is "升级" or "降级");
                if (!affection.TrySave(AffectionFile))
                {
                    affection = before;
                    ConfigureAffection();
                    throw new IOException("好感分数无法保存。");
                }
                affectionReactions.Clear();
                if (affection.Level != before.Level)
                {
                    hoverZone = null;
                    hoverCooldownUntil = default;
                    StopInitiative();
                }
                UpdateAffectionReactions(before.Level);
                if (!args.TryGetProperty("level", out _)) level = affection.Level;
            }
            if (command == "affection_save")
            {
                var previous = EffectiveAffectionRule(level, kind);
                var limits = EffectiveAffectionLimits(level);
                string? action = args.TryGetProperty("action", out var raw) ?
                    raw.ValueKind == JsonValueKind.Null ? null :
                    raw.ValueKind == JsonValueKind.String ? raw.GetString()!.Trim() :
                    throw new ArgumentException("请选择有效的回应动作。") : previous.Action;
                if (action == "default") action = null;
                string eventId = previous.Event == LegacySkirtPokeEvent ? previous.Event : kind;
                var rule = new AffectionEventRule(level, eventId, Integer("delta", previous.Delta), Integer("perDay", previous.PerDay), action);
                var cap = new AffectionLevelLimits(level, Integer("dailyGain", limits.Gain), Integer("dailyLoss", limits.Loss));
                if (!AffectionRuleValid(rule) || !AffectionLimitsValid(cap))
                    throw new ArgumentException("每次分数可设为 −200 到 200，每日次数为 0 到 1000，总额度为 0 到 200。");
                var updated = AffectionConfigurationNow with
                {
                    Rules = AffectionConfigurationNow.Rules.Where(item => item.Level != level || AffectionEventId(item.Event) != kind).ToList(),
                    Limits = AffectionConfigurationNow.Limits.Where(item => item.Level != level).ToList()
                };
                var original = Affection.Rules[kind];
                if (rule.Delta != original.Points || rule.PerDay != original.PerDay || rule.Action != null) updated.Rules.Add(rule);
                if (cap.Gain != Affection.DailyGain || cap.Loss != Affection.DailyLoss) updated.Limits.Add(cap);
                SaveAffectionConfiguration(updated);
            }
            if (command == "affection_reset")
            {
                SaveAffectionConfiguration(AffectionConfigurationNow with
                {
                    Rules = AffectionConfigurationNow.Rules.Where(item => item.Level != level ||
                        kind.Length > 0 && AffectionEventId(item.Event) != kind).ToList(),
                    Limits = kind.Length == 0 ? AffectionConfigurationNow.Limits.Where(item => item.Level != level).ToList() : AffectionConfigurationNow.Limits
                });
                if (kind.Length == 0) kind = "摸头";
            }
            var selected = EffectiveAffectionRule(level, kind);
            var daily = EffectiveAffectionLimits(level);
            return Tools.Text(new
            {
                points = affection.Points, min = Affection.Min, max = Affection.Max, level = affection.Level, name = affection.LevelName,
                levels = Enumerable.Range(0, Affection.Levels.Length + 1).Select(id =>
                {
                    var band = Affection.LevelBand(id);
                    return new { id, name = band.Name, from = band.From,
                        to = id < Affection.Levels.Length ? Affection.LevelBand(id + 1).From - 1 : Affection.Max };
                }).ToArray(),
                events = Affection.Rules.Keys.Select(id => new { id, label = AffectionEventLabel(id) }).ToArray(),
                actions = Behavior.Moods.Keys.Concat(ImportedActions.Combinations.Keys).Concat(LibraryCueNames).Order().ToArray(),
                selected = new { level, @event = kind, delta = selected.Delta, perDay = selected.PerDay,
                    action = selected.Action ?? "default",
                    defaultActionLabel = kind switch
                    {
                        "戳脸" => string.Join("、", Behavior.FacePokeActions(level).Select(Behavior.FacePokeLabel)),
                        Affection.ChestPokeEvent when level == Affection.Levels.Length => $"连戳 {Behavior.ChestPokesForHeartEyes} 次爱心眼",
                        _ => AffectionTouchReactions.ClickForEvent(kind, level)
                    },
                    custom = SavedAffectionRule(level, kind) != null,
                    dailyGain = daily.Gain, dailyLoss = daily.Loss },
                today = new { gain = affection.TodayGain, loss = affection.TodayLoss, net = affection.TodayNet },
                minimumNatural = Affection.Levels[1].From
            });
        }
        catch (Exception error) when (error is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return Tools.Text(new { error = error is ArgumentException ? error.Message : "好感设置无法保存，请检查后重试。" });
        }
    }

    private static string AffectionEventLabel(string kind) => kind switch
    {
        "番茄" => "完成专注计时", "融化" => "长时间摸头彩蛋", "戳脸" => "左键戳脸", "摸手" => "左键点手",
        "摸尾巴" => "左键点尾巴", Affection.ChestPokeEvent => "戳胸", "换鞋" => "左键穿脱鞋",
        "挠脚" or "挠腰" or "捏脸" or "拖裙摆" => "右键" + kind + "（松手后）",
        _ => kind
    };
}
