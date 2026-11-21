using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace FishPet;

// 鼠标和她的实时互动（本机加，2026-11-10）：
//   · 右键按住在她头上（头 + 整张脸）移动 = 摸头：不播固定动画，每一帧按鼠标的位置驱动头的弹簧，像真的在摸；
//     头朝着和手相反的方向转、眼睛看着手，靠近两侧单闭眼、摸到脸上闭双眼；鼠标变成一只白手（HandWindow）。
//   · 右键拖裙摆、腰或脚，以及 Shift + 右键拖脸颊，分别驱动局部互动。
//   · 左键拖她：按拖动速度实时有惯性（横向甩、竖向拉伸压扁）；转圈或甩得太狠会累积“头晕”，放下后晕一会儿。
//   · 鼠标在她身上停约 10 秒：做个轻轻的小反应，冷却 30 秒。
// 台词在 stroke.txt / hover.txt / dizzy.txt（和 touch.txt 同格式，一起读进 touchLines）。
internal sealed partial class PetWindow
{
    // ---- 弹簧：每个参数一根，目标由当前互动设置，弹簧负责惯性和回弹，再用渲染器的 live 命令每帧发过去 ----
    private sealed class Spring { internal double X, V, Target, K, C; }
    private readonly Dictionary<string, Spring> springs = [];
    private readonly Dictionary<string, double> sentValues = [];
    private double springK = 130, springC = 13;
    private DispatcherTimer? touchTimer;
    private DateTime touchLast;

    // k/c 传 0 就用全局那对（其它地方的弹簧照旧）；传了就这一条参数自己用（拖动时的分层弹簧）。
    private void Aim(string id, double target, double k = 0, double c = 0)
    {
        if (CurrentInteractionAction is { } action && ActionStyleFor(action) is { } style && style != ActionStyle.Default)
        {
            target = id == "EyeOpen" ? 1 + (target - 1) * style.Strength : target * style.Strength;
            double softness = 1.35 - style.Softness * .7;
            k = (k > 0 ? k : springK) * style.Speed * style.Speed * softness;
            c = (c > 0 ? c : springC) * style.Speed * Math.Sqrt(softness);
        }
        if (!springs.TryGetValue(id, out var spring)) springs[id] = spring = new Spring();
        spring.Target = target;
        spring.K = k;
        spring.C = c;
    }

    // 这条参数弹簧现在的位置（两级弹簧里，头要读身子当前摆到哪了）。
    private double Aimed(string id) => springs.TryGetValue(id, out var spring) ? spring.X : 0;

    private void AimAllZero() { foreach (var spring in springs.Values) spring.Target = 0; }

    // 返回是否还有弹簧在动（没动完就继续跑定时器）。
    private bool StepSprings(double dt)
    {
        bool moving = false;
        foreach (var (id, spring) in springs.ToArray())
        {
            double k = spring.K > 0 ? spring.K : springK, c = spring.C > 0 ? spring.C : springC;
            double a = k * (spring.Target - spring.X) - c * spring.V;
            spring.V += a * dt;
            spring.X += spring.V * dt;
            if (spring.Target == 0 && Math.Abs(spring.X) < .004 && Math.Abs(spring.V) < .03)
            {
                if (sentValues.Remove(id)) Send($"clear {id}");
                springs.Remove(id);
                continue;
            }
            moving = true;
            if (!sentValues.TryGetValue(id, out var sent) || Math.Abs(sent - spring.X) > .003)
            {
                sentValues[id] = spring.X;
                Send(FormattableString.Invariant($"live {id} {spring.X:0.####}"));
            }
        }
        return moving;
    }

