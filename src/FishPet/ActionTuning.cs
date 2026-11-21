using System.Globalization;
using System.IO;
using System.Text.Json;

namespace FishPet;

internal sealed record ActionStyle(double Strength = 1, double Speed = 1, string Face = "", double Softness = .5)
{
    internal static readonly ActionStyle Default = new();
    internal bool Valid => double.IsFinite(Strength) && Strength is >= .25 and <= 2 &&
        double.IsFinite(Speed) && Speed is >= .5 and <= 2 &&
        double.IsFinite(Softness) && Softness is >= 0 and <= 1 && Face != null;

    internal Mood Apply(Mood source)
    {
        double Scale(string id, double value) => id == "EyeOpen" ? 1 + (value - 1) * Strength : value * Strength;
        string Track(string keys) => string.Join(' ', keys.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(key =>
        {
            var pair = key.Split(':');
            double time = double.Parse(pair[0], CultureInfo.InvariantCulture) / Speed;
            double value = double.Parse(pair[1], CultureInfo.InvariantCulture) * Strength;
            return FormattableString.Invariant($"{time:0.######}:{value:0.######}");
        }));
        return source with
        {
            Seconds = source.Seconds / Speed,
            ExpressionStrength = source.ExpressionStrength * Strength,
            BlushStrength = Face.Length == 0 ? source.BlushStrength * Strength : null,
            Expressions = Face.Length == 0 ? source.Expressions : Face == "无表情" ? [] : [Face],
            Parameters = source.Parameters.Select(item => (item.Id, Scale(item.Id, item.Value))).ToArray(),
            Motion = source.Motion?.Select(item => (item.Id, Track(item.Keys))).ToArray()
        };
    }

    internal static double ExpressionAmount(string name, double strength) => name switch
    {
        // Discrete eye artwork appears only once the expression is strong enough. Do not fade two eye types together.
        "＞＜" or "圈圈眼" or "爱心眼" => strength >= .5 ? 1 : 0,
        "脸红" or "黑脸" or "流泪" or "流冷汗" => Math.Clamp(strength, 0, 1),
        _ => 1
    };
}

internal sealed class ActionTuning
{
    internal static readonly string[] Faces = ["脸红", "＞＜", "爱心眼", "圈圈眼", "黑脸", "流泪", "流冷汗", "无表情"];
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly string path;
    private readonly Dictionary<string, ActionStyle> styles;

    internal ActionTuning(string path)
    {
        this.path = path;
        try
        {
            styles = File.Exists(path) ? JsonSerializer.Deserialize<Dictionary<string, ActionStyle>>(File.ReadAllText(path), Json) ?? [] : [];
            foreach (var key in styles.Where(item => item.Value == null || !Allowed(item.Value)).Select(item => item.Key).ToArray())
                styles.Remove(key);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            styles = [];
            Places.Log("无法读取动作调整：" + error.GetType().Name);
        }
    }

    internal ActionStyle Get(string name) => styles.GetValueOrDefault(name) ?? ActionStyle.Default;
    internal bool Custom(string name) => styles.ContainsKey(name);
    internal static bool Allowed(ActionStyle style) => style.Valid && (style.Face.Length == 0 || Faces.Contains(style.Face));

    internal void Save(string name, ActionStyle? style)
    {
        if (style != null && !Allowed(style)) throw new ArgumentException("动作设置超出可调整范围。");
        var updated = new Dictionary<string, ActionStyle>(styles);
        if (style == null || style == ActionStyle.Default) updated.Remove(name);
        else updated[name] = style;
        string temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(updated, Json));
        File.Move(temporary, path, overwrite: true);
        styles.Clear();
        foreach (var item in updated) styles.Add(item.Key, item.Value);
    }
}
