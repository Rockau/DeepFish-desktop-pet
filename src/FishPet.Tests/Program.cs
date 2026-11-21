// Her reflexes and her body language, checked without a window or a renderer: what commands they send.
using FishPet;

int checks = 0;
void Check(bool condition, string message) { if (!condition) throw new Exception(message); checks++; }
VerifyLive2DBehavior();
VerifyWhatHerBodyTellsHer();
VerifyUpdateReminders();
VerifyIdleSelfTalk();
VerifyConditions();
VerifyBond();
VerifyMemory();
VerifyMemoryView();
VerifyAlarms();
VerifyWelcome();
VerifyDance();
VerifyBeatTracker();
VerifyPerformanceLibrary();
Console.WriteLine($"FishPet: {checks} checks passed.");

void VerifyPerformanceLibrary()
{
    const string model = "大肥鱼";
    const string action = PerformanceLibrary.Action;
    const string skit = PerformanceLibrary.Skit;
    var directory = Directory.CreateTempSubdirectory("deepfish-library-");
    var path = Path.Combine(directory.FullName, "library.json");
    PerformanceLibrary Open(string file) => new(file, model, ["ParamMouthForm"],
        (_, reference) => reference == "Cue:点头", name => Body.CueReadable(name));
    try
    {
        var library = Open(path);
        var item = new PerformanceItem("点头练习", Parameters: new() { ["ParamMouthForm"] = .4 },
            Tracks: new() { ["FaceAngleY"] = [new(0, 0), new(.5, -10), new(1, 0)] }, Seconds: 1);
        var package = PerformanceLibrary.Package(new(1, action, model, [item]));
        Check(library.Import(action, package, []).SequenceEqual(["点头练习"]), "import a numeric gesture");
        Check(library.Import(action, package, []).SequenceEqual(["点头练习（2）"]), "same name imports as a copy");
        Check(PerformanceLibrary.ToMood(Open(path).Entries(action)[0]).Motion!.Single() ==
            ("FaceAngleY", "0:0 0.5:-10 1:0"), "saved keyframes reload in renderer format");

        string before = File.ReadAllText(path);
        void Reject(PerformancePack pack)
        {
            try { library.Import(action, PerformanceLibrary.Package(pack), []); }
            catch (ArgumentException)
            {
                Check(File.ReadAllText(path) == before && library.Entries(action).Length == 2, "a failed import leaves the entire library unchanged");
                return;
            }
            throw new Exception("bad performance package was accepted");
        }
        Reject(new(1, action, model, [item, item with { Parameters = new() { ["ViewX"] = 100 } }]));
        Reject(new(1, action, "另一款模型", [item]));
        Reject(new(1, action, model, [item with { Tracks = new() { ["FaceAngleY"] = [new(.5, 0), new(.5, -10)] } }]));
        Reject(new(1, action, model, [item with { Parameters = new() { ["GhostParameter"] = .5 } }]));

        const string reservedName = "歪头:左";
        Check(Body.CueReadable(reservedName) && Body.CueReadable(reservedName + "（2）"), "the cue parser ignores the suffix of a motion modifier");
        var alias = library.Add(action, [item with { Name = reservedName }], []).Single();
        Check(!Body.CueReadable(alias), "importing a reserved cue name assigns a usable name");

        var script = "@道具 单片眼镜\n准备好了！|点头|1\n|微笑|2";
        var scene = new PerformanceItem("彩排", Script: script);
        library.Add(skit, [scene], []);
        library.SetSkitRemoved(scene.Name, true);
        var restored = Open(path);
        Check(restored.RemovedSkits.SequenceEqual([scene.Name]) && restored.Entries(skit).Single().Script == script, "removing a skit persists without losing its script");
        restored.SetSkitRemoved(scene.Name, false);
        Check(Open(path).RemovedSkits.Count == 0, "restoring a skit survives a reload");

        var folder = new DirectoryInfo(AppContext.BaseDirectory);
        while (folder != null && !File.Exists(Path.Combine(folder.FullName, "skits.txt"))) folder = folder.Parent;
        Check(folder != null, "shipped skits located");
        var stock = SkitScript.ReadStock(Path.Combine(folder!.FullName, "skits.txt"));
        var visible = stock.Where(entry => !entry.Hidden).Select(entry => entry.Item).ToArray();
        Check(visible.Any(entry => entry.Name == "跳舞") && stock.Any(entry => entry.Hidden && entry.Item.Condition == "生日"), "legacy shows and hidden seasonal shows retain their settings");
        var shared = Open(Path.Combine(directory.FullName, "imported.json"));
        Check(shared.Import(skit, shared.Export(skit, visible), visible.Select(entry => entry.Name)).Length == visible.Length,
            "the whole visible stock skit collection can be shared and reimported");
        const string oldScript = "慢慢来。|点头|18";
        var normalized = SkitScript.ForSharing(oldScript, SkitScript.Parse(oldScript, strict: false));
        Check(SkitScript.Parse(normalized, strict: true).Steps.Single().Seconds == 10, "old long timings export as played");

        var chineseScript = string.Join('\n', Enumerable.Repeat(new string('鱼', 200) + "|点头|1", 20));
        var longItems = Enumerable.Range(1, 6).Select(number => new PerformanceItem($"长剧本 {number}", Script: chineseScript)).ToArray();
        var escapedPackage = System.Text.Json.JsonSerializer.SerializeToElement(shared.Export(skit, longItems));
        Check(escapedPackage.GetRawText().Length > PerformanceLibrary.MaxPackageBytes &&
            shared.Import(skit, escapedPackage, []).Length == longItems.Length, "JSON Unicode escaping does not inflate the import size limit");
    }
    finally
    {
        foreach (var file in directory.GetFiles()) file.Delete();
        directory.Delete();
    }
}

// 待机自言自语：模型现编的「话|动作|道具」怎么解析，数据文件里的动作词她都做得出来，抽选能抽到小剧场。
void VerifyIdleSelfTalk()
{
    Check(Body.CueReadable(Body.MutterVocabulary, 100), "every word offered to the model is one she can do");
    Check(Body.CueReadable("皱眉 看左边 竖手指", 5) && !Body.CueReadable("皱眉 大哭", 5) && !Body.CueReadable("", 5), "a cue she cannot read is turned down");
    Check(!Body.CueReadable("微笑 点头 眨眼 挑眉 皱眉 歪头", 5), "too many words in one cue are turned down");

    var full = Mutter.Parse("哼哼，这案子我接了|皱眉 看左边 竖手指|单片眼镜");
    Check(full is { Text: "哼哼，这案子我接了", Cue: "皱眉 看左边 竖手指" } && full.Props.SequenceEqual(["单片眼镜"]), "text, cue and prop are taken apart");
    var wrongProp = Mutter.Parse("嗯|微笑|魔法棒");
    Check(wrongProp is { Cue: "微笑" } && wrongProp.Props.Length == 0, "a prop she does not have is dropped");
    var wrongCue = Mutter.Parse("“只有话”|乱七八糟的词|兔牙");
    Check(wrongCue is { Text: "只有话", Cue: null } && wrongCue.Props.SequenceEqual(["兔牙"]), "words kept when the cue is not readable");
    Check(Mutter.Parse("  ") == null && Mutter.Parse(null) == null && Mutter.Parse("|微笑") == null, "no words, nothing said");

    // The data files she reads: every cue is something her body can do, every prop is one she has.
    var root = AppContext.BaseDirectory;
    while (root != null && !File.Exists(Path.Combine(root, "mutter.txt"))) root = Path.GetDirectoryName(root);
    Check(root != null, "mutter.txt is found");
    foreach (var line in File.ReadAllLines(Path.Combine(root!, "mutter.txt")).Where(line => line.Contains('|') && !line.StartsWith("//")))
    {
        var parts = line.Split('|', 4);
        Check(Body.CueReadable(parts[1].Trim(), 8), "mutter.txt cue is readable: " + line);
        if (parts.Length >= 3) Check(parts[2].Split(' ', StringSplitOptions.RemoveEmptyEntries).All(PetProps.Names.Contains), "mutter.txt prop exists: " + line);
        if (parts.Length == 4) Check(Condition.Parse(parts[3], out _) != null, "mutter.txt condition is understood: " + line);
    }
    foreach (var title in File.ReadAllLines(Path.Combine(root!, "skits.txt")).Where(line => line.StartsWith('#') && line.Contains('@')))
        Check(Condition.Parse(title[(title.IndexOf('@') + 1)..], out _) != null, "skits.txt condition is understood: " + title);
    int steps = 0;
    foreach (var line in File.ReadAllLines(Path.Combine(root!, "skits.txt")).Select(line => line.Trim()).Where(line => line.Length > 0 && !line.StartsWith("//") && !line.StartsWith('#')))
    {
        if (line.StartsWith("@道具")) { Check(line[3..].Split(' ', StringSplitOptions.RemoveEmptyEntries).All(PetProps.Names.Contains), "skits.txt prop exists: " + line); continue; }
        var parts = line.Split('|', 3);
        Check(parts.Length >= 2 && Body.CueReadable(parts[1].Trim(), 8), "skits.txt cue is readable: " + line);
        steps++;
    }
    Check(steps >= 20, "skits.txt has a good many steps");

    // Left alone for long enough she picks every kind: a move, a line, a little play.
    var sent = new List<string>(); var texts = new List<string>(); var plays = new List<string>(); var moves = new List<string>();
    var start = new DateTime(2026, 11, 5, 4, 0, 0, DateTimeKind.Utc);
    var tickNow = start;
    Behavior? live2d = null;
    live2d = new Behavior(sent.Add, () => [], new Random(7))
    {
        TextIdleMoods = ["甲", "乙"], TextIdle = texts.Add, SkitIdleMoods = ["戏一", "戏二"],
        // 真实运行时 PetWindow 会在开演前调用 SkitPlayed，测试里也补上。
        SkitIdle = name => { plays.Add(name); live2d!.SkitPlayed(tickNow); },
        IdleStarted = moves.Add,
    };
    live2d.Resume(start, 40);
    for (int i = 1; i <= 400; i++) { tickNow = start.AddSeconds(i * 41); live2d.Tick(tickNow, false, 40); }
    Check(texts.Count > 0 && plays.Count > 0 && moves.Count > 0, "she picks a line, a little play and a move");
    Check(plays.Distinct().Count() == 2 && texts.Distinct().Count() == 2, "every one of them comes up");
    // 三类比例 35:35:30（小动作、嘀咕、小剧场；用户 2026-11-14 改，400 次里允许 8% 的随机偏差）。
    int all = plays.Count + texts.Count + moves.Count;
    Check(Math.Abs(moves.Count - all * .35) < all * 0.08 && Math.Abs(texts.Count - all * .35) < all * 0.08 && Math.Abs(plays.Count - all * .30) < all * 0.08,
        $"move / line / play split 35:35:30: {moves.Count}/{texts.Count}/{plays.Count}");
    // 外面发来的事件（hooks）：events.txt 的格式、冷却、不连着演同一条。
    var book = EventBook.Parse(["// 注释", "# 甲 30 优先", "你好|微笑 点头|4", "|看过来", "乱写|乱七八糟的词|2", "# 乙", "只有话", "# 空的", "|"]);
    Check(book.Count == 2 && book["甲"] is { Cooldown: 30, Priority: true } && book["乙"] is { Cooldown: 0, Priority: false }, "event headers: name, cooldown, priority");
    Check(book["甲"].Lines.Length == 3 && book["甲"].Lines[0] is { Text: "你好", Cue: "微笑 点头", Seconds: 4 }, "event line: words, cue, seconds");
    Check(book["甲"].Lines[1] is { Text: "", Cue: "看过来", Seconds: EventBook.DefaultSeconds } && book["甲"].Lines[2] is { Text: "乱写", Cue: null }, "event line: cue only; unreadable cue dropped");
    var eventBook = new EventBook();
    var eventPath = Path.Combine(Path.GetTempPath(), "fish-events-" + Guid.NewGuid().ToString("N") + ".txt");
    File.WriteAllLines(eventPath, ["# 甲 30", "一|微笑", "# 乙 0", "二|微笑", "三|微笑"]);
    eventBook.Load(eventPath);
    var now0 = new DateTime(2026, 11, 6, 12, 0, 0, DateTimeKind.Utc);
    Check(eventBook.TryGet("甲", out var jia) && !eventBook.Cooling(jia, now0), "event is ready before it is played");
    eventBook.Played(jia, now0);
    Check(eventBook.Cooling(jia, now0.AddSeconds(29)) && !eventBook.Cooling(jia, now0.AddSeconds(31)), "event cools down for its seconds");
    Check(eventBook.TryGet("乙", out var yi) && !eventBook.Cooling(yi, now0), "no cooldown means ready");
    eventBook.Played(yi, now0);
    Check(!eventBook.Cooling(yi, now0), "no cooldown means always ready");
    var rnd = new Random(3); string? previous = null; bool repeated = false;
    for (int i = 0; i < 200; i++) { var chosen = eventBook.Choose(yi, rnd).Text; repeated |= chosen == previous; previous = chosen; }
    Check(!repeated, "the same event line is not played twice in a row");
    // 「轮流」：一轮里每条演一遍不重复，下一轮第一条不和上一轮最后一条相同。
    var rotating = EventBook.Parse(["# 轮 0 轮流", "一|微笑", "二|微笑", "三|微笑", "四|微笑", "五|微笑"])["轮"];
    Check(rotating.Rotate, "rotate flag is read from the header");
    var rotateBook = new EventBook(); var rotateRandom = new Random(11); var played = new List<string>();
    for (int i = 0; i < 50; i++) played.Add(rotateBook.Choose(rotating, rotateRandom).Text);
    bool everyRoundFull = Enumerable.Range(0, 10).All(round => played.Skip(round * 5).Take(5).Distinct().Count() == 5);
    bool noRepeatAcross = Enumerable.Range(1, 49).All(i => played[i] != played[i - 1]);
    Check(everyRoundFull && noRepeatAcross, "rotating event: every line once per round, never twice in a row");
    Check(!eventBook.TryGet("没有", out _), "an unknown event is not played");
    File.Delete(eventPath);
    // events.txt 本身：每个事件名 hook 脚本都会发来；每句动作都读得懂。
    var shipped = EventBook.Parse(File.ReadAllLines(Path.Combine(root!, "events.txt")));
    foreach (var name in new[] { "授权", "等你", "完成", "子任务完成", "出错", "开工", "收到" })
        Check(shipped.TryGetValue(name, out var shippedDef) && shippedDef.Lines.Length > 0, "events.txt has event: " + name);
    foreach (var entry in File.ReadAllLines(Path.Combine(root!, "events.txt")).Where(text => text.Contains('|') && !text.TrimStart().StartsWith("//")))
        Check(Body.CueReadable(entry.Split('|', 3)[1].Trim(), 8), "events.txt cue is readable: " + entry);
    Check(shipped.TryGetValue("思考一分钟", out var loading) && loading.Rotate && loading.Lines.Length == 3 && loading.Lines.All(line => line.Text.Length == 0 && line.Cue != null && line.Cue.Contains("加载")), "events.txt: three loading moves for the first minute, no words, taking turns");
    var loadFace = new List<string>(); var loadBody = new Body(loadFace.Add); loadBody.Apply("半睁 加载");
    Check(loadFace.Contains("exp 加载 1") && loadBody.Overlays.Contains("加载"), "the loading icon is a word of her body");
    loadBody.Relax();
    Check(loadFace.Contains("exp 加载 0") && !loadBody.Overlays.Contains("加载"), "and it goes away with the rest when she relaxes");
    Check(shipped["开工"].Rotate && shipped["收到"].Rotate, "events.txt: the start-of-work and got-it lines take turns");
    Check(shipped.TryGetValue("压缩开始", out var packing) && packing.Lines.Length == 2 && packing.Cooldown == 0 && packing.Lines.All(line => line.Cue != null && line.Cue.Contains("加载")), "events.txt: two compact-start lines, no cooldown, each with the loading icon");
    Check(shipped.TryGetValue("压缩完成", out var packed) && packed.Lines.Length == 2 && packed.Cooldown == 0 && packed.Lines.All(line => line.Text.Length > 0 && line.Cue != null), "events.txt: two compact-done lines, no cooldown");
    Check(shipped["授权"].Priority && !shipped["出错"].Priority && !shipped["完成"].Priority, "only the event that needs her owner is priority");

    // Not while she is speaking.
    int before = plays.Count + texts.Count + moves.Count;
    for (int i = 401; i <= 450; i++) live2d.Tick(start.AddSeconds(i * 41), true, 40);
    Check(plays.Count + texts.Count + moves.Count == before, "nothing of her own while she speaks");
}