    private void StartTouchTimer()
    {
        touchTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(4), DispatcherPriority.Render, (_, _) => TouchStep(), Dispatcher);
        if (!touchTimer.IsEnabled) { touchLast = DateTime.UtcNow; touchTimer.Start(); }
    }

    private void TouchStep()
    {
        var now = DateTime.UtcNow;
        double dt = Math.Clamp((now - touchLast).TotalSeconds, .004, .05);
        touchLast = now;
        bool busy = false;
        if (dizzyOn) { StepDizzy(now); busy = true; }
        if (stroking) { StepStroke(dt, now); busy = true; }
        if (dragging && dragged) { StepDrag(dt); busy = true; }
        if (StepExtraTouch(now)) busy = true;
        bool moving = StepSprings(dt);
        // 松手后等弹簧回稳，再演“摸后”的反应（弹簧和反应都会占同一批参数，不能同时）；最多等 1.5 秒。
        if (afterStroke is { } release)
        {
            busy = true;
            if (stroking || dragging || dizzyOn || behavior.Quiet)
            {
                afterStroke = null;
                afterStrokeMelt = false;
                if (!stroking && !dizzyOn)
                {
                    Face(NoSets, []);
                    behavior.Touching = false;
                }
            }
            else if ((!moving && release.Mood != HeadStrokeFace.MeltReleaseMood) || now > afterStrokeAt) PlayAfterStroke();
        }
        if (!moving && !busy) touchTimer?.Stop();
    }

    // 屏幕上的 DIP 坐标。
    private Point ScreenDip(NativePoint p)
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        return new Point(p.X / dpi.DpiScaleX, p.Y / dpi.DpiScaleY);
    }

    private bool OnHer(Point local) => local.X >= 0 && local.Y >= 0 && local.X < Width && local.Y < Height && VisualTreeHelper.HitTest(image, local) != null;

    // ---- 脸上的表情（抚摸、头晕共用）：按“想要的”和“现在有的”比，只发有变化的 ----
    private readonly Dictionary<string, double> faceSets = [];
    private readonly HashSet<string> faceExps = [];
    private readonly Dictionary<string, double> faceExpressionAmounts = [];

    private void Face(IReadOnlyDictionary<string, double> sets, IEnumerable<string> exps, double amount = 1, double? blush = null)
    {
        var expressionList = exps.ToArray();
        bool negativeStroke = stroking && strokeLevel == 0;
        bool strokeDisgust = negativeStroke && expressionList.Contains("黑脸");
        if ((sets.Count > 0 || expressionList.Length > 0) && CurrentInteractionAction is { } action)
        {
            var style = ActionStyleFor(action);
            amount *= style.Strength;
            blush *= style.Strength;
            if (style.Face.Length > 0 && !strokeDisgust)
            {
                expressionList = style.Face == "无表情" ? [] : [style.Face];
                blush = null;
            }
            if (style.Strength != 1) sets = sets.ToDictionary(pair => pair.Key,
                pair => pair.Key == "EyeOpen" ? 1 + (pair.Value - 1) * style.Strength : pair.Value * style.Strength);
        }
        foreach (var id in faceSets.Keys.Where(id => !sets.ContainsKey(id)).ToArray()) { faceSets.Remove(id); Send($"clear {id}"); }
        foreach (var (id, value) in sets)
            if (!faceSets.TryGetValue(id, out var have) || have != value) { faceSets[id] = value; Send(FormattableString.Invariant($"set {id} {value}")); }
        var want = expressionList.ToHashSet();
        if (NegativeAffection || negativeStroke) want.Remove("脸红");
        foreach (var name in faceExps.Where(name => !want.Contains(name)).ToArray())
        {
            faceExps.Remove(name);
            faceExpressionAmounts.Remove(name);
            Send($"exp {name} 0");
        }
        foreach (var name in want)
        {
            double strength = strokeDisgust && name == "黑脸" ? 1 : name == "脸红" ? blush ?? amount : amount;
            double weight = Math.Round(ActionStyle.ExpressionAmount(name, strength) * 40) / 40;
            faceExps.Add(name);
            if (!faceExpressionAmounts.TryGetValue(name, out var shown) || weight != shown)
            {
                faceExpressionAmounts[name] = weight;
                Send(weight == 1 ? $"exp {name} 1" : FormattableString.Invariant($"expamount {name} {weight:0.###}"));
            }
        }
    }

    private static readonly Dictionary<string, double> NoSets = [];

    // 前倾这项不从抚摸的临时表情里清掉，留给「摸后融化」用 set 慢慢接到 -18。
    private void KeepLean() => faceSets.Remove("LeanInput");

    // ================= 右键抚摸（只摸头） =================
    // 只在头和整张脸（Behavior.Zone 的 head / face）上算：右键按在别处没反应。只有头部动，身体不动：
    //   · 头朝着和手相反的方向转（手在右上，脸就朝左下），眼睛却跟着手看过去；
    //   · 表情按开始抚摸时的好感选档。睁眼表情会在手靠近的一侧眯眼；
    //   · 反感档久摸会黑脸。其他档有效抚摸越久越脸红，好感越高越早开始，长按可触发融化。

    private bool stroking;
    private HandWindow? handWindow;
    private Point strokeLast;
    private Vector strokeVel;
    private double strokeTravel, strokeMoving, strokeMaxTime, strokeHeadTime;
    // 20 秒彩蛋（本机加，2026-11-11）：单独一条更宽容的计时（停手只退 0.1/秒、速度门槛 25），
    // 和 3/8 秒用的 strokeHeadTime 分开，旧的手感一点不动。melted 满 20 秒后锁存到松手。
    private double strokeDeepTime;
    private bool melted, melted30; // melted30：本机加（2026-11-13）满 30 秒的升级版彩蛋，同样锁存到松手
    private DateTime lastStrokeGain = DateTime.MinValue;
    private (HeadStrokeFace Face, ActionStyle Style)? strokeFace;
    private int strokeLevel = 1;
    private int strokeSide; // -1 手靠近头的左侧，1 靠近右侧（都是屏幕上的左右），0 在中间；带一点滞后，别在交界处来回抖
    private const double HandSize = .4; // 摸头那只白手的大小：效果图里那只的 40%

    private bool OnHead(Point local, out string? zone)
    {
        zone = null;
        if (!OnHer(local)) return false;
        if (TouchAt(local.X / Width, local.Y / Height, right: true) is not { Gesture: "stroke" } region) return false;
        zone = region.Id;
        return true;
    }

    private bool rightDragging;
    private bool rightMoved;
    private Point rightDragStart;
    private TouchRegion? rightDragRegion;
    private string? rightAffectionEvent;

    private bool ArmRightDrag(TouchRegion region, Point at, bool pinchModifier)
    {
        string gesture = RightGesture(region, pinchModifier);
        if (gesture is not ("skirt" or "waist" or "foot" or "pinch" or "action")) return false;
        ArmClothesDrag(gesture);
        BeginExtraTouch(gesture, at, region.Rect);
        if (afterStroke != null)
        {
            afterStroke = null;
            afterStrokeMelt = false;
            Face(NoSets, []);
            AimAllZero();
            behavior.Touching = false;
        }
        hoverZone = null;
        rightDragRegion = region;
        rightAffectionEvent = RightGestureAffectionEvent(gesture);
        recentEditableAction = RegionEditableAction(region, right: true);
        rightMoved = false;
        rightDragging = true;
        return true;
    }

    private static string RightGesture(TouchRegion region, bool pinchModifier) =>
        pinchModifier && region.Gesture == "stroke" && region.Id == "face" ? "pinch" : region.Gesture;

    private static string? RightGestureAffectionEvent(string gesture) => gesture switch
    {
        "skirt" => "拖裙摆", "foot" => "挠脚", "waist" => "挠腰", "pinch" => "捏脸", _ => null
    };

    private void RightDragMove(object sender, MouseEventArgs e)
    {
        if (!rightDragging) return;
        if (e.RightButton != MouseButtonState.Pressed) { CancelRightDrag(); return; }
        GetCursorPos(out var pointer);
        var position = ScreenDip(pointer);
        var offset = position - rightDragStart;
        MoveRightDrag(offset.X, offset.Y);
        handWindow?.Update(position, DragHandPose());
        e.Handled = true;
    }

    private void MoveRightDrag(double dx, double dy)
    {
        if (!rightDragging) return;
        rightMoved |= Math.Abs(dx) + Math.Abs(dy) >= ClothesDragStart;
        if (MoveClothesDrag(dx, dy)) return;
        MoveExtraTouch(dx, dy);
    }

    private void FinishRightDrag()
    {
        if (!rightDragging) return;
        rightDragging = false;
        var region = rightDragRegion;
        var kind = rightAffectionEvent;
        rightDragRegion = null;
        rightAffectionEvent = null;
        HideTouchHand();
        bool clothing = FinishClothesDrag();
        bool extra = FinishExtraTouch();
        string? cue = region?.Action;
        int level = affection.Level;
        if ((clothing || extra) && kind != null)
        {
            BondSeen();
            level = affection.Level;
            cue = EffectiveAffectionRule(level, kind).Action ?? cue;
            BondGain(kind, queueReaction: false);
        }
        if ((clothing || extra || rightMoved) && cue is { Length: > 0 })
        {
            behavior.ResetChestPokes();
            CancelExtraTouch();
            YieldMutter();
            PlayTouchCue(cue);
            SayTouch(cue, region?.Box, level);
        }
    }

    private void CancelRightDrag()
    {
        if (!rightDragging) return;
        rightDragging = false;
        rightDragRegion = null;
        rightAffectionEvent = null;
        HideTouchHand();
        CancelClothesDrag();
        CancelExtraTouch();
        if (IsMouseCaptured) ReleaseMouseCapture();
    }

    private TouchRegion? strokeRegion;
    private void StrokeDown(object sender, MouseButtonEventArgs e)
    {
        if (exiting || standby || hiding || stroking || dragging || rightDragging) return;
        var at = e.GetPosition(this);
        if (!OnHer(at)) return;
        if (TouchAt(at.X / Width, at.Y / Height, right: true) is not { Gesture.Length: > 0 } region) return;
        if (ArmRightDrag(region, at, (Keyboard.Modifiers & ModifierKeys.Shift) != 0))
        {
            e.Handled = true;
            RaiseAboveAll();
            GetCursorPos(out var press);
            rightDragStart = ScreenDip(press);
            if (!CaptureMouse()) CancelRightDrag();
            else ShowTouchHand(rightDragStart, DragHandPose());
            return;
        }
        if (region.Gesture != "stroke") return;
        strokeRegion = region;
        recentEditableAction = "interaction:stroke";
        e.Handled = true;
        RaiseAboveAll();
        CancelExtraTouch();
        stroking = true;
        strokeTravel = strokeMoving = strokeMaxTime = strokeHeadTime = 0;
        strokeDeepTime = 0; melted = false; melted30 = false; afterStrokeMelt = false;
        strokeVel = default; strokeFace = null; strokeSide = 0;
        afterStroke = null;
        GetCursorPos(out var p);
        strokeLast = ScreenDip(p);
        if (!CaptureMouse())
        {
            stroking = false;
            return;
        }
        EndDizzy();
        Face(NoSets, []);
        behavior.Touching = true;
        behavior.Interrupt();
        YieldMutter();
        springK = 150; springC = 14;
        ShowTouchHand(strokeLast, HandPoseFor(strokeLast));
        BondSeen();
        strokeLevel = affection.Level;
        Happened("stroke_start", new { zone = region.Id });
        StartTouchTimer();
    }

    private void StrokeUp(object sender, MouseButtonEventArgs e)
    {
        if (rightDragging)
        {
            e.Handled = true;
            try { FinishRightDrag(); }
            finally { if (IsMouseCaptured) ReleaseMouseCapture(); }
            return;
        }
        if (!stroking) return;
        e.Handled = true;
        EndStroke();
    }

    // 按好感和抚摸时长选择松手反应，脸红接续松手时的状态。
    private (string Mood, string Key)? afterStroke;
    private DateTime afterStrokeAt;
    private bool afterStrokeBond, afterStrokeMelt;
    private double afterStrokeBlush;

    // 好感降到反感时，已经在摸或还等着播放的收尾也随之换档。
    private void RegradeActiveStroke()
    {
        if (!stroking && afterStroke == null) return;
        strokeLevel = 0;
        strokeFace = null;
        melted = melted30 = afterStrokeMelt = false;
        afterStrokeBlush = 0;
        if (afterStroke == null) return;
        var release = HeadStrokeFace.ReleaseAt(0, Math.Max(strokeMaxTime, strokeDeepTime), 0);
        afterStroke = release;
        Face(NoSets, release.Mood == HeadStrokeFace.NegativeReleaseMood ? ["黑脸"] : []);
    }

    private void EndStroke()
    {
        if (!stroking) return;
        stroking = false;
        if (IsMouseCaptured) ReleaseMouseCapture();
        HideTouchHand();
        afterStrokeBlush = strokeFace != null ? faceExpressionAmounts.GetValueOrDefault("脸红") : 0;
        strokeFace = null;
        double held = strokeMaxTime;
        // 只有真的摸了几下才有“摸后”的反应（按下去没动不算）。
        bool touched = strokeTravel > 8 && strokeMoving >= .4 && !dizzyOn;
        Happened("stroke_end", new { seconds = Math.Round(strokeMoving, 1), stage = melted ? 4 : held >= 8 ? 3 : held >= 3 ? 2 : 1,
            affectionLevel = strokeLevel, blush = afterStrokeBlush });
        if (!touched && !melted)
        {
            strokeRegion = null;
            Face(NoSets, []);
            AimAllZero();
            springK = 200; springC = 28;
            if (!dizzyOn) behavior.Touching = false;
            return;
        }
        // 摸头加亲密度最多 60 秒一次，摸得太短（不到 1.2 秒）不加。触发彩蛋后保留原有加分事件。
        afterStrokeBond = strokeMoving >= 1.2;
        afterStrokeMelt = melted;
        string? meltLine = melted ? (melted30 ? "抚摸头5" : "抚摸头4") : null;
        var release = HeadStrokeFace.ReleaseAt(strokeLevel, held, random.NextDouble(), meltLine);
        afterStroke = release;
        if (release.Mood == HeadStrokeFace.MeltReleaseMood)
        {
            // 彩蛋：不断档。眯眼笑、前倾、歪头都保留，只让头和眼珠慢慢回正，0.8 秒后进「摸后融化」。
            afterStrokeAt = DateTime.UtcNow.AddSeconds(.8);
            springK = 90; springC = 9;
            foreach (var id in new[] { "FaceAngleX", "FaceAngleY", "FaceAngleZ", "ParamEyeBallX", "ParamEyeBallY" })
                if (springs.TryGetValue(id, out var spring)) spring.Target = 0;
            StartTouchTimer();
            return;
        }
        string[] lingering = release.Mood == HeadStrokeFace.NegativeReleaseMood ? ["黑脸"]
            : afterStrokeBlush > 0 ? ["脸红"] : [];
        Face(NoSets, lingering, blush: afterStrokeBlush);
        AimAllZero();
        springK = 200; springC = 28; // 松手后头很快回正（临界阻尼），再接摸后的反应
        behavior.Touching = false;
        afterStrokeAt = DateTime.UtcNow.AddSeconds(1.5);
        StartTouchTimer();
    }

    private void PlayAfterStroke()
    {
        var (mood, key) = afterStroke!.Value;
        afterStroke = null;
        behavior.Touching = false;
        string kind = afterStrokeMelt ? "融化" : "摸头";
        int releaseLevel = affection.Level;
        var cue = StrokeReleaseCue(releaseLevel, kind, strokeRegion);
        var box = strokeRegion?.Box;
        strokeRegion = null;
        if (cue == null) KeepLean(); // 前倾交给默认表情接着
        else if (cue.Length == 0) AimAllZero();
        Face(NoSets, []);
        if (cue == null)
        {
            var action = behavior.Action(mood);
            double blush = Math.Min(afterStrokeBlush, action.BlushStrength ?? action.ExpressionStrength);
            YieldMutter();
            behavior.Show(mood, DateTime.UtcNow, force: true, preview: action with { BlushStrength = blush });
        }
        else if (cue.Length > 0)
        {
            YieldMutter();
            PlayTouchCue(cue);
        }
        if (afterStrokeMelt)
        {
            afterStrokeMelt = false;
            BondMelt();
        }
        else if (afterStrokeBond && DateTime.UtcNow - lastStrokeGain > TimeSpan.FromSeconds(60))
        {
            lastStrokeGain = DateTime.UtcNow;
            BondGain("摸头", queueReaction: false);
        }
        if (cue != "" && !speech.IsSaying) SayTouch(cue ?? key, box, cue == null ? strokeLevel : releaseLevel);
    }

    private void StepStroke(double dt, DateTime now)
    {
        if (Mouse.RightButton != MouseButtonState.Pressed) { EndStroke(); return; } // 在别处松了手也别一直摸下去
        GetCursorPos(out var p);
        var cur = ScreenDip(p);
        var raw = (cur - strokeLast) / dt;
        strokeTravel += (cur - strokeLast).Length;
        strokeLast = cur;
        strokeVel += (raw - strokeVel) * (1 - Math.Exp(-dt / .06));
        double speed = strokeVel.Length;
        var local = new Point(cur.X - Left, cur.Y - Top);
        bool onHead = OnHead(local, out var zone);
        bool engaged = strokeTravel > 8; // 真的移动了才算在摸
        bool moving = engaged && onHead && speed > 40;
        if (moving) { strokeMoving += dt; strokeHeadTime += dt; }
        else strokeHeadTime = Math.Max(0, strokeHeadTime - dt * .25);
        if (!onHead) strokeHeadTime = Math.Max(0, strokeHeadTime - dt); // 手离开了头，害羞的程度也退下去
        // 20 秒彩蛋：单独一条更宽容的计时（速度 25 就算、停手只退 0.1/秒），和上面的旧计时互不影响。
        bool deepMoving = engaged && onHead && speed > 25;
        if (deepMoving) strokeDeepTime += dt;
        else if (onHead) strokeDeepTime = Math.Max(0, strokeDeepTime - dt * .1);
        else strokeDeepTime = Math.Max(0, strokeDeepTime - dt);
        bool canMelt = strokeLevel > 0;
        strokeMaxTime = Math.Max(strokeMaxTime, canMelt ? strokeHeadTime : strokeDeepTime);
        if (canMelt && strokeDeepTime >= 20) melted = true;
        if (canMelt && strokeDeepTime >= 30) melted30 = true;
        double melt = !canMelt ? 0 : melted ? 1 : Math.Clamp((strokeDeepTime - 17) / 3, 0, 1);
        melt = Math.Round(melt * 30) / 30; // 每帧只发有变化的几个值，30 档够平滑

        var box = StrokeHeadBox();
        double hx = Left + Width * box.X, hy = Top + Height * box.Y, rx = Width * box.Rx, ry = Height * box.Ry;
        double ux = Math.Clamp((cur.X - hx) / rx, -1.3, 1.3), uy = Math.Clamp((cur.Y - hy) / ry, -1.3, 1.3);

        // 单闭哪只眼：手越过头的左右两侧才闭，进出各有一道门槛。
        // （Behavior.Zone 的“脸”那一条横带比真正的脸宽，两边其实是头发，所以左右要自己按离中线的远近分。）
        if (strokeSide == 0) strokeSide = ux > .65 ? 1 : ux < -.65 ? -1 : 0;
        else if (strokeSide > 0 && ux < .5) strokeSide = 0;
        else if (strokeSide < 0 && ux > -.5) strokeSide = 0;

        if (melted || (engaged && onHead))
        {
            double elapsed = canMelt ? strokeHeadTime : strokeMaxTime;
            var pose = HeadStrokeFace.At(strokeLevel, strokeSide, zone == "face" && strokeSide == 0, elapsed, melt);
            var state = (pose, ActionStyleFor("interaction:stroke"));
            if (strokeFace != state)
            {
                strokeFace = state;
                var (parameters, expressions) = pose.Build();
                Face(parameters, expressions, blush: pose.Blush);
            }
        }
        else if (strokeFace != null) { strokeFace = null; Face(NoSets, []); }

        // 只动头：头朝着和手相反的方向转，眼睛看着手；速度只给头一点点跟手晃的味道。
        double fx = Math.Clamp(strokeVel.X / 600, -1, 1);
        double amp = engaged && onHead ? 1 : 0;           // 融化后头仍然 100% 跟着手走（用户定，2026-11-11）
        double tilt = 6 * Math.Sin(Math.PI * melt) + 3 * melt; // 合眼时轻轻歪一下，融化后留一点
        Aim("FaceAngleX", amp * ux * 22);                 // 手在右，脸朝左（FaceAngleX 正是朝屏幕左）
        Aim("FaceAngleY", amp * uy * 14);                 // 手在上，脸朝下（FaceAngleY 负是低头）
        Aim("FaceAngleZ", amp * (ux * 7 - fx * 5) + tilt); // 歪向转的那一边，再随手的晃动轻轻甩一下
        Aim("ParamEyeBallX", amp * Math.Clamp(-ux * .9, -1, 1)); // 眼睛看向手：手在右，眼珠往右（ParamEyeBallX 负是朝屏幕右）
        Aim("ParamEyeBallY", amp * Math.Clamp(-uy * .9, -1, 1)); // 手在上，眼珠往上

        handWindow?.Update(cur, HandPoseFor(cur));
    }

    private void ShowTouchHand(Point position, HandPose pose)
    {
        if (test) return;
        try
        {
            handWindow ??= new HandWindow();
            handWindow.Update(position, pose);
            if (!handWindow.IsVisible) handWindow.Show();
            Mouse.OverrideCursor = Cursors.None;
        }
        catch (Exception error)
        {
            Places.Log("触摸小手无法显示：" + error.Message);
            Mouse.OverrideCursor = Cursors.Hand;
        }
    }

    private void HideTouchHand()
    {
        Mouse.OverrideCursor = null;
        handWindow?.Hide();
    }

    private HandPose DragHandPose() => new(-40 * Math.PI / 180, HandSize * Math.Clamp(Width / 340, .7, 1.6), 1, 0, 0, 0);

    private (double X, double Y, double Rx, double Ry) StrokeHeadBox() =>
        strokeRegion is { Id: not ("head" or "face"), Rect: { } box }
            ? (box.X + box.Width / 2, box.Y + box.Height / 2, box.Width / 2, box.Height / 2)
            : TouchHeadBox();

    // 手的姿态：按鼠标相对头部椭圆的位置倾斜，掌心朝头部中心；指尖朝左上约 40°，在头的左右两侧略跟着转。
    private HandPose HandPoseFor(Point cur)
    {
        var box = StrokeHeadBox();
        double hx = Left + Width * box.X, hy = Top + Height * box.Y, rx = Width * box.Rx, ry = Height * box.Ry;
        double ux = (cur.X - hx) / rx, uy = (cur.Y - hy) / ry;
        double r = Math.Min(Math.Sqrt(ux * ux + uy * uy), 1.05);
        double nz = Math.Max(.52, Math.Sqrt(Math.Max(0, 1 - r * r * .7)));
        double rest = -Math.PI / 2 - 40 * Math.PI / 180;
        double angle = rest + Math.Clamp(ux, -1, 1) * .22;
        double k = rx / 78 * (1.15 + .1 * (1 - r)) * HandSize; // 效果图里头的半宽是 78，手大小 1 就是图里那么大
        double len = Math.Sqrt((cur.X - hx) * (cur.X - hx) + (cur.Y - hy) * (cur.Y - hy));
        double dx = len < 1 ? 0 : (cur.X - hx) / len, dy = len < 1 ? 0 : (cur.Y - hy) / len;
        return new HandPose(angle + Math.PI / 2, k, nz, Math.Sqrt(1 - nz * nz), dx, dy);
    }

    // ================= 左键拖拽：惯性和头晕 =================

    private bool dragMeasured;
    private Point dragLast;
    private Vector dragVel;
    private double dizzy;
    private double? dizzyAngle;

    // 拖动时的惯性（用户 2026-11-12）：抓哪儿都用同一套「抓腰」的参数——
    //   身子摆幅 1.3（原来腰档 1.0 再增强 30%），头增益 0.98（0.75 × 1.3），
    //   身子 k/c 70/7、头 k/c 40/5，整体平移压到 20%（抓点才跟手）。
    // 身子和头是两级弹簧：头的目标来自「身子当前摆到哪」，所以晚一步、还会过冲（鞭子感）。
    internal double GrabbedSway { get; private set; } = 1; // 拖动时渲染器整体平移要压掉多少（抓点那头才跟手）

    private void TouchDragStarted()
    {
        GrabbedSway = .2;
        EndDizzy();
        dizzy = 0; dizzyAngle = null; dragMeasured = false;
        springK = 80; springC = 6; // 拎着的时候阻尼小，摆几下才停
        StartTouchTimer();
    }

    internal void TouchDragEnded() => GrabbedSway = 1;

    private void StepDrag(double dt)
    {
        var pos = new Point(Left, Top);
        if (!dragMeasured) { dragLast = pos; dragVel = default; dragMeasured = true; return; }
        var raw = (pos - dragLast) / dt;
        dragLast = pos;
        dragVel += (raw - dragVel) * (1 - Math.Exp(-dt / .05));
        double speed = dragVel.Length;
        double fx = Math.Clamp(dragVel.X / 900, -1, 1), fy = Math.Clamp(dragVel.Y / 900, -1, 1);

        // 一套参数，抓哪儿都一样（用户 2026-11-12 定的：只留腰那一档，再增强 30%）
        // 全身摆动强度（用户 2026-11-12）：比原来整体强 30%，拖得越快越强，到 1500 像素/秒封顶，封顶是原来的 1.6 倍。
        double boost = 1.3 + .3 * Math.Clamp(speed / 1500, 0, 1);
        double bodyAmp = 1.3 * boost, headGain = .98 * boost;
        const double bodyK = 70, bodyC = 7, headK = 40, headC = 5;

        // 一级：身子（速度带着走）
        Aim("ParamBodyAngleX", -fx * 4 * bodyAmp, bodyK, bodyC);
        Aim("ParamBodyAngleZ", fx * 2.5 * bodyAmp, bodyK, bodyC);
        Aim("ViewRoll", fx * 4 * bodyAmp, bodyK, bodyC);
        // 垂直方向（用户 2026-11-12 要的：弹簧程度再强 40%，之后又加 30%）：往下放压扁、往上提拉长，幅度 ×1.82
        Aim("ViewSquash", Math.Clamp(fy * .082 * boost, -.073 * boost, .091 * boost), bodyK, bodyC);
        Aim("ViewX", 0, bodyK, bodyC); // 整体横移先不加：那一挪，抓着的部位就不跟手了

        // 二级：头跟着身子实际摆到的位置，方向相反（被拖着走），慢一步、会过冲
        double maxBody = 4 * bodyAmp;
        double norm = maxBody > 0 ? Math.Clamp(Aimed("ParamBodyAngleX") / maxBody, -1.2, 1.2) : 0;
        Aim("FaceAngleZ", -headGain * norm * 18, headK, headC);
        Aim("FaceAngleX", -headGain * norm * 7, headK, headC);
        Aim("FaceAngleY", fy * 14.6 * boost, headK, headC); // 上下拖时的点头：8 × 1.4 × 1.3

        // 头晕：转圈、来回甩、拖得太快都会累积；平稳了慢慢退。
        if (speed > 150)
        {
            double angle = Math.Atan2(dragVel.Y, dragVel.X);
            if (dizzyAngle is double last)
            {
                double d = angle - last;
                while (d > Math.PI) d -= 2 * Math.PI;
                while (d < -Math.PI) d += 2 * Math.PI;
                dizzy += Math.Min(.35, Math.Abs(d)) * 3.2;
            }
            dizzyAngle = angle;
        }
        else dizzyAngle = null;
        if (speed > 900) dizzy += (speed - 900) * dt * .018;
        dizzy = Math.Clamp(dizzy - 4.5 * dt, 0, 100);
    }

    // ================= 头晕 =================

    private bool dizzyOn;
    private DateTime dizzyStart;
    private double dizzyDuration, dizzyAmp;

    private const double DizzyLow = 30, DizzyHigh = 65;

    private void StartDizzy(double level)
    {
        dizzyOn = true;
        dizzyStart = DateTime.UtcNow;
        dizzyDuration = 1.6 + level * .0425;   // 30 分约 2.9 秒，65 分约 4.4 秒，满了约 5.9 秒
        dizzyAmp = .55 + level / 100 * .6;
        springK = 70; springC = 9;
        behavior.Touching = true;
        var sets = new Dictionary<string, double> { ["EyeOpen"] = .6, ["ParamMouthOpenY"] = .2, ["JawOpen"] = .1, ["BrowInnerUp"] = .6 };
        Face(sets, level >= DizzyHigh ? ["圈圈眼", "流冷汗"] : ["圈圈眼"]);
        StartTouchTimer();
        YieldMutter();
        SayTouch(level >= DizzyHigh ? "头晕重" : "头晕中", dragTouchBox);
    }

    private void StepDizzy(DateTime now)
    {
        double t = (now - dizzyStart).TotalSeconds;
        if (t >= dizzyDuration) { EndDizzy(blink: true); return; }
        double amp = dizzyAmp * Math.Pow(1 - t / dizzyDuration, 1.3);
        double w = 2 * Math.PI * 1.05 * t;
        Aim("FaceAngleX", amp * 13 * Math.Sin(w));
        Aim("FaceAngleY", amp * 7 * Math.Cos(w) - amp * 3);
        Aim("FaceAngleZ", amp * 9 * Math.Sin(w + 1.2));
        Aim("ViewRoll", amp * 2.2 * Math.Sin(w * .7 + .5));
        Aim("ViewX", amp * .01 * Math.Sin(w * .7));
        Aim("ParamBodyAngleX", amp * 3 * Math.Sin(w + .6));
        Aim("ViewSquash", .01 * amp);
    }

    private void EndDizzy(bool blink = false)
    {
        if (!dizzyOn) return;
        dizzyOn = false;
        if (!stroking) behavior.Touching = false;
        Face(NoSets, []);
        AimAllZero();
        if (blink) Send("anim EyeOpen 0:0 .1:-1 .24:0"); // 晕完眨一下眼，回过神来
    }

    // ================= 鼠标停在她身上：小反应 =================

    private string? hoverZone;
    private bool hoverPlayed;
    private DateTime hoverSince, hoverCooldownUntil;
    private static readonly TimeSpan HoverAfter = TimeSpan.FromSeconds(10), HoverCooldown = TimeSpan.FromSeconds(30);

    private void HoverMove(object sender, MouseEventArgs e)
    {
        if (dragging || rightDragging || stroking || dizzyOn) { hoverZone = null; return; }
        var at = e.GetPosition(this);
        if (VisualTreeHelper.HitTest(image, at) == null) { hoverZone = null; return; }
        var hit = TouchAt(at.X / Width, at.Y / Height, right: false);
        var zone = hit?.Hover == "" ? null : hit?.Id;
        if (zone != hoverZone) { hoverZone = zone; hoverSince = DateTime.UtcNow; hoverPlayed = false; }
    }

    private void HoverTick()
    {
        if (hoverZone == null || hoverPlayed || test) return;
        var now = DateTime.UtcNow;
        if (now - hoverSince < HoverAfter || now < hoverCooldownUntil) return;
        if (dragging || rightDragging || stroking || dizzyOn || behavior.Quiet || !FreeToTalk || !IsVisible) return;
        // 鼠标可能已经走了、窗口没收到离开：再看一眼它现在在不在她身上。
        GetCursorPos(out var p);
        var cur = ScreenDip(p);
        var local = new Point(cur.X - Left, cur.Y - Top);
        if (!OnHer(local) || TouchAt(local.X / Width, local.Y / Height, right: false) is not { } hit
            || hit.Id != hoverZone || hit.Hover == "") { hoverZone = null; return; }
        hoverCooldownUntil = now + HoverCooldown;
        hoverPlayed = true;
        HoverTouchRegion(hit, now, local.X < Width / 2 ? 1 : -1);
    }

    private void HoverTouchRegion(TouchRegion region, DateTime now, int side)
    {
        YieldMutter();
        var cue = region.Hover;
        if (string.IsNullOrWhiteSpace(cue)) behavior.Show(Behavior.HoverMood(region.Id, affection.Level), now, side);
        else PlayTouchCue(cue, side);
        if (behavior.Showing is { } actionName) recentEditableAction = actionName;
        if (CanInitiate)
        {
            initiativeMotion = behavior.MotionRevision;
            initiativeSpeechToken = mutterToken;
            SayTouch(cue ?? behavior.Showing, region.Box);
        }
        Happened("hover", new { zone = region.Id, reaction = behavior.Showing ?? cue });
    }

    // 关闭时收拾干净：别让指针一直藏着。
    private void TouchClosed()
    {
        Mouse.OverrideCursor = null;
        touchTimer?.Stop();
        handWindow?.Close();
    }
}
