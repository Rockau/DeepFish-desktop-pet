using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace FishPet;

// 她在一摞窗口里排在哪、碰到任务栏时怎么站（2026-11-28 收到这里）。
//
// 该在的位置：永远在置顶层的最前面，压着普通窗口（最大化的窗口也盖不住她），也压着任务栏。
// 任务栏遮挡开着、她碰到任务栏时，把和任务栏重叠的那截裁掉（ApplyBarCut），看起来就是站在任务栏后面；
// 裁掉的地方是透明的，点下去点到的是任务栏。拖着她走的时候不裁，整个人盖过任务栏好看全貌，一放手就裁回去（11-12 用户要的）。
// 任务栏遮挡关着：不裁，整个人盖在任务栏上面。
// （11-25 到 11-28 试过“排到任务栏后面、隔着毛玻璃透出来”，系统和别的程序一动任务栏或她的先后就会闪，11-28 用户定了改回裁剪。）
// 置顶层里别的“正经程序窗口”盖到她身上：被碰到时、或者每秒看一眼时抬回来。系统外壳和截图录屏的浮层不算，她不去抢。
// 摆到位置只有一个入口 PutInPlace。
//
// 别人（系统、别的程序）会动她的先后，分四道守：
//   1. 当场拦（OnWindowMessage，WM_WINDOWPOSCHANGING）：要把她挪出置顶层的（放到最底下、插到不置顶的窗口后面），不换先后。
//   2. 换完马上补（WM_WINDOWPOSCHANGED）：用 HWND_NOTOPMOST 摘她置顶时，递过来的消息和普通挪位置分不出来，只能事后补。
//   3. 别的窗口换先后时马上看（WinEvent：切前台、换先后）：比如点一下任务栏，任务栏会跑到她前面。
//   4. 兜底（Tick 里的 KeepOrder）：每一拍都查一遍，每秒再看一次有没有别的置顶窗口盖着她。
// 2、3、4 都走 CheckOrder，规矩只写在那一处。
internal sealed partial class PetWindow
{
    private Rect barCut = Rect.Empty; // 不被任务栏挡住的那部分（窗口里的坐标，UpdateCut 算）；Empty 是没碰到任务栏
    private static bool ownRaise; // 她自己在往最前面抬：第 1 道别拦
    private bool fixQueued;
    private long lastEventCheck;
    private DateTime nextCoverLook;
    private WinEventProc? orderChanged; // 留着引用，免得委托被回收
    private readonly Dictionary<string, DateTime> orderLogged = new();

    private bool Guarding => !test && !standby && IsVisible && !exiting;

    // SourceInitialized 时接上第 1、2、3 道。
    private void GuardOrder(nint handle)
    {
        HwndSource.FromHwnd(handle)?.AddHook(OnWindowMessage);
        if (test) return;
        orderChanged = (_, _, _, _, _, _, _) =>
        {
            if (Environment.TickCount64 - lastEventCheck < 10) return; // 别的程序换得勤时最多 10 毫秒看一次
            lastEventCheck = Environment.TickCount64;
            CheckOrder(lookForCovers: false);
        };
        SetWinEventHook(0x0003, 0x0003, 0, orderChanged, 0, 0, 2); // EVENT_SYSTEM_FOREGROUND，WINEVENT_SKIPOWNPROCESS
        SetWinEventHook(0x8004, 0x8004, 0, orderChanged, 0, 0, 2); // EVENT_OBJECT_REORDER
    }

    // 第 4 道：Tick 每一拍调。
    private void KeepOrder()
    {
        bool lookForCovers = DateTime.UtcNow >= nextCoverLook;
        if (lookForCovers) nextCoverLook = DateTime.UtcNow.AddSeconds(1);
        CheckOrder(lookForCovers);
    }