void VerifyConditions()
{
    // Conditions (when a line may be said): spec -> parsed, or null with the word she did not understand.
    Check(Condition.Parse("", out _) is { Plain: true } && Condition.Parse("  ", out _) is { Plain: true }, "no condition means always");
    Check(Condition.Parse("深夜 周末 火星", out var bad) == null && bad == "火星", "an unknown word is reported, not ignored");
    Check(Condition.Parse("13月1日", out _) == null && Condition.Parse("2月30日", out _) == null && Condition.Parse("24点", out _) == null, "impossible dates and hours are refused");
    DateTime At(int y, int mo, int d, int h = 12, int mi = 0) => new(y, mo, d, h, mi, 0, DateTimeKind.Local);
    // Periods and hours.
    var night = Condition.Parse("深夜", out _)!;
    Check(night.Matches(At(2026, 11, 6, 23)) && night.Matches(At(2026, 11, 6, 0)) && night.Matches(At(2026, 11, 6, 4)) && !night.Matches(At(2026, 11, 6, 5)) && !night.Matches(At(2026, 11, 6, 22)), "深夜 is 23 to 4");
    var periods = new[] { ("清晨", 5), ("上午", 8), ("中午", 11), ("下午", 14), ("傍晚", 17), ("晚上", 19) };
    foreach (var (name, from) in periods) Check(Condition.Parse(name, out _)!.Matches(At(2026, 11, 6, from)) && !Condition.Parse(name, out _)!.Matches(At(2026, 11, 6, from - 1)), "period starts on the hour: " + name);
    Check(Condition.Parse("8点", out _)!.Matches(At(2026, 11, 6, 8, 40)) && !Condition.Parse("8点", out _)!.Matches(At(2026, 11, 6, 9)), "hour of day");
    // Weekdays (2026-11-06 is a Friday).
    Check(Condition.Parse("周五", out _)!.Matches(At(2026, 11, 6)) && !Condition.Parse("周四", out _)!.Matches(At(2026, 11, 6)), "a named weekday");
    Check(Condition.Parse("工作日", out _)!.Matches(At(2026, 11, 6)) && !Condition.Parse("工作日", out _)!.Matches(At(2026, 11, 7)) && Condition.Parse("周末", out _)!.Matches(At(2026, 11, 8)), "workdays and the weekend");
    // OR inside a kind, AND between kinds.
    var fridayEvening = Condition.Parse("周五 傍晚 晚上", out _)!;
    Check(fridayEvening.Matches(At(2026, 11, 6, 20)) && fridayEvening.Matches(At(2026, 11, 6, 18)) && !fridayEvening.Matches(At(2026, 11, 6, 12)) && !fridayEvening.Matches(At(2026, 11, 7, 20)), "or inside a kind, and between kinds");
    // Dates: fixed, solar festivals, lunar ones from the system calendar.
    Check(Condition.Parse("10月21日", out _)!.Matches(At(2026, 10, 21, 0)) && Condition.Parse("10月21日", out _)!.Matches(At(2027, 10, 21, 23)) && !Condition.Parse("10月21日", out _)!.Matches(At(2026, 10, 22)), "a fixed date, any year");
    Check(Condition.Parse("国庆", out _)!.Matches(At(2026, 10, 1)) && Condition.Parse("圣诞", out _)!.Matches(At(2026, 12, 25)) && Condition.Parse("跨年", out _)!.Matches(At(2026, 12, 31)), "solar festivals");
    foreach (var (name, date) in new[] { ("春节", At(2026, 2, 17)), ("除夕", At(2026, 2, 16)), ("元宵", At(2026, 3, 3)), ("端午", At(2026, 6, 19)), ("七夕", At(2026, 8, 19)), ("中秋", At(2026, 9, 25)), ("重阳", At(2026, 10, 18)), ("春节", At(2027, 2, 6)), ("中秋", At(2028, 10, 3)), ("除夕", At(2029, 2, 12)) })
        Check(Condition.Parse(name, out _)!.Matches(date) && !Condition.Parse(name, out _)!.Matches(date.AddDays(3)), $"lunar festival {name} on {date:yyyy-MM-dd}");
    // Weight: plain 1, time/weekday 4, a date 15; the chime flag keeps a line out of the ordinary draw.
    Condition.Birthday = null;
    Check(!Condition.Parse("生日", out _)!.Matches(At(2026, 10, 21, 9)), "no birthday set, never");
    Condition.Birthday = "10-21";
    Check(Condition.Parse("生日", out _)!.Matches(At(2026, 10, 21, 9)) && !Condition.Parse("生日", out _)!.Matches(At(2026, 10, 22, 9)), "the user's birthday");
    Condition.Birthday = null;
    Check(Condition.NormalBirthday("10-21") == "10-21" && Condition.NormalBirthday("1/5") == "01-05" && Condition.NormalBirthday("10月21日") == "10-21" && Condition.NormalBirthday("1021") == "10-21" && Condition.NormalBirthday("13-01") == null && Condition.NormalBirthday("") == null, "birthday formats");
    Check(Condition.Always.Boost == 1 && Condition.Parse("深夜", out _)!.Boost == 4 && Condition.Parse("周五 晚上", out _)!.Boost == 4 && Condition.Parse("国庆", out _)!.Boost == 15 && Condition.Parse("10月21日 晚上", out _)!.Boost == 15, "boost by kind");
    Check(Condition.Parse("整点 8点", out _) is { Chime: true, Plain: false } && !Condition.Parse("深夜", out _)!.Chime, "the chime flag");

    // Behavior: the draw follows the weights; the chime plays once an hour, early in the hour, when she is free.
    var start = new DateTime(2026, 11, 6, 12, 1, 0, DateTimeKind.Local).ToUniversalTime();
    var sent = new List<string>(); var texts = new List<string>(); var plays = new List<string>(); var moves = new List<string>();
    var weighted = new Behavior(sent.Add, () => [], new Random(11))
    {
        TextIdleMoods = ["甲", "乙", "丙"], TextIdle = texts.Add, SkitIdleMoods = ["戏"], SkitIdle = plays.Add, IdleStarted = moves.Add,
        TextWeight = (name, _) => name switch { "甲" => 15, "乙" => 1, _ => 0 }, SkitWeightOf = (_, _) => 0,
    };
    weighted.Resume(start, 40);
    for (int i = 1; i <= 600; i++) weighted.Tick(start.AddSeconds(i * 41), false, 40);
    Check(texts.Count(text => text == "甲") > 4 * texts.Count(text => text == "乙") && !texts.Contains("丙"), "a boosted line comes up more, a line out of season never");
    Check(plays.Count == 0 && texts.Count > 0 && moves.Count > 0, "a kind with nothing to draw is skipped");

    var chimed = new List<string>(); var blocked = false;
    var chimer = new Behavior(sent.Add, () => [], new Random(5)) { TextIdle = chimed.Add, ChimeTexts = local => local.Hour is 12 or 13 ? [$"报时{local.Hour}"] : [], ChimeBlocked = () => blocked };
    chimer.Resume(start, 600);
    chimer.Tick(start, false, 600);
    chimer.Tick(start.AddSeconds(30), false, 600);
    Check(chimed.SequenceEqual(["报时12"]), "the chime plays once in its hour");
    chimer.Tick(start.AddHours(1), false, 600);
    Check(chimed.SequenceEqual(["报时12", "报时13"]), "and again the next hour");
    chimer.Tick(start.AddHours(2), false, 600);
    Check(chimed.Count == 2, "no chime for an hour without one");
    blocked = true;
    var later = new Behavior(sent.Add, () => [], new Random(5)) { TextIdle = chimed.Add, ChimeTexts = local => ["迟到"], ChimeBlocked = () => blocked };
    later.Resume(start, 600);
    later.Tick(start, false, 600);
    Check(!chimed.Contains("迟到"), "no chime while she is busy");
    blocked = false;
    later.Tick(start.AddSeconds(90), false, 600);
    Check(chimed.Contains("迟到"), "she chimes as soon as she is free");
    int count = chimed.Count;
    var missed = new Behavior(sent.Add, () => [], new Random(5)) { TextIdle = chimed.Add, ChimeTexts = local => ["错过"], ChimeBlocked = () => false };
    missed.Resume(start, 600);
    missed.Tick(start.AddMinutes(4), false, 600);
    Check(!chimed.Contains("错过") && chimed.Count == count, "a chime that is more than a few minutes late is not made up");

    // The shipped chimes: every hour of every day of the week has one, and an ordinary line never carries the chime flag by mistake.
    var dataRoot = AppContext.BaseDirectory;
    while (dataRoot != null && !File.Exists(Path.Combine(dataRoot, "mutter.txt"))) dataRoot = Path.GetDirectoryName(dataRoot);
    var chimes = File.ReadAllLines(Path.Combine(dataRoot!, "mutter.txt")).Where(line => line.Contains('|') && !line.StartsWith("//"))
        .Select(line => line.Split('|', 4)).Where(parts => parts.Length == 4).Select(parts => Condition.Parse(parts[3], out _)!).Where(condition => condition.Chime).ToArray();
    for (int day = 0; day < 7; day++)
        for (int hour = 0; hour < 24; hour++)
            Check(chimes.Any(condition => condition.Matches(At(2026, 11, 8 + day, hour, 1))), $"a chime for {At(2026, 11, 8 + day, hour):ddd} {hour}:00");
}

