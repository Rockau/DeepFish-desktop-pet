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
//   · 左键拖她：按拖动速度实时有惯性（横向甩、竖向拉伸压扁）；转圈或甩得太狠会累积“头晕”，放下后晕一会儿。
//   · 鼠标在她身上停约 2 秒：做个轻轻的小反应，冷却 10 秒。
// 台词在 stroke.txt / hover.txt / dizzy.txt（和 touch.txt 同格式，一起读进 touchLines）。
internal sealed partial class PetWindow
{
    // ---- 弹簧：每个参数一根，目标由当前互动设置，弹簧负责惯性和回弹，再用渲染器的 live 命令每帧发过去 ----
    private sealed class Spring { internal double X, V, Target; }
    private readonly Dictionary<string, Spring> springs = [];
    private readonly Dictionary<string, double> sentValues = [];
    private double springK = 130, springC = 13;
    private DispatcherTimer? touchTimer;
    private DateTime touchLast;

    private void Aim(string id, double target)
    {
        if (!springs.TryGetValue(id, out var spring)) springs[id] = spring = new Spring();
        spring.Target = target;
    }

    private void AimAllZero() { foreach (var spring in springs.Values) spring.Target = 0; }

    // 返回是否还有弹簧在动（没动完就继续跑定时器）。
    private bool StepSprings(double dt)
    {
        bool moving = false;
        foreach (var (id, spring) in springs.ToArray())
        {
            double a = springK * (spring.Target - spring.X) - springC * spring.V;
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
        touchTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(16), DispatcherPriority.Render, (_, _) => TouchStep(), Dispatcher);
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
        if (!StepSprings(dt) && !busy) touchTimer?.Stop();
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

    private void Face(IReadOnlyDictionary<string, double> sets, IEnumerable<string> exps)
    {
        foreach (var id in faceSets.Keys.Where(id => !sets.ContainsKey(id)).ToArray()) { faceSets.Remove(id); Send($"clear {id}"); }
        foreach (var (id, value) in sets)
            if (!faceSets.TryGetValue(id, out var have) || have != value) { faceSets[id] = value; Send(FormattableString.Invariant($"set {id} {value}")); }
        var want = exps.ToHashSet();
        foreach (var name in faceExps.Where(name => !want.Contains(name)).ToArray()) { faceExps.Remove(name); Send($"exp {name} 0"); }
        foreach (var name in want) if (faceExps.Add(name)) Send($"exp {name} 1");
    }

    private static readonly Dictionary<string, double> NoSets = [];

    // ================= 右键抚摸（只摸头） =================
    // 只在头和整张脸（Behavior.Zone 的 head / face）上算：右键按在别处没反应。只有头部动，身体不动：
    //   · 头朝着和手相反的方向转（手在右上，脸就朝左下），眼睛却跟着手看过去；
    //   · 表情是笑眼；手靠近头的右侧单闭右眼，靠近左侧单闭左眼，摸到脸上两只眼都闭上（眯眼笑）；
    //   · 摸得越久越害羞：3 秒后脸红，8 秒后眼冒爱心。

    private bool stroking;
    private HandWindow? handWindow;
    private Point strokeLast;
    private Vector strokeVel;
    private double strokeTravel, strokeMoving, strokeMaxTime, strokeHeadTime;
    private DateTime lastStrokeGain = DateTime.MinValue;
    private string? strokeFace;
    private int strokeSide; // -1 手靠近头的左侧，1 靠近右侧（都是屏幕上的左右），0 在中间；带一点滞后，别在交界处来回抖
    private const double HandSize = .4; // 摸头那只白手的大小：效果图里那只的 40%

    private bool OnHead(Point local, out string? zone)
    {
        zone = null;
        if (!OnHer(local)) return false;
        zone = Behavior.Zone(Shape.Id, local.X / Width, local.Y / Height, settings.Outfit);
        return zone is "head" or "face";
    }

    private void StrokeDown(object sender, MouseButtonEventArgs e)
    {
        if (exiting || standby || stroking || dragging) return;
        var at = e.GetPosition(this);
        if (!OnHead(at, out _)) return; // 只有摸头：别的地方右键不算
        e.Handled = true;
        stroking = true;
        strokeTravel = strokeMoving = strokeMaxTime = strokeHeadTime = 0;
        strokeVel = default; strokeFace = null; strokeSide = 0;
        GetCursorPos(out var p);
        strokeLast = ScreenDip(p);
        CaptureMouse();
        EndDizzy();
        behavior.Touching = true;
        behavior.Interrupt();
        YieldMutter();
        springK = 150; springC = 14;
        try
        {
            handWindow ??= new HandWindow();
            handWindow.Update(strokeLast, HandPoseFor(strokeLast));
            if (!handWindow.IsVisible) handWindow.Show();
            Mouse.OverrideCursor = Cursors.None; // 手替代了指针；手窗口开不起来就保留一个普通的手形指针
        }
        catch (Exception error) { Places.Log("摸头的手开不起来：" + error.Message); Mouse.OverrideCursor = Cursors.Hand; }
        BondSeen();
        Happened("stroke_start", new { });
        StartTouchTimer();
    }

    private void StrokeUp(object sender, MouseButtonEventArgs e)
    {
        if (!stroking) return;
        e.Handled = true;
        EndStroke();
    }

    private void EndStroke()
    {
        if (!stroking) return;
        stroking = false;
        if (IsMouseCaptured) ReleaseMouseCapture();
        Mouse.OverrideCursor = null;
        handWindow?.Hide();
        strokeFace = null;
        Face(NoSets, []);
        AimAllZero();
        springK = 120; springC = 11; // 松手后回弹几下再停
        if (!dizzyOn) behavior.Touching = false;
        double held = strokeMaxTime;
        Happened("stroke_end", new { seconds = Math.Round(strokeMoving, 1) });
        // 真的摸了一会儿才念话；摸头加亲密度最多 60 秒一次，别按着刷分。
        if (strokeMoving < 1.2) return;
        if (DateTime.UtcNow - lastStrokeGain > TimeSpan.FromSeconds(60))
        {
            lastStrokeGain = DateTime.UtcNow;
            BondGain("摸头");
        }
        if (speech.IsSaying || voice != null) return;
        SayTouch(held >= 8 ? "抚摸头3" : held >= 3 ? "抚摸头2" : "抚摸头1");
    }

    // 脸：side 是手靠近头的哪一侧（屏幕上的），face 为 true 表示手在脸上；t 是摸了多久（秒）。
    // 单闭哪只眼：ParamEyeLOpen 是模型的左眼，在屏幕的右边，所以手在屏幕右侧就闭它。
    private static (Dictionary<string, double> Sets, string[] Exps) StrokeFace(int side, bool face, double t)
    {
        var sets = new Dictionary<string, double> { ["EyeSmile"] = 1, ["ParamMouthForm"] = 1 };
        if (face) sets["EyeOpen"] = 0;                                  // 摸到脸上：双眼闭上，眯眼笑
        else if (side > 0) { sets["EyeOpen"] = .45; sets["ParamEyeLOpen"] = 0; }   // 靠近右侧：单闭右眼，另一只笑眼
        else if (side < 0) { sets["EyeOpen"] = .45; sets["ParamEyeROpen"] = 0; }   // 靠近左侧：单闭左眼
        else sets["EyeOpen"] = .45;                                     // 其他：笑眼
        if (t >= 8) { sets["BrowInnerUp"] = .5; sets["MouthPressLipOpen"] = .4; }
        return (sets, t >= 8 ? ["脸红", "爱心眼"] : t >= 3 ? ["脸红"] : []);
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
        strokeMaxTime = Math.Max(strokeMaxTime, strokeHeadTime);

        var box = Behavior.HeadBox(Shape.Id);
        double hx = Left + Width * box.X, hy = Top + Height * box.Y, rx = Width * box.Rx, ry = Height * box.Ry;
        double ux = Math.Clamp((cur.X - hx) / rx, -1.3, 1.3), uy = Math.Clamp((cur.Y - hy) / ry, -1.3, 1.3);

        // 单闭哪只眼：手越过头的左右两侧才闭，进出各有一道门槛。
        // （Behavior.Zone 的“脸”那一条横带比真正的脸宽，两边其实是头发，所以左右要自己按离中线的远近分。）
        if (strokeSide == 0) strokeSide = ux > .65 ? 1 : ux < -.65 ? -1 : 0;
        else if (strokeSide > 0 && ux < .5) strokeSide = 0;
        else if (strokeSide < 0 && ux > -.5) strokeSide = 0;

        if (engaged && onHead)
        {
            var (sets, exps) = StrokeFace(strokeSide, zone == "face" && strokeSide == 0, strokeHeadTime);
            string key = string.Join(',', sets.Select(pair => pair.Key + pair.Value)) + "|" + string.Join(',', exps);
            if (key != strokeFace) { strokeFace = key; Face(sets, exps); }
        }
        else if (strokeFace != null) { strokeFace = null; Face(NoSets, []); }

        // 只动头：头朝着和手相反的方向转，眼睛看着手；速度只给头一点点跟手晃的味道。
        double fx = Math.Clamp(strokeVel.X / 600, -1, 1);
        double amp = engaged && onHead ? 1 : 0;
        Aim("FaceAngleX", amp * ux * 22);                 // 手在右，脸朝左（FaceAngleX 正是朝屏幕左）
        Aim("FaceAngleY", amp * uy * 14);                 // 手在上，脸朝下（FaceAngleY 负是低头）
        Aim("FaceAngleZ", amp * (ux * 7 - fx * 5));       // 歪向转的那一边，再随手的晃动轻轻甩一下
        Aim("ParamEyeBallX", amp * Math.Clamp(-ux * .9, -1, 1)); // 眼睛看向手：手在右，眼珠往右（ParamEyeBallX 负是朝屏幕右）
        Aim("ParamEyeBallY", amp * Math.Clamp(-uy * .9, -1, 1)); // 手在上，眼珠往上

        handWindow?.Update(cur, HandPoseFor(cur));
    }

    // 手的姿态：按鼠标相对头部椭圆的位置倾斜，掌心朝头部中心；指尖朝左上约 40°，在头的左右两侧略跟着转。
    private HandPose HandPoseFor(Point cur)
    {
        var box = Behavior.HeadBox(Shape.Id);
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
    private double dragTime, dizzy;
    private double? dizzyAngle;

    private void TouchDragStarted()
    {
        EndDizzy();
        dizzy = 0; dizzyAngle = null; dragMeasured = false; dragTime = 0;
        springK = 80; springC = 6; // 拎着的时候阻尼小，摆几下才停
        StartTouchTimer();
    }

    private void StepDrag(double dt)
    {
        var pos = new Point(Left, Top);
        if (!dragMeasured) { dragLast = pos; dragVel = default; dragMeasured = true; return; }
        var raw = (pos - dragLast) / dt;
        dragLast = pos;
        dragVel += (raw - dragVel) * (1 - Math.Exp(-dt / .05));
        dragTime += dt;
        double speed = dragVel.Length;
        double fx = Math.Clamp(dragVel.X / 900, -1, 1), fy = Math.Clamp(dragVel.Y / 900, -1, 1);
        // 拎着不动或慢慢走时轻轻摆；快了就让速度接管。
        double swing = 4 * Math.Sin(2 * Math.PI * dragTime / 2.4) * (1 - Math.Min(1, speed / 300));
        Aim("FaceAngleZ", fx * 18 + swing);
        Aim("FaceAngleX", fx * 7 - swing * .4);
        Aim("FaceAngleY", fy * 8);
        Aim("ViewRoll", fx * 4 + swing * .3);
        Aim("ViewX", -fx * .012);
        Aim("ParamBodyAngleX", -fx * 4);
        Aim("ViewSquash", Math.Clamp(fy * .045, -.04, .05)); // 往下放压扁、往上提拉长

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
        SayTouch(level >= DizzyHigh ? "头晕重" : "头晕中");
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
    private DateTime hoverSince, hoverCooldownUntil;
    private static readonly TimeSpan HoverAfter = TimeSpan.FromSeconds(2), HoverCooldown = TimeSpan.FromSeconds(10);

    private void HoverMove(object sender, MouseEventArgs e)
    {
        if (dragging || stroking || dizzyOn) { hoverZone = null; return; }
        var at = e.GetPosition(this);
        if (VisualTreeHelper.HitTest(image, at) == null) { hoverZone = null; return; }
        var zone = Behavior.Zone(Shape.Id, at.X / Width, at.Y / Height, settings.Outfit);
        if (zone != hoverZone) { hoverZone = zone; hoverSince = DateTime.UtcNow; }
    }

    private void HoverTick()
    {
        if (hoverZone == null || test) return;
        var now = DateTime.UtcNow;
        if (now - hoverSince < HoverAfter || now < hoverCooldownUntil) return;
        if (dragging || stroking || dizzyOn || behavior.Quiet || !FreeToTalk || !IsVisible) return;
        // 鼠标可能已经走了、窗口没收到离开：再看一眼它现在在不在她身上。
        GetCursorPos(out var p);
        var cur = ScreenDip(p);
        var local = new Point(cur.X - Left, cur.Y - Top);
        if (!OnHer(local)) { hoverZone = null; return; }
        hoverCooldownUntil = now + HoverCooldown;
        hoverSince = now;
        YieldMutter();
        behavior.Show(Behavior.HoverMood(hoverZone), now, local.X < Width / 2 ? 1 : -1);
        SayTouch(behavior.Showing);
        Happened("hover", new { zone = hoverZone, reaction = behavior.Showing });
    }

    // 关闭时收拾干净：别让指针一直藏着。
    private void TouchClosed()
    {
        Mouse.OverrideCursor = null;
        touchTimer?.Stop();
        handWindow?.Close();
    }
}
