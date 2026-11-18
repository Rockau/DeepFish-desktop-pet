"""鱼眼的眼睛：看最前面的窗口是谁、截屏、把大肥鱼自己从画面里抹掉、判断画面变了没有。

只用 Windows 自带的接口（ctypes）和 mss 截屏，截下来的图只在本机内存里用，不落盘、不上传。
"""
from __future__ import annotations

import ctypes
import ctypes.wintypes as wt
from dataclasses import dataclass

import mss
import numpy as np
from PIL import Image

user32 = ctypes.windll.user32
kernel32 = ctypes.windll.kernel32
dwmapi = ctypes.windll.dwmapi

# 大肥鱼自己的窗口：截屏时盖成灰色，免得她看着自己吐槽自己。
SELF_PROCESSES = {"fishpet.exe", "fishtray.exe", "l2d-render.exe"}


def make_dpi_aware() -> None:
    """4K 屏一般开了缩放：不声明的话，拿到的窗口坐标和截屏像素对不上。"""
    try:
        user32.SetProcessDpiAwarenessContext(ctypes.c_void_p(-4))  # PER_MONITOR_AWARE_V2
    except Exception:
        try:
            ctypes.windll.shcore.SetProcessDpiAwareness(2)
        except Exception:
            pass


@dataclass
class Window:
    hwnd: int
    title: str
    process: str          # 小写的 exe 名，比如 "chrome.exe"
    rect: tuple[int, int, int, int]  # left, top, right, bottom（物理像素）
    minimized: bool

    @property
    def area(self) -> int:
        l, t, r, b = self.rect
        return max(0, r - l) * max(0, b - t)


def _process_name(pid: int) -> str:
    handle = kernel32.OpenProcess(0x1000, False, pid)  # PROCESS_QUERY_LIMITED_INFORMATION
    if not handle:
        return ""
    try:
        size = wt.DWORD(1024)
        buffer = ctypes.create_unicode_buffer(1024)
        if kernel32.QueryFullProcessImageNameW(handle, 0, buffer, ctypes.byref(size)):
            return buffer.value.rsplit("\\", 1)[-1].lower()
        return ""
    finally:
        kernel32.CloseHandle(handle)


def _rect(hwnd: int) -> tuple[int, int, int, int]:
    rect = wt.RECT()
    # DWM 给的是去掉阴影的真实边框；拿不到再退回 GetWindowRect
    if dwmapi.DwmGetWindowAttribute(wt.HWND(hwnd), 9, ctypes.byref(rect), ctypes.sizeof(rect)) != 0:
        user32.GetWindowRect(wt.HWND(hwnd), ctypes.byref(rect))
    return rect.left, rect.top, rect.right, rect.bottom


def _window(hwnd: int) -> Window:
    length = user32.GetWindowTextLengthW(hwnd)
    buffer = ctypes.create_unicode_buffer(length + 1)
    user32.GetWindowTextW(hwnd, buffer, length + 1)
    pid = wt.DWORD()
    user32.GetWindowThreadProcessId(hwnd, ctypes.byref(pid))
    return Window(hwnd, buffer.value, _process_name(pid.value), _rect(hwnd), bool(user32.IsIconic(hwnd)))


def foreground() -> Window | None:
    hwnd = user32.GetForegroundWindow()
    return _window(hwnd) if hwnd else None


def own_console() -> int:
    return kernel32.GetConsoleWindow() or 0


def self_rects() -> list[tuple[int, int, int, int]]:
    """大肥鱼的所有可见窗口（身体、气泡、托盘面板）的位置。"""
    found: list[tuple[int, int, int, int]] = []

    @ctypes.WINFUNCTYPE(ctypes.c_bool, wt.HWND, wt.LPARAM)
    def visit(hwnd, _):
        if user32.IsWindowVisible(hwnd) and not user32.IsIconic(hwnd):
            pid = wt.DWORD()
            user32.GetWindowThreadProcessId(hwnd, ctypes.byref(pid))
            if _process_name(pid.value) in SELF_PROCESSES:
                found.append(_rect(hwnd))
        return True

    user32.EnumWindows(visit, 0)
    return found


class Screen:
    def __init__(self) -> None:
        self._mss = mss.MSS()

    def monitor_of(self, rect: tuple[int, int, int, int]) -> dict:
        monitors = self._mss.monitors[1:]
        cx, cy = (rect[0] + rect[2]) // 2, (rect[1] + rect[3]) // 2
        for monitor in monitors:
            if monitor["left"] <= cx < monitor["left"] + monitor["width"] and monitor["top"] <= cy < monitor["top"] + monitor["height"]:
                return monitor
        return self._mss.monitors[1]

    def grab(self, window: Window) -> Image.Image:
        """截下这个窗口（窗口太小就截它所在的整块屏幕），并把大肥鱼盖掉。只截这一块，4K 屏也不慢。"""
        monitor = self.monitor_of(window.rect)
        ml, mt = monitor["left"], monitor["top"]
        mr, mb = ml + monitor["width"], mt + monitor["height"]
        wl, wt_, wr, wb = window.rect
        l, t, r, b = max(wl, ml), max(wt_, mt), min(wr, mr), min(wb, mb)
        # 窗口占屏幕不到四分之一（小窗口、对话框）就看整块屏幕
        if (r - l) * (b - t) < monitor["width"] * monitor["height"] * 0.25:
            l, t, r, b = ml, mt, mr, mb
        shot = self._mss.grab({"left": l, "top": t, "width": r - l, "height": b - t})
        pixels = np.frombuffer(shot.rgb, dtype=np.uint8).reshape(shot.height, shot.width, 3).copy()
        for sl, st, sr, sb in self_rects():
            sl, st, sr, sb = max(sl - l, 0), max(st - t, 0), min(sr - l, pixels.shape[1]), min(sb - t, pixels.shape[0])
            if sr > sl and sb > st:
                pixels[st:sb, sl:sr] = 118
        return Image.fromarray(pixels)


def fingerprint(image: Image.Image) -> np.ndarray:
    """64×36 的灰度小图，用来比画面变了多少。"""
    return np.asarray(image.convert("L").resize((64, 36), Image.BILINEAR), dtype=np.float32)


def change(a: np.ndarray | None, b: np.ndarray) -> float:
    """两张小图分成 8×6 块，取变化最大的那一块的平均亮度差（0~1）。只动了一小块（比如角色在动）也算得出来。"""
    if a is None or a.shape != b.shape:
        return 1.0
    diff = np.abs(a - b).reshape(6, 6, 8, 8).mean(axis=(1, 3))
    return float(diff.max() / 255.0)


def scaled(image: Image.Image, long_side: int) -> Image.Image:
    w, h = image.size
    if max(w, h) <= long_side:
        return image
    k = long_side / max(w, h)
    return image.resize((int(w * k), int(h * k)), Image.LANCZOS)