void VerifyLive2DBehavior()
{
    var sent = new List<string>();
    var worn = new List<string>();
    var start = new DateTime(2026, 11, 2, 12, 0, 0, DateTimeKind.Utc);
    var live2d = new Behavior(sent.Add, () => worn, new Random(1));
    live2d.Resume(start, 40);
    bool Said(string command) => sent.Contains(command);

    foreach (var mood in Behavior.Moods)
    {
        Check(mood.Value.Motion is { Length: > 0 } && !mood.Value.Expressions.Contains("加载"), "every mood moves her body: " + mood.Key);
        foreach (var (id, keys) in mood.Value.Motion!)
        {
            var parts = keys.Split(' ');
            Check(parts[0].StartsWith("0:") && parts[^1].EndsWith(":0"), $"{mood.Key} {id} starts and ends at rest");
            var times = parts.Select(key => double.Parse(key.Split(':')[0], System.Globalization.CultureInfo.InvariantCulture)).ToArray();
            Check(times.Zip(times.Skip(1)).All(pair => pair.First < pair.Second), $"{mood.Key} {id} runs forward");
        }
    }
    // Swinging round her feet looked like a roly-poly (the owner, 2026-11-02): only the head pat he liked keeps a little of it.
    Check(Behavior.Moods.Where(mood => mood.Key != "摸头").All(mood => mood.Value.Motion!.All(track => track.Id is not ("ViewRoll" or "ViewX"))), "no roly-poly swaying");
    // She jumps only when startled, and heart eyes are for love, which none of her reflexes is (the owner, 2026-11-02).
    Check(Behavior.Moods.Where(mood => mood.Value.Motion!.Any(track => track.Id == "ViewY")).Select(mood => mood.Key).Order().SequenceEqual(["打瞌睡", "震惊"]), "jumping only in fright");
    // 例外是最高好感连戳胸部触发的“娇羞”。
    // 摸头摸满 8 秒后松手的“摸后腻歪”也是爱意，同样可以有爱心眼（用户定的，2026-11-10）。
    Check(Behavior.Moods.Where(mood => mood.Key is not ("娇羞" or "摸后腻歪")).All(mood => !mood.Value.Expressions.Contains("爱心眼")), "no heart eyes without love");
    Check(Behavior.Zone("small", .5, .05, worn) == "head" && Behavior.Zone("small", .5, .24, worn) == "face", "small head and face");
    Check(Behavior.Zone("small", .5, .31, worn) == "face" && Behavior.Zone("small", .5, .33, worn) == "body", "small mouth and chin are the face, the collar is the body");
    Check(Behavior.Zone("small", .5, .5, worn) == "skirt" && Behavior.Zone("small", .85, .75, worn) == "tail", "small skirt and tail");
    Check(Behavior.Zone("small", .22, .58, worn) == "hand" && Behavior.Zone("small", .5, .8, worn) == "body", "small hand and legs");
    Check(Behavior.Zone("chibi", .5, .42, worn) == "face" && Behavior.Zone("chibi", .5, .2, worn) == "head", "chibi head and face");
    Check(Behavior.Zone("full", .5, .2, worn) == "face" && Behavior.Zone("full", .1, .7, worn) == "tail", "full body zones");
    Check(Behavior.Zone("small", .85, .75, ["尾巴开关"]) == "skirt" && Behavior.Zone("full", .1, .7, ["尾巴开关"]) == "skirt", "no tail to touch once it is put away");
    Check(Behavior.Zone("small", .5, .8, ["裙子切换"]) == "skirt" && Behavior.Zone("small", .5, .4, ["裙子切换"]) == "body", "a long skirt covers her legs, not her chest");

    live2d.Click("head", start, level: Affection.Levels.Length);
    Check(live2d.Showing == "摸头" && Said("exp 脸红 1") && Said("set EyeSmile 0.9"), "stroking her head");
    Check(sent.Any(command => command.StartsWith("anim ViewRoll ")), "her body moves with her face");
    live2d.Tick(start.AddSeconds(1), false, 40);
    Check(live2d.Showing == "摸头", "a mood stays for its time");
    sent.Clear();
    live2d.Tick(start.AddSeconds(3.1), false, 40);
    Check(live2d.Showing == null && Said("exp 脸红 0") && Said("clear EyeSmile"), "a mood ends and leaves nothing on");

    sent.Clear();
    live2d.Click("face", start.AddSeconds(4), 1);
    Check(Said("set FaceAngleX -14") && Said("set FaceAngleZ -8") && Said("anim FaceAngleX 0:0 .08:-12 .3:-6 1.2:0"), "poked on the screen's left cheek she turns toward that side");
    live2d.Click("tail", start.AddSeconds(4.2), level: 0);
    Check(Said("exp ＞＜ 0") && live2d.Showing == "震惊", "a new reaction replaces the one before");
    Check(!Said("exp 圈圈眼 1") && Said("set ParamMouthForm -0.74"), "a negative tail click turns away with a plain frown");
    Check(!sent.Any(command => command.StartsWith("anim ViewY ")), "movements do not pile up on quick clicks");
    for (int i = 0; i < 3; i++) live2d.Click("body", start.AddSeconds(4.4 + i * .2));
    Check(live2d.Showing == "挨打" && Said("exp 钢盆 1") && Said("anim ViewSquash 0:0 .09:.11 .3:.02 .5:.05 .75:.01 1.4:0"), "clicking too often brings the basin down");
    sent.Clear();
    worn.Add("钢盆");
    live2d.Tick(start.AddSeconds(9), false, 40);
    Check(!Said("exp 钢盆 0") && Said("exp 流冷汗 0"), "what the owner put on her stays after a mood used it");
    worn.Clear();

    sent.Clear();
    live2d.Activity("video", true);
    Check(Said("exp 黑框眼镜 1"), "glasses on while she watches");
    live2d.Show("开心", start.AddSeconds(10));
    Check(!Said("exp 黑框眼镜 0"), "a reaction leaves them on");
    live2d.Activity("writing", true);
    live2d.Activity("video", false);
    Check(!Said("exp 黑框眼镜 0"), "still on while she writes");
    live2d.Activity("writing", false);
    Check(Said("exp 黑框眼镜 0"), "off when everything is done");
    sent.Clear();
    worn.Add("单片眼镜");
    live2d.Activity("share", true);
    Check(!Said("exp 黑框眼镜 1"), "no second pair over the owner's");
    live2d.ClearActivities();
    worn.Clear(); worn.Add("黑框眼镜");
    sent.Clear();
    live2d.Activity("share", true);
    live2d.Activity("share", false);
    Check(sent.Count == 0, "the owner's own pair is never taken off");
    worn.Clear();
    live2d.Activity("bilibili", true);
    sent.Clear();
    live2d.ClearActivities();
    Check(Said("exp 黑框眼镜 0"), "nobody left to say the work ended");

    sent.Clear();
    live2d.Click("skirt", start.AddSeconds(12));
    // The owner found swinging round her feet like a roly-poly the least natural (2026-11-02).
    Check(live2d.Showing == "护裙" && Said("motion 默认手切举手") && !sent.Any(command => command.StartsWith("anim ViewRoll ") || command.StartsWith("anim ViewX ")), "touching her skirt she guards it without swaying");
    live2d.Tick(start.AddSeconds(14), false, 40);
    sent.Clear();
    live2d.DragStart();
    Check(live2d.Showing == null && Said("exp 圈圈眼 1") && Said("set ParamMouthOpenY 0.35") && !sent.Any(command => command.StartsWith("animloop ")), "dragged she goes dizzy and hangs (the swing is driven live by PetWindow.Touch now)");
    live2d.DragEnd(start.AddSeconds(16)); live2d.DragEnd(start.AddSeconds(16.1));
    Check(live2d.Showing == "落地" && sent.Count(command => command.StartsWith("anim ViewSquash ")) == 1 && Said("animstop") && Said("clear JawOpen"), "and lands once");
    Check(!Said("exp 圈圈眼 0"), "still dizzy as she lands");
    live2d.Tick(start.AddSeconds(17.3), false, 40);
    Check(sent.Count(command => command == "exp 圈圈眼 0") == 1, "and blinks it away");
    // 本机加（2026-11-10）：转晕了放下，不播“落地”（头晕由 PetWindow.Touch 接手）；抚摸/头晕期间待机小动作别插进来；悬停的小反应每个部位都有。
    var dazed = new Behavior(sent.Add, () => worn, new Random(5));
    dazed.Resume(start, 1);
    dazed.DragStart(); sent.Clear();
    dazed.DragEnd(start.AddSeconds(20), dizzy: true);
    Check(dazed.Showing == null && Said("animstop") && Said("exp 圈圈眼 1") && !sent.Any(command => command.StartsWith("anim ViewSquash ")), "dazed she does not play the plain landing");
    dazed.Touching = true; sent.Clear();
    dazed.Tick(start.AddSeconds(500), false, 1);
    Check(sent.Count == 0 && dazed.Showing == null, "no small move while she is being stroked or dizzy");
    dazed.Touching = false;
    foreach (var zone in new[] { "head", "face", "hand", "chest", "skirt", "tail", "body" })
        Check(Behavior.Moods.ContainsKey(Behavior.HoverMood(zone, 1)), "a hover reaction for " + zone);
    foreach (var after in new[] { "摸后失落", "摸后不舍", "摸后腻歪" })
        Check(Behavior.Moods.TryGetValue(after, out var afterMood) && afterMood.Expressions.Contains("脸红"), after + " blushes");
    foreach (var shape in Shape.All)
    {
        var box = Behavior.HeadBox(shape.Id);
        Check(box.Rx > 0 && box.Ry > 0 && box.Y - box.Ry > 0 && box.Y + box.Ry < .6, "a head box inside the frame for " + shape.Id);
    }
    Check(Behavior.Moods["告别"].Gesture != null && Behavior.Moods["告别"].Motion!.Any(track => track.Id == "EyeOpen"), "a maid's greeting and goodbye");

    var right = new Behavior(sent.Add, () => worn, new Random(3));
    sent.Clear();
    right.Resume(start, 40);
    right.Click("face", start, -1);
    Check(Said("set FaceAngleX 14") && Said("set FaceAngleZ 8") && Said("anim FaceAngleX 0:0 .08:12 .3:6 1.2:0"), "poked on the screen's right cheek she turns toward that side");
    Check(Said("anim FaceAngleY 0:0 .08:-6 .4:-2 1.2:0") && Said("anim ViewSquash 0:0 .06:.04 .25:0 1.2:0"), "only her turning flips, not her flinch");
    var earlyFacePokes = Behavior.FacePokeActions(0).ToArray();
    int stageAt50 = Affection.LevelOf(50), stageAt100 = Affection.LevelOf(100);
    string slightSurprise = Behavior.FacePokeActions(stageAt50).Single();
    var squeezedFacePokes = Behavior.FacePokeActions(stageAt100).ToArray();
    Check(slightSurprise == "戳脸·轻微惊讶" && squeezedFacePokes.SequenceEqual(["戳脸·挤眼惊呼", "戳脸·挤眼噘嘴"]),
        "50 points reacts with slight surprise and 100 with the squeezed-eye faces");
    Check(right.NextFacePoke(0, start) == earlyFacePokes[0] && right.NextFacePoke(stageAt100, start) == squeezedFacePokes[0]
        && right.NextFacePoke(0, start) == earlyFacePokes[1] && right.NextFacePoke(stageAt100, start) == squeezedFacePokes[1],
        "the low and familiar stages each alternate their own expressions");
    double MaxTurn(string name) => Behavior.Moods[name].Motion!.Single(track => track.Id == "FaceAngleX").Keys
        .Split(' ').Max(key => Math.Abs(double.Parse(key.Split(':')[1], System.Globalization.CultureInfo.InvariantCulture)));
    Check(MaxTurn(earlyFacePokes[0]) < MaxTurn(slightSurprise) && squeezedFacePokes.All(name => MaxTurn(name) > MaxTurn(slightSurprise)),
        "face-poke turns get larger from the initial stage through 50 and 100 points");
    sent.Clear();
    right.Show(squeezedFacePokes[0], start.AddSeconds(2), side: -1, force: true);
    Check(Said("anim FaceAngleX 0:0 .11:20 .37:19 .57:15 .80:8 1.10:0"),
        "the stronger turn holds briefly and still mirrors to the cheek that was poked");
    int topStage = Affection.Levels.Length;
    foreach (var zone in new[] { "head", "hand", "tail", "chest" })
    {
        var byLevel = Enumerable.Range(0, topStage + 1).Select(level =>
        {
            var reflex = new Behavior(_ => { }, () => worn, new Random(5));
            var touch = AffectionTouchReactions.ForZone(zone, level)!.Value;
            string click = reflex.ClickReaction(zone, start, level);
            Check(click == touch.Click && Behavior.Moods.ContainsKey(click)
                && AffectionTouchReactions.ClickForEvent(touch.Event, level) == click,
                zone + " selects the click shown in affection settings");
            string hover = Behavior.HoverMood(zone, level);
            Check(hover == (touch.Hover ?? "悬停身") && Behavior.Moods.ContainsKey(hover),
                zone + " selects the hover for this affection level");
            return (click, hover);
        }).ToArray();
        Check(byLevel.Select(reaction => reaction.click).Distinct().Count() == topStage + 1,
            zone + " has a distinct click at each stage");
        if (zone is "head" or "hand" or "tail")
            Check(byLevel.Select(reaction => reaction.hover).Distinct().Count() == topStage + 1,
                zone + " has a distinct hover at each stage");
    }
    var negative = new Behavior(sent.Add, () => worn, new Random(5));
    sent.Clear();
    negative.Click("head", start, level: 0);
    Check(negative.Showing == "点头部·黑脸" && Said("exp 黑脸 1") &&
        Behavior.Moods[Behavior.HoverMood("face", 0)].Expressions.SequenceEqual(["黑脸"]),
        "a negative score blackens both the head-click response and the face hover");
    Check(Enumerable.Range(0, 3).All(level => !Affection.AllowsInitiative(level))
        && Affection.AllowsInitiative(3), "proactive hover and idle start at the familiar stage");
    Check(Enumerable.Range(1, 3).All(level => Behavior.HoverMood("head", level) == Behavior.HoverMood("face", level)
        && !Behavior.Moods[Behavior.HoverMood("face", level)].Expressions.Contains("脸红")),
        "hovering over the face or head shares a stage and starts without blushing");
    var invitation = Behavior.Moods[Behavior.HoverMood("head", topStage)];
    Check(invitation.Motion is { } invitationMotion && invitationMotion.Any(track => track.Id == "EyeOpen" && track.Keys.Contains(":-1"))
        && invitationMotion.Any(track => track.Id == "LeanInput" && track.Keys.Contains(":-18")),
        "the top hover leans forward and closes both eyes");
    Check(earlyFacePokes.SequenceEqual(["戳脸·半睁眼嫌弃", "戳脸·皱眉瞪眼"])
        && Behavior.FacePokeActions(1).SequenceEqual(["戳脸·侧目"]), "the sketches are reserved for negative affection");
    var guard = Behavior.Moods[AffectionTouchReactions.ClickForEvent("点胸口", 0)!];
    var waryChest = Behavior.Moods[AffectionTouchReactions.ClickForEvent("点胸口", 1)!];
    foreach (var zone in new[] { "body", "waist" })
    {
        var negativeClick = Behavior.ClickMood(zone, 0);
        Check(negativeClick == "点身体·黑脸" && Behavior.Moods[negativeClick].Expressions.SequenceEqual(new[] { "黑脸" }),
            $"the negative {zone} click frowns under full shade");
        Check(Behavior.ClickMood(zone, 1) == "心虚", $"the neutral {zone} click still looks startled");
        Check(Enumerable.Range(0, 6).All(i => live2d.ClickReaction(zone, DateTime.UnixEpoch.AddMilliseconds(i * 120), 0) == negativeClick),
            $"rapid negative {zone} clicks still show the dark reaction");
    }
    Check(guard.Parameters.Contains(("EyeOpen", .38)) && guard.Gesture != null && guard.Parameters.Contains(("ParamMouthForm", -1))
        && waryChest.Expressions.Length == 0, "negative chest taps look down and recoil with a hostile frown");
    Check(AffectionTouchReactions.ClickForEvent("摸手", 0) == "点手·缩手" &&
        AffectionTouchReactions.ClickForEvent("摸手", 1) == "点手·迟疑", "the strongest hand rejection belongs to the negative band");
    Check(HeadStrokeFace.At(0, 0, false, 4.99, 0).Build().Expressions.Length == 0 &&
        HeadStrokeFace.At(0, 0, true, 5, 0).Build().Expressions.SequenceEqual(["黑脸"]),
        "the negative hold changes from a cold look to black face after sustained rubbing");
    var impatient = HeadStrokeFace.At(0, 1, true, 30, 1);
    var negativeRelease = HeadStrokeFace.ReleaseAt(0, 30, .999, "抚摸头5");
    Check(impatient.Blush == 0 && impatient.Melt == 0 && impatient.Build().Parameters["LeanInput"] > 0
        && negativeRelease.Mood == HeadStrokeFace.NegativeReleaseMood
        && Behavior.Moods[negativeRelease.Mood].Expressions.SequenceEqual(["黑脸"]),
        "extended negative strokes and their release stay dark and lean away instead of melting");
    var smirk = Behavior.Moods[AffectionTouchReactions.ClickForEvent("点胸口", 3)!];
    var bashful = Behavior.Moods[AffectionTouchReactions.ClickForEvent("点胸口", topStage)!];
    Check(guard.Expressions.SequenceEqual(["黑脸"]) && guard.ExpressionStrength == 1 && guard.HoldMouth,
        "the unfamiliar chest response uses only the full dark face and holds the frown");
    Check(smirk.Parameters.Contains(("ParamMouthForm", 1)) && smirk.HoldMouth
        && bashful.Expressions.Contains("脸红"), "the chest interaction goes from smirking to a familiar blush");
    Check(AffectionTouchReactions.ClickForEvent("摸手", topStage) == "开心"
        && AffectionTouchReactions.ClickForEvent("摸尾巴", 0) == "震惊", "the recognizable hand and tail reactions are reused at their matching levels");
    Check(right.NextFacePoke(topStage, start) == "戳脸" && right.NextFacePoke(topStage, start.AddSeconds(1)) == "戳脸",
        "the top stage starts with the original face-poke action");
    string blushPoke = right.NextFacePoke(topStage, start.AddSeconds(3));
    var originalPoke = Behavior.Moods["戳脸"];
    var blushingPoke = Behavior.Moods[blushPoke];
    Check(blushingPoke.Expressions.SequenceEqual(originalPoke.Expressions.Append("脸红"))
        && blushingPoke.Parameters.SequenceEqual(originalPoke.Parameters) && blushingPoke.Motion == originalPoke.Motion,
        "a third top-stage poke adds blush to the original face and movement");
    Check(right.NextFacePoke(topStage, start.AddSeconds(8)) == "戳脸", "old pokes expire before new top-stage pokes");
    double[] blushStarts = [5, 3.5, 2.4, 1.4, .7];
    for (int level = 1; level <= topStage; level++)
    {
        double onset = blushStarts[level - 1];
        var beforeBlush = HeadStrokeFace.At(level, 0, false, onset - .01, 0);
        var startedBlush = HeadStrokeFace.At(level, 0, false, onset, 0);
        Check(beforeBlush.Blush == 0 && !beforeBlush.Build().Expressions.Contains("脸红")
            && startedBlush.Blush == .25 && startedBlush.Build().Expressions.Contains("脸红"),
            "head strokes start blushing at the threshold for each affection level");
    }
    var startled = HeadStrokeFace.At(2, 1, false, 1, 0).Build();
    var squeezed = HeadStrokeFace.At(3, 1, false, 1, 0).Build();
    Check(startled.Parameters.ContainsKey("ParamEyeLOpen") && startled.Expressions.Length == 0
        && squeezed.Expressions.SequenceEqual(["＞＜"]) && !squeezed.Parameters.ContainsKey("EyeOpen"),
        "the open-eye looks follow the touch side, and the squeezed-eye artwork stays visible");
    var smilingHead = HeadStrokeFace.At(topStage, 0, false, 1, 0).Build();
    var smilingFace = HeadStrokeFace.At(topStage, 0, true, 1, 0).Build();
    Check(smilingHead.Parameters["EyeOpen"] == .45 && smilingFace.Parameters["EyeOpen"] == 0
        && smilingFace.Parameters["EyeSmile"] == 1 && smilingFace.Parameters["ParamMouthForm"] == 1
        && smilingFace.Expressions.SequenceEqual(["脸红"]),
        "the highest level restores the original smiling eyes and closes them when stroked on the face");
    var smilingRight = HeadStrokeFace.At(topStage, 1, false, 1, 0).Build();
    var smilingLeft = HeadStrokeFace.At(topStage, -1, false, 1, 0).Build();
    Check(smilingRight.Parameters["ParamEyeLOpen"] == 0 && !smilingRight.Parameters.ContainsKey("ParamEyeROpen")
        && smilingLeft.Parameters["ParamEyeROpen"] == 0 && !smilingLeft.Parameters.ContainsKey("ParamEyeLOpen"),
        "the original wink mirrors which side of the head is being stroked");
    var relaxed = HeadStrokeFace.At(topStage, 0, false, 20, 1).Build();
    Check(relaxed.Expressions.SequenceEqual(["脸红"]) && relaxed.Parameters["LeanInput"] == -17
        && relaxed.Parameters["EyeOpen"] == 0 && relaxed.Parameters["EyeSmile"] == 1
        && relaxed.Parameters["MouthPressLipOpen"] == .4, "long strokes still melt into the original closed-eye smile and lean");
    (double Mouth, double Jaw) StrokeMouth(int level, double seconds, double melt = 0)
    {
        var face = HeadStrokeFace.At(level, 0, false, seconds, melt).Build();
        return (face.Parameters["ParamMouthOpenY"], face.Parameters["JawOpen"]);
    }
    for (int level = 1; level <= topStage; level++)
    {
        var startMouth = StrokeMouth(level, .1);
        var firstPart = StrokeMouth(level, .55);
        var laterPart = StrokeMouth(level, 1.2);
        var open = StrokeMouth(level, 2);
        Check(startMouth == (0, 0) && firstPart.Mouth > 0 && firstPart.Mouth < laterPart.Mouth
            && laterPart.Mouth < open.Mouth && firstPart.Jaw < laterPart.Jaw && laterPart.Jaw < open.Jaw,
            "mouth and jaw ease from closed to the stage's own opening");
    }
    var strokePreview = HeadStrokeFace.Preview(3);
    var previewOpen = strokePreview.Motion!.Single(track => track.Id == "ParamMouthOpenY").Keys;
    Check(strokePreview.Parameters.Contains(("ParamMouthOpenY", 0)) && previewOpen.StartsWith("0:0 ")
        && previewOpen.Contains("1.95:0.69") && previewOpen.EndsWith(" 3:0"),
        "the stroke preview animates the same mouth opening instead of setting the final pose immediately");
    var smilePreview = HeadStrokeFace.Preview(topStage);
    Check(smilePreview.Parameters.Contains(("EyeOpen", 0)) && smilePreview.Parameters.Contains(("EyeSmile", 1))
        && smilePreview.Expressions.SequenceEqual(["脸红"]),
        "the highest-level action preview shows her original closed-eye smile");
    Check(StrokeMouth(3, 20, 1) == (0, 0)
        && Behavior.Moods["戳脸·挤眼惊呼"].Parameters.Contains(("ParamMouthOpenY", .94)),
        "melting closes the stroke mouth and a poke still goes straight to its round-mouth expression");
    for (int level = 1; level <= 2; level++)
        Check(HeadStrokeFace.ReleaseAngerChance(level, 1) < HeadStrokeFace.ReleaseAngerChance(level, 4)
            && HeadStrokeFace.ReleaseAngerChance(level, 4) < HeadStrokeFace.ReleaseAngerChance(level, 8),
            "unfamiliar levels get more likely to be annoyed by longer strokes");
    for (int level = 4; level <= topStage; level++)
        Check(HeadStrokeFace.ReleaseAngerChance(level, 1) > HeadStrokeFace.ReleaseAngerChance(level, 4)
            && HeadStrokeFace.ReleaseAngerChance(level, 4) > HeadStrokeFace.ReleaseAngerChance(level, 8),
            "close levels get more likely to be annoyed by shorter strokes");
    Check(HeadStrokeFace.ReleaseAngerChance(1, 8) > HeadStrokeFace.ReleaseAngerChance(2, 8)
        && HeadStrokeFace.ReleaseAngerChance(topStage, 1) > HeadStrokeFace.ReleaseAngerChance(4, 1),
        "the extreme levels have the stronger preference");
    Check(HeadStrokeFace.ReleaseAt(1, 1, .50).Mood == "摸后嫌弃"
        && HeadStrokeFace.ReleaseAt(1, 8, .50).Mood == "摸太久生气"
        && HeadStrokeFace.ReleaseAt(1, 8, .95).Mood == "摸后放松",
        "low affection may react with disdain, irritation, or relaxation depending on time and chance");
    Check(HeadStrokeFace.ReleaseAt(topStage, 1, .50).Mood == "没摸够生气"
        && HeadStrokeFace.ReleaseAt(topStage, 1, .95).Mood == "摸后失落"
        && HeadStrokeFace.ReleaseAt(topStage, 8, 0).Key == "抚摸头3",
        "a short close stroke may trigger playful anger and a long one uses the existing affectionate response");
    Check(HeadStrokeFace.ReleaseAt(3, 2.99, 0).Key == "抚摸头1"
        && HeadStrokeFace.ReleaseAt(3, 3, 0).Key == "抚摸头2"
        && HeadStrokeFace.ReleaseAt(3, 8, 0).Key == "抚摸头3",
        "the familiar middle level uses the existing duration-based lines");
    Check(HeadStrokeFace.ReleaseAt(1, 2, .50, "抚摸头4").Mood == "摸太久生气"
        && HeadStrokeFace.ReleaseAt(1, 20, .95, "抚摸头4").Mood == HeadStrokeFace.MeltReleaseMood
        && HeadStrokeFace.ReleaseAt(topStage, 30, 0, "抚摸头5").Key == "抚摸头5",
        "reaching the long-stroke bonus still allows low-affection anger and keeps the original melt lines");
    var blushingSqueeze = HeadStrokeFace.At(4, 0, true, 1.6, 0);
    var (closeParameters, closeExpressions) = blushingSqueeze.Build();
    var pose = new Mood(closeExpressions, closeParameters.Select(pair => (pair.Key, pair.Value)).ToArray(),
        BlushStrength: blushingSqueeze.Blush);
    sent.Clear();
    new Behavior(sent.Add, () => worn, new Random(3)).Show("摸头互动", start, preview: pose);
    Check(Said("exp ＞＜ 1") && Said(FormattableString.Invariant($"expamount 脸红 {blushingSqueeze.Blush:0.###}")),
        "a light blush keeps the squeezed-eye expression at full weight");
    sent.Clear();
    right.Click("hand", start.AddSeconds(10), level: topStage);
    Check(Said("motion 默认手切举手"), "clicked on the hand she raises it");
    right.Tick(start.AddSeconds(14), false, 40);
    Check(Said("motion 举手切默认手"), "and lowers it again");

    var quiet = new Behavior(sent.Add, () => worn, new Random(2));
    sent.Clear();
    quiet.Resume(start, 40);
    quiet.Tick(start.AddSeconds(100), true, 40);
    Check(sent.Count == 0, "no small move while she speaks");
    quiet.Tick(start.AddSeconds(30), false, 40);
    Check(sent.Count == 0, "none before the interval");
    quiet.Tick(start.AddSeconds(41), false, 40);
    Check(sent.Count > 0, "a small move after the interval");

    // A hand lifted on its own for no reason looked odd (the owner, 2026-11-02): only a move that needs it
    // brings one up. She never does the same thing twice in a row, and late at night she gets sleepy.
    var idle = new Behavior(sent.Add, () => worn, new Random(5));
    idle.Resume(start, 40);
    string? previous = null;
    bool repeated = false, strayHand = false, grass = false;
    var done = new HashSet<string>();
    for (int i = 0; i < 300; i++)
    {
        var at = start.AddSeconds(41 + i * 50);
        idle.Tick(at.AddSeconds(-20), false, 40);
        sent.Clear();
        idle.Tick(at, false, 40);
        var name = idle.Showing!;
        repeated |= name == previous;
        previous = name;
        done.Add(name);
        strayHand |= sent.Any(command => command.StartsWith("motion ")) && Behavior.Moods[name].Gesture == null;
        grass |= name == "玩狗尾草" && Said("motion 狗尾草切换出来") && Said("anim FaceAngleX 0:0 .5:0 1.2:-10 4.7:-10 5.4:0");
    }
    Check(done.Count == 11 && !repeated, "all her small moves come round, never one twice in a row");
    Check(!strayHand && grass, "a hand comes up only with a move that uses it");
    Check(Behavior.IdleWeight("打瞌睡", 2) > Behavior.IdleWeight("打瞌睡", 14)
        && Behavior.IdleWeight("张望", 2) == Behavior.IdleWeight("张望", 14), "sleepy late at night only");

    // Her own body language (the owner reviewed it, 2026-11-02): words in 〔〕, each a hand-tuned piece, stacked
    // by the rules learned on this model, and her words waiting for what her body does.
    var acting = new List<string>();
    var body = new Body(acting.Add);
    var clickCommands = new List<string>();
    var clickBody = new Body(clickCommands.Add);
    clickBody.Apply("圈圈眼 冷汗 黑脸 抚胸", 0, '\0', plainFace: true);
    Check(clickCommands.Where(command => command.StartsWith("exp ")).SequenceEqual(new[] { "exp 黑脸 1" })
        && clickCommands.Contains("motion 默认手切举手"), "custom negative click cues keep the gesture and shadow");
    clickBody.Apply("圈圈眼");
    Check(clickCommands.Contains("exp 圈圈眼 1"), "normal body cues can still display special eyes");
    bool Did(string command) => acting.Contains(command);
    double waited = body.Apply("歪头:右 眯眼笑 点头");
    Check(Did("set FaceAngleZ -16") && Did("set EyeOpen 0") && Did("set EyeSmile 1"), "a cue becomes her body, directions as the owner sees her");
    Check(Did("anim FaceAngleY 0:0 0.17:-10 0.4:1 0.63:-10 0.86:1 1.06:0") && Math.Abs(waited - 1.06) < 1e-9, "a nod is two, and her words wait for both");
    acting.Clear();
    Check(body.Apply("快点头×3") < 1.06 && acting.Single().Split(":-10").Length == 4, "quicker and counted");
    acting.Clear();
    body.Apply("笑眼 转头:左 看左边");
    Check(Did("set EyeOpen 0.45") && !acting.Any(command => command.StartsWith("set EyeSmile")) && Did("set FaceAngleX 27"), "a new look on the same place replaces the old, looking aside while turned adds up");
    acting.Clear();
    body.Apply("脸红 大笑 微微笑");
    Check(Did("exp 脸红 1") && body.Overlays.Contains("脸红") && Did("set ParamMouthForm 0.5") && !Did("set ParamMouthOpenY 0.6"), "大笑 is a word of its own, 微 makes 微笑 smaller, the later mouth wins");
    acting.Clear();
    body.Apply("吐舌 屑屑眼 -脸红");
    Check(!Did("set TongueOut 1") && Did("exp 屑屑眼 1") && Did("exp 脸红 0"), "the smug eyes bring their own mouth, and a minus takes one thing off");
    acting.Clear();
    Check(body.Apply("抚胸") >= 1.1 && Did("motion 默认手切举手") && body.HandUp, "her hand comes up and she waits for it");
    body.Apply("竖手指");
    Check(acting.SkipWhile(command => command != "motion 举手切默认手").Contains("motion 竖手指切出来动画"), "one hand at a time");
    acting.Clear();
    body.Apply("叉腰");
    Check(acting.Count == 0 && body.Unknown.Contains("叉腰"), "what her body cannot do is left out and noted");
    body.Apply("歪头:右");
    acting.Clear();
    body.Restore();
    Check(Did("set FaceAngleZ -16"), "after a reflex her own look comes back");
    acting.Clear();
    body.Apply("恢复");
    Check(!body.Posing && Did("clear FaceAngleZ") && Did("exp 屑屑眼 0") && Did("motion 竖手指切回默认") && !acting.Any(command => command.StartsWith("exp 体型")),
        "back to how she is, leaving her body type alone");
    Check(Body.Strip("〔点头〕好呀〔微笑〕") == "好呀" && !Body.HasCues("好呀"), "the words without her cues");

    // Her moves go with her words (the owner, 2026-11-02: "动作要配合语气和句子长短").
    acting.Clear();
    Check(body.Apply("点头", 3) == .35 && acting.Single().EndsWith(" 1.696:0"), "a long sentence: the nod stretches over it and the words go on as it starts");
    acting.Clear();
    body.Apply("点头", .3);
    Check(acting.Single().EndsWith(" 0.742:0"), "a short one: quicker");
    acting.Clear();
    body.Apply("点头", 1.06, '！');
    Check(acting.Single().Contains(":-12 "), "said with a ！: bigger");
    acting.Clear();
    Check(body.Apply("吓一跳", 3) == .45 && acting.Any(command => command == "anim ViewY 0:0 0.06:0 0.2:0.06 0.4:0"), "a fright keeps its own speed");
    acting.Clear();
    body.Apply("慢点头", 3);
    Check(acting.Single().EndsWith(" 1.802:0"), "what she says of the speed herself wins");
    body.Apply("恢复");
    var heard = new List<(double Spoken, char Tone)>();
    var shouted = new Line("〔点头〕好呀！〔微笑〕嗯……", (cue, spoken, tone) => { heard.Add((spoken, tone)); return .35; });
    shouted.Advance(10);
    Check(heard.Count == 2 && Math.Abs(heard[0].Spoken - (2 / 9.0 + .12)) < 1e-9 && heard[0].Tone == '！' && heard[1].Tone == '…', "each cue knows the words it goes with");

    var cues = new List<string>();
    var said = new Line("〔点头〕好呀……嗯", (cue, _, _) => { cues.Add(cue); return 1; });
    said.Advance(0);
    Check(cues.SequenceEqual(["点头"]) && said.Shown == 0 && !said.Talking, "she acts before she speaks");
    said.Advance(.5);
    Check(said.Shown == 0, "and her words wait for it");
    said.Advance(1.05);
    Check(said.Plain == "好呀……嗯" && said.Shown == 1 && said.Talking, "then the words come at a speaking pace");
    said.Advance(1.3);
    Check(said.Shown == 3 && !said.Talking, "her mouth rests on the dots");
    said.Advance(5);
    Check(said.Done && said.Shown == said.Plain.Length, "all said");
    Check(Math.Abs(said.Spoken - (3 / 9.0 + .5)) < 1e-9, "how long the words take");
    var ending = new Line("好呀〔点头×2〕", (_, _, _) => 1.5);
    ending.Advance(.3);
    Check(ending.Shown == 2 && !ending.Done, "a move at the end has begun once the words are out");
    ending.Advance(1.6);
    Check(!ending.Done, "and the line is not over while it plays, so the next one waits");
    ending.Advance(2);
    Check(ending.Done, "then it is");
    var alone = new Line("〔点头〕", (_, _, _) => 1.5);
    alone.Advance(0);
    Check(!alone.Done, "a move without words takes its time too");
    alone.Advance(1.6);
    Check(alone.Done, "and ends with it");
    // What she says at once is a bubble a line, cues alone on a line going with the words after them.
    Check(Line.Split("〔看过来〕\n好呀〔点头〕\r\n\n  明天见  \n〔眯眼笑〕").SequenceEqual(["〔看过来〕好呀〔点头〕", "明天见〔眯眼笑〕"]), "several lines are several bubbles");
    Check(Line.Split("甲 / 乙／丙").SequenceEqual(["甲", "乙", "丙"]) && Line.Split("A/B 和 C/D").SequenceEqual(["A/B 和 C/D"]), "slashes split the bubbles, only the full-width one or one with spaces");
    Check(Line.Split("〔微笑〕 / 你好 / 〔点头〕").SequenceEqual(["〔微笑〕你好〔点头〕"]) && Line.HasSlashBreak("甲 / 乙") && !Line.HasSlashBreak("A/B"), "a slash line of only cues still goes with the words next to it");
    Check(Line.Split("好呀").SequenceEqual(["好呀"]) && Line.Split("〔点头〕").SequenceEqual(["〔点头〕"]), "one line, or cues alone, is one");

    var restored = 0;
    var underneath = new Behavior(sent.Add, () => worn, new Random(4)) { HandBusy = () => true, Ended = () => restored++ };
    underneath.Resume(start, 40);
    sent.Clear();
    underneath.Click("hand", start, level: Affection.Levels.Length);
    Check(underneath.Showing == "开心" && !Said("motion 默认手切举手"), "a reflex leaves the hand she has up");
    underneath.Tick(start.AddSeconds(3.1), false, 40);
    Check(restored == 1, "and gives her look back when it ends");
}