    // 查一遍她排得对不对，不对就摆回去。lookForCovers：顺便看有没有别的置顶窗口盖着她（要列一遍进程，只每秒做一次）。
    private void CheckOrder(bool lookForCovers)
    {
        if (!Guarding) return;
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == 0) return;
        if (!IsTopmost(handle))
        {
            PutInPlace(handle);
            LogOrder("置顶被去掉了（别的程序或系统），已经重新置顶" + Above(handle));
            return;
        }
        if (TaskbarAbove(handle)) { PutInPlace(handle); return; } // 点一下任务栏，任务栏会跑到她前面
        if (!lookForCovers) return;
        var cover = CoveredBy(handle, taskbarCounts: true);
        if (cover == null) return;
        PutInPlace(handle);
        nextCoverLook = DateTime.UtcNow.AddSeconds(3); // 对方要是也爱抢最前，别跟它抢得太勤
        LogOrder("被别的置顶窗口盖住了，已经抬回来；压在上面的是 " + cover);
    }

    // 用户碰她（点、摸头、叫她）时：被正经的程序窗口压着才抬。托盘的隐藏图标面板、开始菜单、截图录屏的浮层
    // 都不算——点她一下就盖到它们上面，会把用户正在用的面板挡住（2026-11-13）。
    // 她从不抢焦点（WS_EX_NOACTIVATE），点了不会像普通窗口那样自己浮上来，所以要手动抬。
    private void RaiseIfCovered()
    {
        if (!Guarding) return;
        var handle = new WindowInteropHelper(this).Handle;
        if (handle != 0 && CoveredBy(handle, taskbarCounts: true) != null) PutInPlace(handle);
    }

    // 摆到该在的位置：置顶层最前面。
    private void PutInPlace(nint handle)
    {
        ownRaise = true;
        try { SetWindowPos(handle, HwndTopmost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate | SwpNoOwnerZOrder); }
        finally { ownRaise = false; }
    }

    // 第 1、2 道。
    private nint OnWindowMessage(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (ownRaise || exiting) return 0;
        if (message == 0x0047) // WM_WINDOWPOSCHANGED：置顶没了就马上补（排到消息队列最前，不等下一拍）
        {
            if (!fixQueued && !IsTopmost(hwnd) && Guarding)
            {
                fixQueued = true;
                Dispatcher.BeginInvoke(DispatcherPriority.Send, () => { fixQueued = false; CheckOrder(lookForCovers: false); });
            }
            return 0;
        }
        if (message != 0x0046) return 0; // WM_WINDOWPOSCHANGING
        var pos = Marshal.PtrToStructure<WindowPos>(lParam);
        if ((pos.Flags & SwpNoZOrder) != 0) return 0;
        bool topmost = IsTopmost(hwnd);
        if (!topmost || !(pos.After == HwndBottom || pos.After > HwndBottom && !IsTopmost(pos.After))) return 0;
        pos.Flags |= SwpNoZOrder; // 要挪出置顶层（放到最底下、插到不置顶的窗口后面）：不换先后
        Marshal.StructureToPtr(pos, lParam, false);
        LogOrder("拦下一次把她挪出置顶层的换先后");
        return 0;
    }

    // 任务栏遮挡开着、碰到了任务栏、又没被拖着走：把和任务栏重叠的那截裁掉；其余时候不裁。
    private void ApplyBarCut()
    {
        bool cut = settings.TaskbarCover && barCut != Rect.Empty && !(dragging && dragged);
        if (!cut) { if (image.Clip != null) image.Clip = null; return; }
        if (image.Clip is RectangleGeometry { Rect: var now } && now == barCut) return;
        image.Clip = new RectangleGeometry(barCut);
    }

    // 同一件事每分钟最多记一行。
    private void LogOrder(string message)
    {
        if (orderLogged.TryGetValue(message, out var last) && DateTime.UtcNow - last < TimeSpan.FromMinutes(1)) return;
        orderLogged[message] = DateTime.UtcNow;
        Places.Log(message);
    }

    // ---- 看窗口 ----

    private static bool IsTopmost(nint window) => ((long)GetWindowLongPtr(window, -20) & 0x8) != 0; // WS_EX_TOPMOST

    // 有没有一条看得见的任务栏排在她前面。
    private static bool TaskbarAbove(nint handle)
    {
        var above = handle;
        for (int step = 0; step < 300 && (above = GetWindow(above, 3)) != 0; step++) // GW_HWNDPREV
            if (IsTaskbar(above) && IsWindowVisible(above)) return true;
        return false;
    }

    private static bool IsTaskbar(nint window)
    {
        var name = new System.Text.StringBuilder(32);
        GetClassName(window, name, name.Capacity);
        var text = name.ToString();
        return text == "Shell_TrayWnd" || text == "Shell_SecondaryTrayWnd";
    }

    // 置顶层里压在她前面、又和她的窗口重叠的，是哪个程序（没有就返回 null）。
    // 她自己的气泡和手、任务栏/开始菜单这些系统外壳、被隐藏（cloaked）的和最小化的不算。
    private static readonly HashSet<string> ShellProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer", "ShellExperienceHost", "StartMenuExperienceHost", "SearchHost", "SearchApp", "TextInputHost", "LockApp",
        "FishTray", // 自家的托盘控制台：右键菜单、控制面板弹出来时，她不抢着盖上去（2026-11-25）
    };

    private static string? CoveredBy(nint handle, bool taskbarCounts = false)
    {
        if (!GetWindowRect(handle, out var mine)) return null;
        uint self = (uint)Environment.ProcessId;
        var above = handle;
        for (int step = 0; step < 300 && (above = GetWindow(above, 3)) != 0; step++) // GW_HWNDPREV
        {
            if (!IsWindowVisible(above) || IsIconic(above)) continue;
            if (!GetWindowRect(above, out var other)) continue;
            int width = Math.Min(mine.Right, other.Right) - Math.Max(mine.Left, other.Left);
            int height = Math.Min(mine.Bottom, other.Bottom) - Math.Max(mine.Top, other.Top);
            if (width < 24 || height < 24) continue;
            if (taskbarCounts && IsTaskbar(above)) return "任务栏"; // 任务栏遮挡关着：任务栏压在她前面也要抬回来
            // 只管“正经的程序窗口”：不进任务栏的浮层（截图框、录制条、各种弹出面板）和铺满整屏的浮层，她不去盖，也不抢着压过去
            if (Fullscreen.IsToolWindow(above) || Fullscreen.FillsMonitor(above)) continue;
            if (DwmGetWindowAttribute(above, 14, out int cloaked, sizeof(int)) == 0 && cloaked != 0) continue; // DWMWA_CLOAKED
            GetWindowThreadProcessId(above, out uint id);
            if (id == self) continue;
            try
            {
                var name = Process.GetProcessById((int)id).ProcessName;
                if (!ShellProcesses.Contains(name) && !Fullscreen.IsCaptureTool(name)) return name;
            }
            catch { /* 程序刚好退了：下次再看 */ }
        }
        return null;
    }

    // 压在她前面的是哪个程序（只写日志，好查出是谁撤的置顶）。
    private static string Above(nint handle)
    {
        var above = GetWindow(handle, 3); // GW_HWNDPREV
        if (above == 0) return "";
        GetWindowThreadProcessId(above, out uint id);
        try { return "；刚才压在上面的是 " + Process.GetProcessById((int)id).ProcessName; }
        catch { return ""; }
    }

    private static readonly nint HwndTopmost = -1, HwndBottom = 1;
    private const uint SwpNoZOrder = 0x0004;
    [StructLayout(LayoutKind.Sequential)]
    private struct WindowPos { public nint Hwnd, After; public int X, Y, Cx, Cy; public uint Flags; }
    private delegate void WinEventProc(nint hook, uint type, nint hwnd, int idObject, int idChild, uint thread, uint time);
    [DllImport("user32.dll")] private static extern nint SetWinEventHook(uint min, uint max, nint module, WinEventProc proc, uint process, uint thread, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(nint hwnd, System.Text.StringBuilder text, int count);
}
