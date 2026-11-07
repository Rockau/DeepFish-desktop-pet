using System;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace FishPet;

// 记忆小本本接到工具上（本机加，2026-11-08）：remember / recall / forget，get_state 里只带条数。
// 只有这三个工具和 get_state 会碰它；hooks（event）、说话、待机、托盘别的操作都不碰。第一次用到才读文件，每天第一次用时顺手整理一次。
internal sealed partial class PetWindow
{
    private MemoryBook? memoryBook;
    private MemoryBook Memory => memoryBook ??= test ? MemoryBook.Load(null, null)
        : MemoryBook.Load(Path.Combine(Places.Root, "memory.json"), Path.Combine(Places.Root, "memory-archive.json"));

    private static int? OptionalInt(JsonElement args, string name) =>
        args.TryGetProperty(name, out var value) && value.TryGetInt32(out var number) ? number : null;

    private object MemoryCommand(string name, JsonElement args)
    {
        var now = DateTime.Now;
        var book = Memory;
        book.Tidy(now);
        long stamp = MemoryBook.Unix(now);
        switch (name)
        {
            case "remember":
                var (item, replaced, fresh) = book.Remember(Tools.RequiredText(args, "content", 200), Tools.OptionalText(args, "category"), Tools.OptionalText(args, "slot"),
                    OptionalInt(args, "importance"), Tools.OptionalText(args, "source"), Tools.OptionalText(args, "date"), "ai", now);
                if (fresh && !standby && !speech.IsSaying) body.Apply("微点头"); // 记下了
                return Tools.Text(Compact(("id", item.Id), ("remembered", item.Content), ("replaced", replaced), ("already", fresh ? null : true), ("count", book.Count)));
            case "recall":
                bool old = args.TryGetProperty("archive", out var flag) && flag.ValueKind == JsonValueKind.True;
                var found = book.Recall(Tools.OptionalText(args, "query"), OptionalInt(args, "limit") ?? 5, old, now);
                // 只写有的字段：给模型看的每个字都是 token
                return Tools.Text(new
                {
                    memories = found.Select(entry => Compact(
                        ("id", entry.Item.Id), ("content", entry.Item.Content), ("category", entry.Item.Category),
                        ("guess", entry.Item.Source == "guess" ? true : null), ("date", entry.Item.Date),
                        ("ago", MemoryBook.Ago(entry.Item.CreatedAt, stamp)),
                        ("past", entry.Old ? (entry.Item.Why == "superseded" ? "以前的，已被新的取代" : entry.Item.Why == "forgotten" ? "已删掉的" : "很久以前的") : null))).ToArray(),
                    count = book.Count,
                });
            case "forget":
                var (changed, done) = book.Forget(Tools.RequiredText(args, "id", 40), Tools.OptionalText(args, "content"), Tools.OptionalText(args, "source"), now);
                if (done == "forgotten" && !standby && !speech.IsSaying) body.Apply("缩一下");
                return Tools.Text(new { id = changed.Id, done, content = changed.Content, count = book.Count });
        }
        throw new ArgumentException("没有这个工具。");
    }

    private static System.Collections.Generic.Dictionary<string, object> Compact(params (string Key, object? Value)[] fields) =>
        fields.Where(field => field.Value != null).ToDictionary(field => field.Key, field => field.Value!);

    private object MemoryState() => new { count = Memory.Count, archived = Memory.Archived };
}