// What her body tells her after she speaks, and the guide she learns it from (2026-11-05).
void VerifyWhatHerBodyTellsHer()
{
    var unread = Body.Unread("〔瞒一眼:左 坏笑〕那这把〔微皂眉 摆手〕算你〔大哭 快点头×3 -脸红〕强");
    Check(unread.SequenceEqual([("瞒一眼:左", "瞟一眼"), ("微皂眉", null), ("摆手", null), ("大哭", null)]), "only the words her body cannot read, each with the one word a character away");
    Check(Body.Unread("〔歪一下头:右〕好〔恢复〕〔看过来〕").Count == 0, "nothing for words it knows");
    Check(Body.ActsWithin("〔鼓腮 转头:右〕打错字那叫〔挑一下眉〕活人感") && Body.ActsWithin("好呀〔眯眼笑〕"), "a look after some words moves within");
    Check(!Body.ActsWithin("〔坏笑〕两天两把轮椅") && !Body.ActsWithin("没写"), "only before the words, or none, does not");
    bool[] Lines(int within, int before) => [.. Enumerable.Repeat(true, within), .. Enumerable.Repeat(false, before)];
    Check(Body.NeedsActing(Lines(3, 1)) && Body.NeedsActing(Lines(1, 9)) && !Body.NeedsActing(Lines(2, 8)), "reminded until two of the last ten move within");
    var folder = new DirectoryInfo(AppContext.BaseDirectory);
    while (folder != null && !File.Exists(Path.Combine(folder.FullName, "prompt", "身体说明.md"))) folder = folder.Parent;
    Check(folder != null, "the body guide is found");
    var guide = File.ReadAllText(Path.Combine(folder!.FullName, "prompt", "身体说明.md"));
    Check(Body.GuideVersion(guide) == "2026-11-16-1" && guide.TrimEnd().EndsWith("【你的身体·完】（身体说明 2026-11-16-1 版）"), "the guide ends with its version");
    Check(guide.Contains("〔〕只写在 say 的 text 里") && guide.Contains("平时还能做的：") && guide.Contains("focus"), "brackets only in say, and the everyday tools");
    Check(guide.Contains("先调一次 get_state 看 hidden") && guide.Contains("很久没动静"), "checks whether she is out, and reports progress while working");
    Check(guide.Contains("〔〕里只能写下面这些词") && guide.Contains("挑衅") && guide.Contains("- 苦笑：微笑 八字眉"), "only what her body can do, and the small moves in full");
    var examples = guide.Split('\n').Select(line => line.TrimEnd('\r')).Where(line => line.StartsWith("- 【") && line.Contains('→')).ToArray();
    Check(examples.Length == 12, "twelve examples");
    foreach (var example in examples)
    {
        var said = example[(example.IndexOf('→') + 1)..].Split('／');
        Check(said.Length == 3 && said.Any(Body.ActsWithin), "three lines, a look within the words: " + example[..12]);
        Check(Body.Unread(string.Join("\n", said)).Count == 0, "every word in it her body can do: " + example[..12]);
    }
    Check(!guide.Contains("欢迎回来~") && !guide.Contains("作业写完了吗"), "the old short ones are gone");
}

// The update notice comes again each day it is not taken, and says when the body guide changed.
void VerifyUpdateReminders()
{
    using var withGuide = System.Text.Json.JsonDocument.Parse("""{"files":[{"filename":"src/FishPet/Body.cs"},{"filename":"prompt/身体说明.md"}]}""");
    using var without = System.Text.Json.JsonDocument.Parse("""{"files":[{"filename":"README.md"}]}""");
    Check(Updates.TouchesGuide(withGuide.RootElement) && !Updates.TouchesGuide(without.RootElement), "the body guide among the changes is seen");
    var updates = new Updates(Path.GetTempPath(), _ => { }) { Latest = new Updates.Found("aaaaaaa", "bbbbbbb", 2, ["新例子"], true) };
    var start = new DateTime(2026, 11, 5, 4, 0, 0, DateTimeKind.Utc);
    Check(updates.Unannounced(start) != null && updates.Unannounced(start.AddHours(1)) == null, "told once, not again at once");
    Check(updates.Unannounced(start.AddHours(25)) != null && updates.Unannounced(start.AddHours(26)) == null, "told again a day later");
    updates.Latest = updates.Latest! with { Latest = "ccccccc" };
    Check(updates.Unannounced(start.AddHours(26.5)) != null, "a newer one is told at once");
}

// 亲密度、番茄钟、连续工作提醒（本机加，2026-11-08）。
void VerifyBond()
{
    // 六档，负分档保留原来五档的编号
    Check(Affection.LevelOf(0) == 1 && Affection.LevelOf(19) == 1 && Affection.LevelOf(20) == 2 && Affection.LevelOf(60) == 3 && Affection.LevelOf(110) == 4 && Affection.LevelOf(160) == 5 && Affection.LevelOf(200) == 5, "five levels over 200 points");
    string[] neutralEvents = ["戳脸", "摸手", "摸尾巴", "点胸口", "换鞋", "挠脚", "挠腰", "捏脸", "拖裙摆"];
    Check(Affection.Rules.Where(rule => !neutralEvents.Contains(rule.Key))
        .All(rule => rule.Value.Points != 0), "original scoring events keep their default values");
    Check(neutralEvents.All(kind => Affection.Rules[kind] == (0, 0)),
        "new interactive scoring events start neutral");
    Check(!Affection.Rules.ContainsKey("戳裙子"), "the skirt no longer has a scoring event");
    var day = new DateTime(2026, 11, 8, 10, 0, 0);
    var a = new Affection();
    for (int i = 0; i < 9; i++) a.Apply("摸头", day.AddMinutes(i));
    Check(a.Points == 5 && a.TodayNet == 5, "head pats count five times a day");
    a.Apply("摸头", day.AddDays(1));
    Check(a.Points == 6 && a.TodayNet == 1, "a new day counts again");
    a.Apply(Affection.ChestPokeEvent, day.AddDays(1));
    Check(a.Points == 6, "a chest tap uses the neutral default score");
    a.Apply("挨打", day.AddDays(1));
    Check(a.Points == 4, "hitting takes points");
    for (int i = 0; i < 5; i++) a.Apply("挨打", day.AddDays(1));
    Check(a.Points == 2, "hitting counts twice a day at most");
    var zero = new Affection { Points = 1 };
    zero.Apply("挨打", day);
    Check(zero.Points == -1 && zero.Level == 0, "early losses enter the new lowest stage");
    zero.Apply("摸头", day);
    Check(zero.Points == 0 && zero.Level == 1 && zero.TodayGain == 1, "negative scores recover one point at a time");
    // 每天加分合计最多 15：摸头 5 + 聊天 6 + 番茄 3 + 按时睡 3 = 17，只算 15；扣分不占额度，扣完也不会多给
    var cap = new Affection { Points = 50 };
    for (int i = 0; i < 6; i++) { cap.Apply("摸头", day); cap.Apply("聊天", day); cap.Apply("番茄", day); }
    cap.Apply("按时睡", day);
    Check(cap.Points == 65 && cap.TodayGain == 15, "15 points a day at most");
    cap.Apply("熬夜", day); cap.Apply("聊天", day.AddMinutes(5));
    Check(cap.Points == 63 && cap.TodayNet == 13, "points taken away are not given back that day");
    cap.Apply("摸头", day.AddDays(1));
    Check(cap.Points == 64 && cap.TodayGain == 1, "a new day, a new 15");
    // 每天扣分合计最多 5：挨打 4 + 熬夜 2 = 6，只扣 5；扣满了加分照常
    var loss = new Affection { Points = 80 };
    loss.Apply("挨打", day); loss.Apply("挨打", day); loss.Apply("熬夜", day);
    Check(loss.Points == 75 && loss.TodayLoss == 5, "5 points taken a day at most");
    loss.Apply("摸头", day);
    Check(loss.Points == 76 && loss.TodayNet == -4, "a full day of losses still lets her gain");
    loss.Apply("挨打", day.AddDays(1));
    Check(loss.Points == 74 && loss.TodayLoss == 2, "a new day, a new 5");
    // 掉级最多掉回“磨合”
    var b = new Affection { Points = 22 };
    b.Apply("挨打", day); b.Apply("挨打", day);
    Check(b.Points == 20 && b.Level == 2, "once 磨合, never back to 初识");
    var c = new Affection { Points = 61 };
    c.Apply("挨打", day);
    Check(c.Points == 59 && c.Level == 2 && c.Pending.Count == 0, "dropping into a quiet stage also clears pending idle dialogue");
    var d = new Affection { Points = 199 };
    d.Apply("按时睡", day);
    Check(d.Points == 200, "200 at most");
    var e = new Affection { Points = 59 };
    e.Apply("聊天", day);
    Check(e.Level == 3 && e.Pending.SequenceEqual(["升级"]), "a new level is told");

    // 可按当前级别调整每种互动的分值、日次数和总额度；一次事件批次使用开始时的级别。
    var configured = new Affection
    {
        Points = 19,
        RuleFor = (level, kind) => kind == "聊天" ? (level == 1 ? 3 : 7, 2, kind) : null,
        DailyLimitsFor = level => level == 1 ? (4, 2) : (20, 6)
    };
    Check(configured.Rule(1, "聊天") == (3, 2) && configured.Rule(2, "聊天") == (7, 2), "each level has its own score rule");
    configured.Apply("聊天", day);
    Check(configured.Points == 22 && configured.TodayGain == 3, "cross-level event uses the prior level's score and daily limit");
    configured.Apply("聊天", day);
    Check(configured.Points == 29 && configured.TodayGain == 10, "the next event uses the new level");
    configured.Apply("聊天", day);
    Check(configured.Points == 29 && configured.Counts["聊天"] == 2, "custom occurrence cap preserves daily counts");
    var batch = new Affection
    {
        Points = 19,
        RuleFor = (level, kind) => (level == 1 ? 3 : 7, 0, kind),
        DailyLimitsFor = _ => (20, 20)
    };
    batch.Apply("聊天", day, times: 2);
    Check(batch.Points == 25, "an event batch resolves its tier once");
    var flipped = new Affection
    {
        Points = 80,
        RuleFor = (_, kind) => kind switch { "摸头" => (-3, 0, kind), "挨打" => (4, 0, kind), _ => null },
        DailyLimitsFor = _ => (5, 4)
    };
    flipped.Apply("摸头", day); flipped.Apply("摸头", day);
    Check(flipped.Points == 76 && flipped.TodayLoss == 4, "custom penalties use the configured loss budget");
    flipped.Apply("挨打", day); flipped.Apply("挨打", day);
    Check(flipped.Points == 81 && flipped.TodayGain == 5 && flipped.TodayLoss == 4, "custom gains and losses have separate budgets");
    var disabled = new Affection { RuleFor = (_, kind) => (0, 1, kind) };
    disabled.Apply("摸头", day);
    Check(disabled.Points == 0 && disabled.Counts["摸头"] == 1, "zero-score events still consume their configured occurrence");
    disabled.DailyLimitsFor = _ => (0, 0);
    disabled.RuleFor = (_, kind) => (2, 0, kind);
    disabled.Apply("聊天", day);
    Check(disabled.Points == 0, "zero daily budget prevents gains");
    int earned = flipped.TodayGain, lost = flipped.TodayLoss, counted = flipped.Counts.Values.Sum();
    var manual = flipped.SetPoints(7, day);
    Check(manual is { Before: 81, After: 7, Delta: -74 } && flipped.Level == 1, "manual score edits may return below the natural twenty-point floor");
    Check(flipped.TodayGain == earned && flipped.TodayLoss == lost && flipped.Counts.Values.Sum() == counted, "manual edits do not spend or restore daily allowances");
    Check(flipped.Pending.Count == 0, "manual edits do not create upgrade or downgrade announcements");
    flipped.SetPoints(200, day);
    Check(flipped.Level == 5, "manual score edit can select the highest level");
    bool manualRange = false;
    try { flipped.SetPoints(201, day); } catch (ArgumentOutOfRangeException) { manualRange = true; }
    Check(manualRange && flipped.Points == 200, "invalid manual score leaves state unchanged");

    // 睡觉：前一晚最后一次动静几点
    Affection Night(int hour, int minute)
    {
        var night = new Affection { Points = 70 };
        night.Seen(new DateTime(2026, 11, 7, 21, 0, 0));
        night.Seen(new DateTime(2026, 11, 7, 0, 0, 0).AddHours(hour).AddMinutes(minute));
        night.Seen(new DateTime(2026, 11, 8, 8, 0, 0));
        return night;
    }
    Check(Night(23, 40) is { Points: 73 } early && early.Pending.SequenceEqual(["早安夸奖"]), "asleep before 0:30: +3 and praise");
    Check(Night(24, 40) is { Points: 71 } coaxed && coaxed.Pending.SequenceEqual(["早安"]), "asleep 0:30–0:50: +1");
    Check(Night(24, 55) is { Points: 70 } late && late.Pending.Count == 0, "0:50–1:00: nothing");
    Check(Night(25, 30) is { Points: 68 } owl && owl.Pending.SequenceEqual(["熬夜念叨"]), "after 1:00: -2 and a nag");
    var once = Night(25, 30); once.Seen(new DateTime(2026, 11, 8, 9, 0, 0));
    Check(once.Points == 68 && once.Pending.Count == 1, "one night judged once");
    var quietAtNight = new Affection { Points = 0 };
    quietAtNight.Seen(new DateTime(2026, 11, 7, 23, 40, 0));
    quietAtNight.Seen(new DateTime(2026, 11, 8, 8, 0, 0));
    Check(quietAtNight.Points == 3 && quietAtNight.Pending.Count == 0,
        "quiet stages keep sleep scoring without accumulating greetings");
    var away = new Affection { Points = 50 };
    away.Seen(new DateTime(2026, 11, 7, 14, 0, 0)); away.Seen(new DateTime(2026, 11, 8, 8, 0, 0));
    Check(away.Points == 50 && away.Pending.Count == 0, "no evening, no judgement");
    // 冷落：隔了 5 天回来，第 3 天起每天 -2
    var lonely = new Affection { Points = 100 };
    lonely.Seen(new DateTime(2026, 11, 1, 15, 0, 0)); lonely.Seen(new DateTime(2026, 11, 6, 15, 0, 0));
    Check(lonely.Points == 95 && lonely.Pending.Contains("想你了"), "five days away: -6, but 5 a day at most, and she missed you");
    var path = Path.Combine(Path.GetTempPath(), "fish-affection-" + Guid.NewGuid().ToString("N") + ".json");
    lonely.Save(path);
    var back = Affection.Load(path);
    Check(back.Points == 95 && back.Pending.SequenceEqual(lonely.Pending) && back.LastActive == lonely.LastActive, "kept in affection.json");
    lonely.SetPoints(Affection.Min, day);
    lonely.Apply("挨打", day);
    lonely.Save(path);
    var lowest = Affection.Load(path);
    Check(lowest.Points == Affection.Min && lowest.Level == 0 && lowest.LevelFrom == Affection.Min &&
        lowest.NextAt == 0, "the negative floor survives a reload and points back to 初识");
    File.Delete(path);
    Check(Affection.Load(path).Points == 0, "no file, a fresh start");

    // 条件：亲密N
    var close = Condition.Parse("亲密3 深夜", out _)!;
    Check(close.MinLevel == 3 && !close.Plain && close.Boost == 4, "亲密3 is read");
    var lateNight = new DateTime(2026, 11, 8, 23, 30, 0);
    Check(close.Matches(lateNight, 3) && !close.Matches(lateNight, 2) && !close.Matches(lateNight.AddHours(-10), 5), "亲密 and the time both have to fit");
    Check(Condition.Parse("亲密6", out var bad) == null && bad == "亲密6", "only 亲密1 to 亲密5");

    // 最高好感时，连续戳胸才出现爱心眼。
    var sent = new List<string>();
    var pet = new Behavior(sent.Add, () => [], new Random(3));
    var now = DateTime.UtcNow;
    int highest = Affection.Levels.Length;
    pet.Click("chest", now, level: highest);
    Check(pet.Showing == "娇羞前倾" && !sent.Contains("exp 爱心眼 1"), "the first chest poke only blushes");
    pet.Click("chest", now.AddSeconds(1), level: highest);
    Check(pet.Showing == "娇羞抚胸" && !sent.Contains("exp 爱心眼 1"), "the second chest poke still has ordinary eyes");
    pet.Click("chest", now.AddSeconds(2), level: highest);
    Check(pet.Showing == "娇羞" && sent.Contains("exp 爱心眼 1"), "the third chest poke turns on heart eyes");
    pet.Click("chest", now.AddSeconds(7), level: highest);
    Check(pet.Showing == "娇羞前倾" && sent.Contains("exp 爱心眼 0"), "a pause starts a new chest-poke sequence");
    pet.Click("skirt", now.AddSeconds(8), level: highest);
    Check(pet.Showing == "护裙", "the skirt keeps its own response at the highest level");
    pet.Click("chest", now.AddSeconds(9), level: highest);
    Check(pet.Showing == "娇羞前倾", "another touch breaks the chest-poke sequence");
    pet.Click("chest", now.AddSeconds(10), level: highest - 1);
    Check(pet.Showing == "点胸口·害羞", "a lower affection level has its own response");
    pet.Click("chest", now.AddSeconds(11), level: highest);
    Check(pet.Showing == "娇羞前倾", "changing affection stages resets the count");
    pet.Interrupt();
    pet.Click("chest", now.AddSeconds(12), level: highest);
    Check(pet.Showing == "娇羞前倾", "an interrupted reaction does not carry a chest poke forward");
    var rapid = new Behavior(_ => { }, () => [], new Random(3));
    var burst = Enumerable.Range(0, 5).Select(i => rapid.ClickReaction("chest", now.AddMilliseconds(300 * i), highest));
    Check(burst.SequenceEqual(["娇羞前倾", "娇羞抚胸", "娇羞", "娇羞", "挨打"]),
        "heart eyes hold across the fourth click, and the fifth rapid click still gets the basin");
    foreach (int level in Enumerable.Range(0, highest))
    {
        var lower = new Behavior(_ => { }, () => [], new Random(3));
        Check(Enumerable.Range(0, 3).All(i => lower.ClickReaction("chest", now.AddSeconds(i), level) != "娇羞"),
            "heart eyes cannot be reached by a chest sequence at stage " + level);
    }
    Check(Behavior.Moods["娇羞前倾"].Motion!.Any(track => track.Id == "LeanInput" && track.Keys.Contains("-14")) && Behavior.Moods["娇羞抚胸"].Motion!.Any(track => track.Id == "LeanInput" && track.Keys.Contains("-21")), "one leans in, one leans in a lot");
    Check(Behavior.Moods["娇羞抚胸"].Parameters.Contains(("EyeOpen", .45)) && Behavior.Moods["娇羞抚胸"].Parameters.Contains(("EyeSmile", 1)), "leaning in a lot, with smiling eyes");
    var quiet = new List<string>(); var said = new List<string>(); var plays = new List<string>();
    var still = new Behavior(_ => { }, () => [], new Random(5)) { Quiet = true, IdleStarted = quiet.Add, TextIdleMoods = ["甲"], TextIdle = said.Add, SkitIdleMoods = ["戏"], SkitIdle = plays.Add, ChimeTexts = _ => ["报时"] };
    var t = new DateTime(2026, 11, 8, 2, 0, 30, DateTimeKind.Local).ToUniversalTime();
    for (int i = 0; i < 200; i++) still.Tick(t.AddSeconds(i * 20), false, 15);
    Check(quiet.Count > 20 && quiet.All(name => name is "思考" or "发呆" or "歪头") && said.Count == 0 && plays.Count == 0, "focusing: only quiet moves, no talk, no skits, no chimes");

    var passive = new Behavior(_ => { }, () => [], new Random(5))
    { IdleStarted = quiet.Add, TextIdle = said.Add, SkitIdle = plays.Add, ChimeTexts = _ => ["报时"] };
    quiet.Clear();
    passive.Resume(t, 15);
    for (int i = 0; i < 100; i++) passive.Tick(t.AddSeconds(i * 20), false, 15, allowIdle: false);
    Check(quiet.Count == 0 && said.Count == 0 && plays.Count == 0 && passive.Showing == null,
        "low affection skips automatic motions, dialogue, skits and chimes");

    // 番茄钟
    var timer = new FocusTimer();
    var start = new DateTime(2026, 11, 8, 2, 0, 0, DateTimeKind.Utc);
    Check(timer.Tick(start) == FocusTimer.Turn.None && timer.Now == FocusTimer.Phase.None, "not started");
    timer.Start(start);
    Check(timer.Now == FocusTimer.Phase.Work && timer.Left(start.AddMinutes(10)) == TimeSpan.FromMinutes(15), "25 minutes of work");
    Check(timer.Tick(start.AddMinutes(24)) == FocusTimer.Turn.None, "not yet");
    Check(timer.Tick(start.AddMinutes(25)) == FocusTimer.Turn.WorkDone && timer.Now == FocusTimer.Phase.Break && timer.RoundsToday == 1, "work done, a break");
    Check(timer.Tick(start.AddMinutes(30)) == FocusTimer.Turn.BreakDone && timer.Now == FocusTimer.Phase.None, "break done, waits for the next start");
    timer.Start(start, minutes: 12);
    Check(timer.Length.TotalMinutes == 12 && timer.Tick(start.AddMinutes(11)) == FocusTimer.Turn.None
        && timer.Tick(start.AddMinutes(12)) == FocusTimer.Turn.WorkDone && timer.Length.TotalMinutes == 5,
        "custom work duration reaches the normal break without changing the rest duration");
    timer.Start(start); timer.Stop();
    Check(timer.Now == FocusTimer.Phase.None && timer.Tick(start.AddHours(1)) == FocusTimer.Turn.None, "stopped early");
    timer.StartStopwatch(start);
    var fortyMinutes = start.AddMinutes(40);
    Check(timer.Left(fortyMinutes) == TimeSpan.Zero && timer.Elapsed(fortyMinutes) == TimeSpan.FromMinutes(40)
        && timer.Tick(fortyMinutes) == FocusTimer.Turn.None && timer.Now == FocusTimer.Phase.Work, "stopwatch counts up without a deadline");
    timer.Stop(fortyMinutes);
    Check(timer.Elapsed(start.AddHours(1)) == TimeSpan.FromMinutes(40), "stopped stopwatch holds its result");

    var zone = TimeZoneInfo.CreateCustomTimeZone("Fixed UTC+8", TimeSpan.FromHours(8), "UTC+8", "UTC+8");
    var beforeMidnight = new DateTime(2026, 11, 20, 15, 58, 0, DateTimeKind.Utc);
    var midnight = FocusTimer.NextTime(beforeMidnight, new TimeOnly(0, 1), zone);
    Check(midnight == beforeMidnight.AddMinutes(3), "target time crosses local midnight");
    Check(FocusTimer.NextTime(beforeMidnight, new TimeOnly(23, 57), zone) == beforeMidnight.AddMinutes(1439), "past clock time rolls to next day");
    timer.StartAt(beforeMidnight, midnight, alarm: false);
    Check(timer.Now == FocusTimer.Phase.Work && timer.Tick(midnight.AddSeconds(-1)) == FocusTimer.Turn.None
        && timer.Tick(midnight) == FocusTimer.Turn.DeadlineDone && timer.Now == FocusTimer.Phase.None, "scheduled focus ends at its time");
    timer.StartAt(beforeMidnight, midnight, alarm: true);
    Check(timer.Now == FocusTimer.Phase.Alarm && timer.Tick(midnight) == FocusTimer.Turn.AlarmDone
        && timer.Tick(midnight.AddSeconds(1)) == FocusTimer.Turn.None, "alarm rings once without entering focus");
    Check(timer.RoundsToday == 2, "only completed countdowns count as focus rounds");

    var transitionTime = new DateTime(1, 1, 1, 2, 0, 0);
    var summer = TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(new DateTime(2026, 1, 1), new DateTime(2026, 12, 31),
        TimeSpan.FromHours(1), TimeZoneInfo.TransitionTime.CreateFixedDateRule(transitionTime, 3, 8),
        TimeZoneInfo.TransitionTime.CreateFixedDateRule(transitionTime, 11, 1));
    var seasonal = TimeZoneInfo.CreateCustomTimeZone("Seasonal Time", TimeSpan.FromHours(-5), "Seasonal Time",
        "Standard Time", "Summer Time", [summer]);
    var beforeOverlap = new DateTime(2026, 11, 1, 4, 50, 0, DateTimeKind.Utc);
    Check(FocusTimer.NextTime(beforeOverlap, new TimeOnly(1, 30), seasonal) == beforeOverlap.AddMinutes(40)
        && FocusTimer.NextTime(beforeOverlap.AddHours(1), new TimeOnly(1, 30), seasonal) == beforeOverlap.AddMinutes(100),
        "scheduled clock uses the next real occurrence during a repeated local hour");

    // 连续工作 2 小时
    var work = new WorkStreak();
    bool due = false;
    for (int m = 0; m <= 115; m += 5) due |= work.Note(start.AddMinutes(m));
    Check(!due && work.Note(start.AddMinutes(120)), "two hours on end: time to stand up");
    work.Reminded(start.AddMinutes(120));
    Check(!work.Note(start.AddMinutes(125)), "told once");
    var gap = new WorkStreak();
    for (int m = 0; m <= 60; m += 5) gap.Note(start.AddMinutes(m));
    Check(!gap.Note(start.AddMinutes(90)), "a 20-minute gap starts again");
    for (int m = 95; m < 210; m += 10) gap.Note(start.AddMinutes(m));
    Check(gap.Note(start.AddMinutes(210)), "two hours after the gap");

    // 提示音是一段像样的 wav
    var wave = ChimeSound.Wave([880, 1318.5]);
    Check(wave.Length > 44 + 44100 && System.Text.Encoding.ASCII.GetString(wave, 0, 4) == "RIFF" && System.Text.Encoding.ASCII.GetString(wave, 8, 4) == "WAVE", "the chime is a wav");

    // 数据文件：新事件都在，touch.txt 的等级读得懂，戳胸有台词，亲密小剧场都是彩蛋
    var root = AppContext.BaseDirectory;
    while (root != null && !File.Exists(Path.Combine(root, "events.txt"))) root = Path.GetDirectoryName(root);
    var book = EventBook.Parse(File.ReadAllLines(Path.Combine(root!, "events.txt")));
    foreach (var name in new[] { "早安夸奖", "早安", "熬夜念叨", "想你了", "升级", "降级", "番茄开始", "番茄结束1分钟内", "番茄结束5分钟内", "番茄结束10分钟", "番茄结束20分钟", "番茄结束", "休息结束", "编译失败", "测试通过", "长任务完成", "该休息" })
        Check(book.TryGetValue(name, out var def) && def.Lines.Length > 0 && def.Lines.All(line => line.Cue != null), "events.txt has a readable " + name);
    var touches = File.ReadAllLines(Path.Combine(root!, "touch.txt")).Where(line => !line.TrimStart().StartsWith("//") && line.Contains('|')).ToArray();
    Check(touches.Where(line => line.Split('|').Length == 3).All(line => Condition.Parse(line.Split('|')[2], out _) is { MinLevel: > 0 }), "touch.txt levels are read");
    Check(touches.Count(line => line.StartsWith("娇羞|")) >= 4, "the highest level has its own chest-poke lines");
    var skitLines = File.ReadAllLines(Path.Combine(root!, "skits.txt"));
    var loveSkits = skitLines.Select((line, i) => (line, i)).Where(item => item.line.StartsWith('#') && item.line.Contains("@亲密")).ToArray();
    Check(loveSkits.Length >= 8 && loveSkits.All(item => skitLines[item.i + 1].TrimStart('/').Trim().StartsWith("彩蛋")), "closeness skits are hidden eggs");
    Check(loveSkits.All(item => Condition.Parse(item.line[(item.line.IndexOf('@') + 1)..], out _) is { MinLevel: >= 2 }), "closeness skits read");
}

// 记忆小本本（本机加，2026-11-08）：记、查、删改、取代、整理、上限、存取，还有“不拖效率”的几条硬规矩。
void VerifyMemory()
{
    var dir = Path.Combine(Path.GetTempPath(), "fish-memory-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dir);
    string main = Path.Combine(dir, "memory.json"), old = Path.Combine(dir, "memory-archive.json");
    var t = new DateTime(2026, 11, 8, 10, 0, 0, DateTimeKind.Local);
    var book = MemoryBook.Load(main, old);
    Check(book.Count == 0 && book.Recall(null, 5, false, t).Count == 0, "a fresh notebook is empty");

    // 记、查
    var fish = book.Remember("爱吃鱼", null, "偏好·食物", null, null, null, "ai", t);
    Check(fish.Fresh && fish.Item.Category == "偏好" && fish.Item.Source == "said" && book.Count == 1, "remembered with defaults");
    Check(book.Recall("鱼", 5, false, t).Single().Item.Content == "爱吃鱼", "found by one character");
    Check(book.Recall("今天想吃鱼了", 5, false, t).Count == 1, "found inside a longer phrase");
    Check(book.Recall("香菜", 5, false, t).Count == 0, "nothing for an unrelated word");
    var again = book.Remember("爱吃鱼。", null, null, 3, null, null, "ai", t.AddMinutes(1));
    Check(!again.Fresh && book.Count == 1 && again.Item.Importance == 3, "the same thing is not written twice");
    Check(book.Remember(new string('长', 80), "事实", null, null, null, null, "ai", t).Item.Content.Length == MemoryBook.MaxContent, "long content is cut to 40");

    // 取代：同一个 slot 新的顶掉旧的，旧的进归档还查得到
    book.Remember("爱喝咖啡", null, "偏好·饮料", null, null, null, "ai", t.AddMinutes(3));
    var latest = book.Remember("现在只喝茶", null, "偏好·饮料", null, null, null, "ai", t.AddMinutes(4));
    Check(latest.Replaced == "爱喝咖啡" && book.Items.Count(item => item.Slot == "偏好·饮料") == 1, "one slot keeps one memory");
    Check(book.Recall("咖啡", 5, false, t).Count == 0 && book.Recall("咖啡", 5, true, t).Single().Old, "the old one is only in the archive");
    Check(book.Archived == 1, "the old drink archived");

    // 参数检查
    bool Throws(Action action) { try { action(); return false; } catch (ArgumentException) { return true; } }
    Check(Throws(() => book.Remember("考试", "日程", null, null, null, null, "ai", t)), "a plan needs a date");
    Check(Throws(() => book.Remember("生日", "纪念日", null, null, null, "明天", "ai", t)), "a date has to be readable");
    Check(Throws(() => book.Remember("什么", "心情", null, null, null, null, "ai", t)), "only four kinds");
    Check(Throws(() => book.Remember("  ", null, null, null, null, null, "ai", t)), "empty is turned down");

    // 一分钟最多记 5 条新的
    var flood = MemoryBook.Load(null, null);
    for (int i = 0; i < 5; i++) flood.Remember("第" + i + "件事", null, null, null, null, null, "ai", t.AddSeconds(i));
    bool limited = false;
    try { flood.Remember("第六件", null, null, null, null, null, "ai", t.AddSeconds(10)); } catch (InvalidOperationException) { limited = true; }
    Check(limited && flood.Remember("第六件", null, null, null, null, null, "ai", t.AddMinutes(2)).Fresh, "five a minute, then later");

    // 推测的分数打对折；确认后变成亲口说的；能改能删
    var guess = book.Remember("好像喜欢猫", null, null, null, "guess", null, "ai", t.AddMinutes(5));
    var dog = book.Remember("喜欢狗", null, null, null, null, null, "ai", t.AddMinutes(5));
    var pets = book.Recall("喜欢", 5, false, t.AddDays(3));
    Check(pets[0].Item.Id == dog.Item.Id, "a guess comes after what he said");
    book.Forget(guess.Item.Id, null, "said", t.AddDays(3));
    Check(book.Items.Single(item => item.Id == guess.Item.Id).Source == "said", "a guess can be confirmed");
    book.Forget(dog.Item.Id, "其实更喜欢柴犬", null, t.AddDays(3));
    Check(book.Recall("柴犬", 5, false, t.AddDays(3)).Count == 1, "a memory can be corrected");
    book.Forget(dog.Item.Id, null, null, t.AddDays(3));
    Check(book.Recall("柴犬", 5, false, t.AddDays(3)).Count == 0 && book.Recall("柴犬", 5, true, t.AddDays(3)).Single().Item.Why == "forgotten", "forgotten goes to the archive");
    Check(Throws(() => book.Forget("nope", null, null, t)), "an unknown id is turned down");

    // 日子：今天的日程、纪念日当天排在前面
    var exam = book.Remember("下午考试", "日程", null, null, null, "2026-11-10", "ai", t.AddMinutes(10));
    var birthday = book.Remember("老大生日", "纪念日", null, null, null, "11-10", "ai", t.AddMinutes(10));
    Check(birthday.Item.Date == "11-10" && MemoryBook.DaysUntil(birthday.Item, new DateOnly(2026, 11, 11)) == 364, "anniversaries come back every year");
    var top = book.Recall(null, 2, false, new DateTime(2026, 11, 10, 9, 0, 0));
    Check(top.Select(found => found.Item.Id).ToHashSet().SetEquals([exam.Item.Id, birthday.Item.Id]), "today's plan and anniversary come first");
    Check(top.All(found => found.Item.RecallCount >= 1), "recalling is counted");

    // 整理：过期日程、久不想起的普通偏好收起来；事实和纪念日不动；一天只整理一次
    var later = new DateTime(2027, 2, 1, 9, 0, 0);
    Check(book.Tidy(later) && !book.Tidy(later.AddHours(3)), "tidied once a day");
    Check(!book.Items.Any(item => item.Id == exam.Item.Id), "an old plan is put away");
    Check(book.Items.Any(item => item.Id == birthday.Item.Id) && book.Items.Any(item => item.Category == "事实"), "facts and anniversaries stay");
    Check(book.Items.Any(item => item.Content == "爱吃鱼") && !book.Items.Any(item => item.Content == "现在只喝茶"), "an important like stays, a plain one fades");

    // 上限：记 200 条，正在用的不超过 120；事实一条不少
    var full = MemoryBook.Load(Path.Combine(dir, "full.json"), Path.Combine(dir, "full-archive.json"));
    var start = new DateTime(2026, 1, 1, 8, 0, 0);
    for (int i = 0; i < 30; i++) full.Remember("事实" + i, "事实", null, null, null, null, "ai", start.AddMinutes(i));
    for (int i = 0; i < 170; i++) full.Remember("偏好" + i, null, null, null, null, null, "ai", start.AddMinutes(100 + i));
    Check(full.Count == MemoryBook.Max && full.Items.Count(item => item.Category == "事实") == 30 && full.Archived == 80, "120 at most, facts kept, the rest archived");
    Check(new FileInfo(Path.Combine(dir, "full.json")).Length < 60_000, "the notebook stays small");

    // 存取：重新打开还在；写坏的临时文件、坏文件都不碍事
    var back = MemoryBook.Load(main, old);
    Check(back.Count == book.Count && back.Items.Select(item => item.Id).SequenceEqual(book.Items.Select(item => item.Id)) && back.Archived == book.Archived, "kept in memory.json");
    File.WriteAllText(main + ".tmp", "{ 写到一半");
    Check(MemoryBook.Load(main, old).Count == book.Count, "a half-written temp file does no harm");
    File.WriteAllText(Path.Combine(dir, "broken.json"), "{ not json");
    Check(MemoryBook.Load(Path.Combine(dir, "broken.json"), null).Count == 0, "a broken file starts fresh instead of crashing");

    // 效率：1000 条也在 5 ms 以内（含写文件）；交给模型的永远不超过 10 条
    var big = MemoryBook.Load(Path.Combine(dir, "big.json"), Path.Combine(dir, "big-archive.json"));
    for (int i = 0; i < 1000; i++) big.Items.Add(new MemoryItem { Id = "x" + i, Content = "第" + i + "件喜欢的东西" + (i % 7 == 0 ? "鱼" : ""), CreatedAt = MemoryBook.Unix(start) + i, Category = "事实" });
    big.Recall("鱼", 5, false, t);
    var clock = System.Diagnostics.Stopwatch.StartNew();
    for (int i = 0; i < 20; i++) big.Recall("喜欢的鱼", 10, false, t.AddDays(i + 1));
    double recallMs = clock.Elapsed.TotalMilliseconds / 20;
    clock.Restart();
    for (int i = 0; i < 20; i++) big.Remember("新的事" + i, "事实", null, null, null, null, "ai", t.AddMinutes(i * 2));
    double rememberMs = clock.Elapsed.TotalMilliseconds / 20;
    Check(recallMs < 5 && rememberMs < 5, $"fast with 1000 memories (recall {recallMs:0.00} ms, remember {rememberMs:0.00} ms)");
    Check(big.Recall(null, 99, false, t).Count == MemoryBook.MaxRecall, "never more than 10 at once");
    Console.WriteLine($"  memory: recall {recallMs:0.00} ms, remember {rememberMs:0.00} ms with 1000 memories");

    // 硬规矩：只有 remember / recall / forget 和 get_state 的条数碰小本本，hooks（event）那条路不碰；get_state 不带内容
    var src = AppContext.BaseDirectory;
    while (src != null && !Directory.Exists(Path.Combine(src, "src", "FishPet"))) src = Path.GetDirectoryName(src);
    var code = Path.Combine(src!, "src", "FishPet");
    var touches = Directory.GetFiles(code, "*.cs").Where(file => !file.EndsWith("Memory.cs") && !file.EndsWith("MemoryView.cs")) // MemoryView 只拿到条目列表，由 VerifyMemoryView 单独守
        .SelectMany(file => File.ReadAllLines(file).Where(line => line.Contains("MemoryCommand") || line.Contains("MemoryState") || line.Contains("memoryBook") || line.Contains("MemoryBook"))).ToArray();
    Check(touches.Length == 2 && touches.Any(line => line.Contains("return MemoryCommand(name, args)")) && touches.Any(line => line.Contains("memory = MemoryState()")), "only the three tools and get_state's count touch the notebook");
    Check(File.ReadAllText(Path.Combine(code, "PetWindow.Memory.cs")).Contains("MemoryState() => new { count = Memory.Count, archived = Memory.Archived }"), "get_state carries only the counts");
    Directory.Delete(dir, true);
}

// 记忆小本本的视图和变更记录（本机加，2026-11-10）：排成 Markdown、写不好也不出错、记录带“改之前”、不拖累小本本本身。
void VerifyMemoryView()
{
    var dir = Path.Combine(Path.GetTempPath(), "fish-memview-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dir);
    var t = new DateTime(2026, 11, 10, 10, 0, 0, DateTimeKind.Local);
    var book = MemoryBook.Load(null, null);
    Check(MemoryView.Render(book.Items, t).Contains("还没有记什么"), "an empty notebook says so");

    book.Remember("爱吃鱼", null, "偏好·食物", 3, null, null, "ai", t);
    book.Remember("养了一只猫", "事实", null, null, "guess", null, "ai", t.AddMinutes(1));
    book.Remember("考试", "日程", null, null, null, "2026-11-12", "ai", t.AddMinutes(2));
    book.Remember("生日", "纪念日", null, null, null, "10-21", "ai", t.AddMinutes(3));
    book.Remember("旅行", "日程", null, null, null, "2026-11-11", "ai", t.AddMinutes(4));
    var md = MemoryView.Render(book.Items, t);
    Check(md.Contains("现有 5 条") && md.IndexOf("## 偏好") < md.IndexOf("## 事实") && md.IndexOf("## 事实") < md.IndexOf("## 日程") && md.IndexOf("## 日程") < md.IndexOf("## 纪念日"), "grouped by category in a fixed order");
    Check(md.Contains("爱吃鱼 · 偏好·食物 · 很重要") && md.Contains("养了一只猫 · 她猜的") && md.Contains("10-21　生日"), "each line carries its notes");
    Check(md.IndexOf("2026-11-11　旅行") is var first and >= 0 && first < md.IndexOf("2026-11-12　考试"), "schedules are listed by date");
    Check(book.Items.All(item => md.Contains("`" + item.Id + "`")), "every memory shows its id so it can be named");

    // 写盘：整份换掉、不留临时文件；路径是 null 或写不进去都不出错
    string view = Path.Combine(dir, "memory.md");
    MemoryView.Export(view, book.Items, t);
    Check(File.ReadAllText(view) == md && !File.Exists(view + ".tmp"), "exported whole, no temp file left behind");
    book.Forget(book.Items.First(item => item.Content == "爱吃鱼").Id, null, null, t.AddMinutes(10));
    MemoryView.Export(view, book.Items, t.AddMinutes(10));
    Check(!File.ReadAllText(view).Contains("爱吃鱼") && File.ReadAllText(view).Contains("现有 4 条"), "a forgotten memory leaves the view");
    MemoryView.Export(null, book.Items, t);
    MemoryView.Export(Path.Combine(dir, "no", "such", "folder", "memory.md"), book.Items, t);
    MemoryView.Log(null, "forget", "m1", null, "x", "said", t);
    MemoryView.Log(Path.Combine(dir, "no", "such", "folder", "log.jsonl"), "forget", "m1", null, "x", "said", t);

    // 变更记录：一次一行，纠正带着改之前的，不带的字段不写，中文不转义
    string log = Path.Combine(dir, "memory-log.jsonl");
    MemoryView.Log(log, "remember", "m1", "爱吃鱼", null, "said", t);
    MemoryView.Log(log, "correct", "m1", "爱吃鲈鱼", "爱吃鱼", "said", t.AddMinutes(1));
    MemoryView.Log(log, "forget", "m1", null, "爱吃鲈鱼", "said", t.AddMinutes(2));
    var lines = File.ReadAllLines(log);
    var rows = lines.Select(line => System.Text.Json.JsonDocument.Parse(line).RootElement).ToArray();
    Check(lines.Length == 3 && rows[0].GetProperty("t").GetString() == "2026-11-10T10:00:00", "one line per change, with the time");
    Check(rows[1].GetProperty("op").GetString() == "correct" && rows[1].GetProperty("before").GetString() == "爱吃鱼" && rows[1].GetProperty("content").GetString() == "爱吃鲈鱼", "a correction keeps what it replaced");
    Check(!rows[0].TryGetProperty("before", out _) && !rows[2].TryGetProperty("content", out _) && lines[0].Contains("爱吃鱼"), "empty fields are left out and Chinese is written as it is");

    // 只追加，超过上限才丢掉最老的一半，最新的一行还在
    string big = Path.Combine(dir, "big-log.jsonl");
    for (int i = 0; i < 40; i++) MemoryView.Log(big, "remember", "m" + i, "第" + i + "件事", null, "said", t, limit: 1000);
    Check(new FileInfo(big).Length <= 1000 && File.ReadAllLines(big)[^1].Contains("\"id\":\"m39\""), "an overgrown log drops its oldest half and keeps the newest line");

    // 硬规矩：它只拿到条目列表，不碰小本本本身；查（recall）不写视图也不写记录
    var src = AppContext.BaseDirectory;
    while (src != null && !Directory.Exists(Path.Combine(src, "src", "FishPet"))) src = Path.GetDirectoryName(src);
    var code = Path.Combine(src!, "src", "FishPet");
    Check(!File.ReadAllLines(Path.Combine(code, "MemoryView.cs")).Any(line => line.Contains("memoryBook") || line.Contains("MemoryCommand") || line.Contains("MemoryState")), "the view never reaches into the notebook");
    var window = File.ReadAllText(Path.Combine(code, "PetWindow.Memory.cs"));
    var recall = window[window.IndexOf("case \"recall\":")..window.IndexOf("case \"forget\":")];
    Check(!recall.Contains("MemoryView"), "looking things up writes neither the view nor the log");
    Directory.Delete(dir, true);
}

// 到点叫人（本机加，2026-11-08）：按本机时钟、开关分开、只补 2 分钟、跨午夜、台词文件读得懂。
void VerifyAlarms()
{
    DateTime At(int day, int h, int m, int s = 0) => new(2026, 11, day, h, m, s);
    List<string> Names(DateTime a, DateTime b, bool sleep = true, bool lunch = true) => Alarms.Due(a, b, sleep, lunch).Select(slot => slot.Name).ToList();
    Check(Names(At(8, 23, 59, 59), At(9, 0, 0, 1)).Count == 0, "midnight itself rings nothing");
    Check(Names(At(9, 0, 29, 59), At(9, 0, 30, 0)).SequenceEqual(["哄睡1"]), "00:30 rings the first stage");
    Check(Names(At(9, 0, 30, 0), At(9, 0, 30, 1)).Count == 0, "rings once, not every second after");
    Check(Names(At(9, 0, 46, 59), At(9, 0, 47, 0)).SequenceEqual(["哄睡4"]), "00:47 rings the fourth");
    Check(Names(At(9, 11, 29, 59), At(9, 11, 30, 0)).SequenceEqual(["午饭"]), "11:30 rings lunch");
    Check(Names(At(9, 0, 29, 59), At(9, 0, 30, 0), sleep: false).Count == 0, "sleep switch off");
    Check(Names(At(9, 11, 29, 59), At(9, 11, 30, 0), lunch: false).Count == 0, "lunch switch off");
    Check(Names(At(9, 0, 29, 59), At(9, 0, 30, 0), lunch: false).SequenceEqual(["哄睡1"]), "switches are apart");
    Check(Names(At(9, 0, 20), At(9, 0, 31)).SequenceEqual(["哄睡1"]), "a short gap still rings");
    Check(Names(At(9, 0, 20), At(9, 0, 48)).SequenceEqual(["哄睡4"]), "after a long sleep only the one still fresh");
    Check(Names(At(8, 22, 0), At(9, 9, 0)).Count == 0, "a night asleep rings nothing in the morning");
    Check(Names(At(9, 0, 31), At(9, 0, 30)).Count == 0, "clock going back rings nothing");

    // 提醒时间可调（2026-11-18）：睡觉 22:00–02:00、午饭 11:00–13:30，15 分钟一档；四层跟着起点走，跨午夜也不漏
    List<string> NamesAt(DateTime a, DateTime b, string sleepAt, string lunchAt = "11:30") => Alarms.Due(a, b, true, true, sleepAt, lunchAt).Select(slot => slot.Name).ToList();
    Check(NamesAt(At(8, 23, 44, 59), At(8, 23, 45, 0), "23:45").SequenceEqual(["哄睡1"]), "sleep reminder starts at the chosen time");
    Check(NamesAt(At(8, 23, 54, 59), At(8, 23, 55, 0), "23:45").SequenceEqual(["哄睡2"]), "second stage follows ten minutes later");
    Check(NamesAt(At(8, 23, 59, 59), At(9, 0, 0, 0), "23:45").SequenceEqual(["哄睡3"]), "third stage lands on midnight");
    Check(NamesAt(At(9, 0, 1, 59), At(9, 0, 2, 0), "23:45").SequenceEqual(["哄睡4"]), "fourth stage after midnight");
    Check(NamesAt(At(8, 23, 59, 30), At(9, 0, 0, 30), "23:45").SequenceEqual(["哄睡3"]), "a check spanning midnight still catches the midnight stage");
    Check(NamesAt(At(9, 0, 29, 59), At(9, 0, 30, 0), "23:45").Count == 0, "the old 00:30 no longer rings");
    Check(NamesAt(At(9, 12, 14, 59), At(9, 12, 15, 0), "00:30", "12:15").SequenceEqual(["午饭"]), "lunch at the chosen time");
    Check(Alarms.SleepTime("22:00") == "22:00" && Alarms.SleepTime("2:00") == "02:00" && Alarms.SleepTime("0:37") == "00:30" && Alarms.SleepTime("0:38") == "00:45", "sleep time snaps to 15 minutes");
    Check(Alarms.SleepTime("21:45") == null && Alarms.SleepTime("02:15") == null && Alarms.SleepTime("12:00") == null && Alarms.SleepTime("abc") == null, "sleep time stays in 22:00–02:00");
    Check(Alarms.LunchTime("11:00") == "11:00" && Alarms.LunchTime("13:30") == "13:30" && Alarms.LunchTime("10:45") == null && Alarms.LunchTime("13:45") == null, "lunch time stays in 11:00–13:30");
    Check(Alarms.LunchTime("12：00") == "12:00", "full-width colon is read");
    Check(Alarms.Spoken(0, 30) == "十二点半" && Alarms.Spoken(11, 45) == "十一点四十五" && Alarms.Spoken(13, 0) == "一点" && Alarms.Spoken(22, 15) == "十点十五"
        && Alarms.Spoken(2, 0) == "两点" && Alarms.Spoken(0, 40) == "十二点四十" && Alarms.Spoken(23, 47) == "十一点四十七" && Alarms.Spoken(1, 2) == "一点零二", "times read aloud");
    Check(Alarms.Schedule("bad", "bad").Select(slot => (slot.Hour, slot.Minute)).SequenceEqual(Alarms.Slots.Select(slot => (slot.Hour, slot.Minute))), "unreadable times fall back to the defaults");
    var root = AppContext.BaseDirectory;
    while (root != null && !File.Exists(Path.Combine(root, "alarms.txt"))) root = Path.GetDirectoryName(root);
    Check(root != null, "alarms.txt is found");
    var text = Alarms.Parse(File.ReadAllLines(Path.Combine(root!, "alarms.txt")));
    Check(Alarms.Slots.All(slot => text.TryGetValue(slot.Name, out var lines) && lines.Count >= 3), "every alarm has lines");
    Check(text["哄睡4"].All(line => !line.Contains('〔') && !line.Contains('／')), "the fourth stage is one plain question");
    Check(Alarms.Slots.All(slot => Alarms.Fallback.ContainsKey(slot.Name)), "every alarm has a fallback");
    var words = text.Where(pair => pair.Key != "哄睡4").SelectMany(pair => pair.Value).Concat(Alarms.Fallback.Values)
        .SelectMany(line => System.Text.RegularExpressions.Regex.Matches(line, "〔([^〕]*)〕").Select(match => match.Groups[1].Value)).ToList();
    Check(words.Count > 0, "alarm lines carry actions");
}

// 第一次出场的欢迎（2026-11-13）：台词从 welcome.txt 取，缺了有内置的；每个动作词她都做得出来；老用户不补说。
void VerifyWelcome()
{
    var root = AppContext.BaseDirectory;
    while (root != null && !File.Exists(Path.Combine(root, "welcome.txt"))) root = Path.GetDirectoryName(root);
    Check(root != null, "welcome.txt is found");
    var shipped = Welcome.Text(File.ReadAllLines(Path.Combine(root!, "welcome.txt")));
    Check(Welcome.Bubbles(shipped).Length is >= 3 and <= 12, "the welcome is a few bubbles, not a speech");
    Check(Welcome.Bubbles(Welcome.Fallback).Length is >= 3 and <= 12, "the built-in welcome is a few bubbles too");

    // 文件缺了、没有「# 欢迎」、或者那一段是空的，都退回内置的
    Check(Welcome.Text(null) == Welcome.Fallback, "no file: the built-in welcome");
    Check(Welcome.Text(["// 只有注释", "# 别的段", "一句话"]) == Welcome.Fallback, "no welcome section: the built-in welcome");
    Check(Welcome.Text(["# 欢迎", "／／", "第二条／带气泡"]) == "第二条／带气泡", "an empty line is skipped, the first real one is used");
    Check(Welcome.Text(["# 欢迎", "甲／乙", "丙"]) == "甲／乙", "only the first line of the section is used");

    // 每个动作词她的身体都做得出来（用的是同一套 Body），而且托盘、气泡、收起这几件事都交代到了
    foreach (var text in new[] { shipped, Welcome.Fallback })
    {
        var acting = new List<string>();
        var body = new Body(acting.Add);
        foreach (var bubble in Welcome.Bubbles(text))
            foreach (System.Text.RegularExpressions.Match match in System.Text.RegularExpressions.Regex.Matches(bubble, "〔([^〕]*)〕"))
                body.Apply(match.Groups[1].Value);
        Check(body.Unknown.Count == 0, "every action word in the welcome is one her body can do" + (body.Unknown.Count > 0 ? "（" + string.Join("、", body.Unknown) + "）" : ""));
    }
    Check(shipped.Contains("小虎鲸") && shipped.Contains("收起来"), "the welcome tells where the panel icon is and how to hide her");

    // 新装 / 老用户：没有设置文件才要欢迎；有设置文件（哪怕是读坏的）都不补说
    Check(Welcome.Pending(settingsFileExisted: false), "no settings.json: a fresh install, she welcomes");
    Check(!Welcome.Pending(settingsFileExisted: true), "an existing settings.json: an old user, no welcome");
}

// 跟着音乐跳（本机加，2026-11-10）：BPM 决定一拍多长，舞步跟着变快变慢。
void VerifyDance()
{
    // 一拍多长：BPM 越高越短，40~190 之外按边界走。
    Check(Math.Abs(Dance.BeatSeconds(120) - .5) < .001, "120 BPM 一拍半秒");
    Check(Dance.BeatSeconds(150) < Dance.BeatSeconds(80), "BPM 越高一拍越短");
    Check(Dance.BeatSeconds(400) >= .32 && Dance.BeatSeconds(10) <= 1.5, "太快太慢都按边界走");

    // 每一句的拍数：摇摆一 12、摇摆二 6、慢摇 3；BPM 高的时候同一支舞更短。
    Check(Dance.Strength("soft") == (.4, 2) && Dance.Strength(null) == (.4, 2) && Dance.Strength("乱写") == (.4, 2), "摇摆强度默认柔和，和以前一样");
    Check(Dance.Strength("normal").Size > Dance.Strength("soft").Size && Dance.Strength("strong").Size > Dance.Strength("normal").Size, "越猛幅度越大");
    Check(Dance.Strength("normal").Stretch < Dance.Strength("soft").Stretch && Dance.Strength("strong").Stretch < Dance.Strength("normal").Stretch, "越猛一句越短");
    Check(Dance.Beats("摇摆一") == 12 && Dance.Beats("摇摆二") == 6 && Dance.Beats("慢摇") == 3, "每个舞步走多少拍");
    Check(Dance.Seconds("摇摆二", 150) < Dance.Seconds("摇摆二", 80), "同一支舞，BPM 高的一句更短");

    // 真的演一遍：发出去的 anim 轨道末尾就是这一句的长度，BPM 高的那条更短。
    var commands = new List<string>();
    var dancer = new Body(commands.Add);
    double fast = dancer.DanceToBeat("摇摆二", Dance.Seconds("摇摆二", 150));
    int firstRun = commands.Count;
    double slow = dancer.DanceToBeat("摇摆二", Dance.Seconds("摇摆二", 80));
    Check(fast < slow && Math.Abs(fast - Dance.Seconds("摇摆二", 150)) < .05, $"按拍子变速（{fast:0.00}s / {slow:0.00}s）");
    Check(firstRun > 0 && commands.Count == firstRun * 2, "每支舞都发出一整套轨道");
    double LastTime(string command) => double.Parse(command.Split(' ').Last().Split(':')[0], System.Globalization.CultureInfo.InvariantCulture);
    Check(commands.Take(firstRun).All(command => Math.Abs(LastTime(command) - fast) < .2), "整句一起变速，不是只改一部分");
    // 变速有上下限：要它演得再短也不会缩没，再长也不会拖到天荒地老。
    Check(dancer.DanceToBeat("摇摆一", .1) >= 3, "再快也有下限");
    Check(dancer.DanceToBeat("摇摆一", 999) <= 20, "再慢也有上限");

    // 拍子速度：喂一串拍点，估出来的一拍长度要和真实 BPM 对得上。
    double Estimate(int bpm)
    {
        var tracker = new TempoTracker();
        var start = new DateTime(2026, 11, 10, 0, 0, 0, DateTimeKind.Utc);
        double beat = 60.0 / bpm;
        for (double t = 0; t < 20; t += beat) tracker.Beat(start.AddSeconds(t));
        return tracker.Bpm;
    }
    double fastBpm = Estimate(150), slowBpm = Estimate(70);
    Check(Math.Abs(fastBpm - 150) < 6, $"150 BPM 认得出（估成 {fastBpm:0}）");
    Check(Math.Abs(slowBpm - 70) < 6, $"70 BPM 认得出（估成 {slowBpm:0}）");

    // 漏拍、同一拍里多触发一次，都不该把速度带跑。
    var bumpy = new TempoTracker();
    var when = new DateTime(2026, 11, 10, 0, 0, 0, DateTimeKind.Utc);
    double gap = 60.0 / 120;
    int index = 0;
    for (double t = 0; t < 20; t += gap, index++)
    {
        if (index % 9 == 8) continue; // 每九拍漏一拍
        bumpy.Beat(when.AddSeconds(t));
        if (index % 7 == 3) bumpy.Beat(when.AddSeconds(t + .13)); // 偶尔一拍里多触发一次
    }
    Check(Math.Abs(bumpy.Bpm - 120) < 8, $"漏拍、多拍也不跑偏（估成 {bumpy.Bpm:0}）");
}


// 节拍追踪：喂一串假的低音起音（20 帧/秒，每帧是前 50ms 的起音和），看它估的速度、相位对不对、稳不稳。
void VerifyBeatTracker()
{
    // 造一条起音：beats 是真实拍点（秒），每个拍点把强度落进它所在的那一帧；offbeat 是拍子中间较弱的一下；noise 是底噪。
    // 返回追踪器、以及把整首歌喂完以后在 checkAt 时刻的拍点预测。
    (BeatTracker Tracker, Func<double, double> Truth) Run(double bpm, double phase, double seconds, double offbeat, double noise, int seed, Func<int, bool>? skip = null, BeatTracker? reuse = null, double startAt = 0)
    {
        var tracker = reuse ?? new BeatTracker();
        var random = new Random(seed);
        double period = 60 / bpm;
        var frames = new Dictionary<int, double>();
        int count = 0;
        for (double b = phase; b < startAt + seconds; b += period, count++)
        {
            if (b < startAt || (skip?.Invoke(count) ?? false)) continue;
            int frame = (int)Math.Ceiling(b / .05);
            frames[frame] = frames.GetValueOrDefault(frame) + 1;
            if (offbeat > 0)
            {
                int half = (int)Math.Ceiling((b + period / 2) / .05);
                frames[half] = frames.GetValueOrDefault(half) + offbeat;
            }
        }
        for (int frame = (int)Math.Ceiling(startAt / .05); frame * .05 < startAt + seconds; frame++)
        {
            double jitter = (random.NextDouble() - .5) * .006; // 进程管道的到达时间有几毫秒晃动
            tracker.Add(frame * .05 + jitter, (frames.GetValueOrDefault(frame) + noise * random.NextDouble()) * .08);
        }
        // 真实拍点：落在 phase + k * period
        return (tracker, _ => 0);
    }
    // 追踪器在某一刻预测的「最近一个拍点」离真实拍点差多少秒（取绝对值，最大半拍）。
    double PhaseError(BeatTracker tracker, double at, double bpm, double phase)
    {
        double period = 60 / bpm;
        double position = tracker.Position(at);
        double predicted = at - (position - Math.Round(position)) * tracker.Period; // 追踪器认为的最近一个拍点
        double error = (predicted - phase) % period;
        if (error > period / 2) error -= period;
        if (error < -period / 2) error += period;
        return Math.Abs(error);
    }

    foreach (var bpm in new[] { 74.0, 90, 120, 146, 172 })
    {
        var (tracker, _) = Run(bpm, .21, 30, offbeat: .25, noise: .15, seed: (int)bpm);
        Check(tracker.Locked, $"{bpm} BPM：30 秒内锁得住");
        double estimated = tracker.Bpm;
        // 速度：容许差 3%，也容许落在 2 倍、1/2 倍上（同一首歌的另一种数法），舞步靠相位，不靠这个
        bool octave = Math.Abs(estimated / bpm - 1) < .03 || Math.Abs(estimated / (bpm * 2) - 1) < .03 || Math.Abs(estimated / (bpm / 2) - 1) < .03;
        Check(octave, $"{bpm} BPM 认得出（估成 {estimated:0.0}）");
        double error = PhaseError(tracker, 29.9, bpm, .21);
        // 相位：采集那一帧是 50ms 平均，所以真实拍点附近 ±50ms 都算对上；对不上就是差半拍，会差得很远
        Check(error < .06, $"{bpm} BPM 相位对得上（差 {error * 1000:0} ms）");
    }

    // 漏拍：每五拍漏掉一个，速度和相位都不该跑。
    var missing = Run(120, .3, 30, offbeat: .2, noise: .15, seed: 5, skip: k => k % 5 == 4).Tracker;
    Check(missing.Locked && Math.Abs(missing.Bpm / 120 - 1) < .04, $"漏拍也认得出（估成 {missing.Bpm:0.0}）");
    Check(PhaseError(missing, 29.9, 120, .3) < .06, "漏拍时相位也对得上");

    // 锁定之后速度不再跳：一首 120 BPM 的歌放 60 秒，每 0.5 秒看一次，估值一直在 4% 以内（或者它的 2 倍、1/2 倍）。
    {
        var tracker = new BeatTracker();
        double firstBpm = 0; bool stable = true; int samples = 0;
        var random = new Random(11);
        for (int frame = 0; frame * .05 < 60; frame++)
        {
            double t = frame * .05, onset = random.NextDouble() * .15;
            double beatPosition = t / .5;
            if (Math.Abs(beatPosition - Math.Round(beatPosition)) * .5 < .025) onset += 1;
            else if (Math.Abs(beatPosition * 2 - Math.Round(beatPosition * 2)) * .25 < .025) onset += .3;
            tracker.Add(t, onset * .08);
            if (t > 12 && frame % 10 == 0 && tracker.Locked)
            {
                if (firstBpm == 0) firstBpm = tracker.Bpm;
                samples++;
                double ratio = tracker.Bpm / firstBpm;
                if (Math.Abs(ratio - 1) > .04) stable = false;
            }
        }
        Check(samples > 50 && stable, $"锁定后速度不跳（{samples} 次采样）");
    }

    // 换歌：先 120 BPM 放 25 秒，再换成 90 BPM（不相干的速度），10 秒内要重新锁到 90 附近。
    {
        var tracker = new BeatTracker();
        Run(120, .1, 25, .2, .15, 21, reuse: tracker);
        Check(tracker.Locked && Math.Abs(tracker.Bpm / 120 - 1) < .04, "先锁到 120");
        Run(90, 25.2 - 25 + .1, 25, .2, .15, 22, reuse: tracker, startAt: 25);
        Check(tracker.Locked && Math.Abs(tracker.Bpm / 90 - 1) < .05, $"换歌后跟到 90（现在 {tracker.Bpm:0.0}）");
    }

    // 没有鼓点、只有一点底噪：不该乱锁。
    {
        var tracker = new BeatTracker();
        var random = new Random(3);
        for (int frame = 0; frame * .05 < 30; frame++) tracker.Add(frame * .05, random.NextDouble() * .0005);
        Check(!tracker.Locked, "只有底噪不锁");
    }

    // 拍位置是连续的、按速度往前走：每过一个周期加 1，Reset 以后没有相位。
    {
        var (tracker, _) = Run(120, .2, 20, .2, .1, 9);
        double a = tracker.Position(20), b = tracker.Position(20.5);
        Check(Math.Abs(b - a - .5 / tracker.Period) < 1e-6, "拍位置按周期往前走");
        tracker.Reset();
        Check(double.IsNaN(tracker.Position(20)) && !tracker.Locked, "Reset 以后没有相位");
    }
}
