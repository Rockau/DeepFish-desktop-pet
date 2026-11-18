"""鱼眼：让大肥鱼陪你打游戏、看视频、看网页。

看：截最前面的窗口（盖掉大肥鱼自己），本机显卡上的 Qwen3-VL 看懂画面（图不出这台电脑）。
听：借插件的声音探针，知道电脑在不在出声；看视频时用 RapidOCR 读字幕跟剧情。
想：值得说的时候，把文字描述交给 DeepSeek，按鱼的人设写一两句。
说：通过插件的本机接口让桌面上的她说出来。

平时在托盘面板「更多设置 → 陪玩陪看」里开关、调话量；双击 启动鱼眼.bat 会多开一个窗口显示每一步（调试用）。
快捷键：Ctrl+Alt+F9 暂停/继续，Ctrl+Alt+F10 让她现在就说一句。安装见 README.md。
"""
from __future__ import annotations

import ctypes
import ctypes.wintypes as wt
import datetime
import json
import os
import re
import shutil
import subprocess
import sys
import threading
import time
from collections import deque

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)

from eye import screen  # noqa: E402
from eye.brain import SCENE_NAMES, Eyes, Writer  # noqa: E402
from eye.control import Control  # noqa: E402
from eye.ear import Ear  # noqa: E402
from eye.pacing import Pacer  # noqa: E402
from eye.pet import Pet  # noqa: E402

LOG_DIR = os.path.join(HERE, "logs")
os.makedirs(LOG_DIR, exist_ok=True)
_log_lock = threading.Lock()


def log(text: str, show: bool = True) -> None:
    stamp = datetime.datetime.now().strftime("%H:%M:%S")
    line = f"{stamp} {text}"
    if show:
        print(line, flush=True)
    with _log_lock, open(os.path.join(LOG_DIR, datetime.date.today().isoformat() + ".log"), "a", encoding="utf-8") as file:
        file.write(line + "\n")


def load_config() -> dict:
    path = os.path.join(HERE, "config.json")
    if not os.path.exists(path):  # 第一次运行：从样板复制一份（config.json 里会填 key，不进仓库）
        shutil.copyfile(os.path.join(HERE, "config.example.json"), path)
    with open(path, encoding="utf-8") as file:
        config = json.load(file)
    for key in ("plugin_dir", "llama_server", "vision_model", "vision_mmproj"):
        config[key] = os.path.normpath(os.path.join(HERE, config[key]))
    return config


# ---------- 本机视觉模型（llama.cpp 的 llama-server），跟着鱼眼一起开、一起关 ----------

def _job_kill_on_close() -> int:
    """Windows 作业对象：鱼眼不管怎么退出（包括直接关窗口、被强杀），llama-server 都跟着退。"""
    kernel32 = ctypes.windll.kernel32
    job = kernel32.CreateJobObjectW(None, None)

    class LIMIT(ctypes.Structure):
        _fields_ = [("PerProcessUserTimeLimit", ctypes.c_int64), ("PerJobUserTimeLimit", ctypes.c_int64),
                    ("LimitFlags", wt.DWORD), ("MinimumWorkingSetSize", ctypes.c_size_t),
                    ("MaximumWorkingSetSize", ctypes.c_size_t), ("ActiveProcessLimit", wt.DWORD),
                    ("Affinity", ctypes.c_size_t), ("PriorityClass", wt.DWORD), ("SchedulingClass", wt.DWORD)]

    class IO(ctypes.Structure):
        _fields_ = [(name, ctypes.c_uint64) for name in ("a", "b", "c", "d", "e", "f")]

    class EXTENDED(ctypes.Structure):
        _fields_ = [("Basic", LIMIT), ("Io", IO), ("ProcessMemoryLimit", ctypes.c_size_t),
                    ("JobMemoryLimit", ctypes.c_size_t), ("PeakProcessMemoryUsed", ctypes.c_size_t),
                    ("PeakJobMemoryUsed", ctypes.c_size_t)]

    info = EXTENDED()
    info.Basic.LimitFlags = 0x2000  # JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
    kernel32.SetInformationJobObject(job, 9, ctypes.byref(info), ctypes.sizeof(info))
    return job


def start_vision(config: dict, eyes: Eyes, beat=lambda: None) -> subprocess.Popen | None:
    if eyes.ready():
        log("视觉模型已经开着了")
        return None
    if not os.path.exists(config["llama_server"]):
        raise SystemExit("找不到 " + config["llama_server"] + "：还没装好，先运行 fisheye 文件夹里的 setup.ps1。")
    port = config["vision_url"].rsplit(":", 1)[-1].strip("/")
    out = open(os.path.join(LOG_DIR, "llama-server.log"), "w", encoding="utf-8")
    process = subprocess.Popen(
        [config["llama_server"], "-m", config["vision_model"], "--mmproj", config["vision_mmproj"],
         "-ngl", "99", "-c", "8192", "-np", "1", "--host", "127.0.0.1", "--port", port, "--no-webui"],
        stdout=out, stderr=subprocess.STDOUT, creationflags=0x08000000)
    job = _job_kill_on_close()
    handle = ctypes.windll.kernel32.OpenProcess(0x1F0FFF, False, process.pid)
    ctypes.windll.kernel32.AssignProcessToJobObject(job, handle)
    start_vision.job = job  # 留着句柄：关掉它 llama-server 就会退
    log("正在加载视觉模型（第一次大概二三十秒）……")
    for _ in range(240):
        if process.poll() is not None:
            raise SystemExit("视觉模型没开起来，看 logs\\llama-server.log")
        if eyes.ready():
            log("视觉模型好了")
            return process
        beat()
        time.sleep(0.5)
    raise SystemExit("视觉模型两分钟都没开好，看 logs\\llama-server.log")


# ---------- 全局快捷键 ----------

class Hotkeys(threading.Thread):
    PAUSE, NOW = 1, 2

    def __init__(self) -> None:
        super().__init__(daemon=True, name="hotkeys")
        self.paused = False
        self.force = threading.Event()
        self.toggled = threading.Event()

    def run(self) -> None:
        user32 = ctypes.windll.user32
        mods = 0x0001 | 0x0002 | 0x4000  # Alt | Ctrl | NoRepeat
        ok1 = user32.RegisterHotKey(None, self.PAUSE, mods, 0x78)
        ok2 = user32.RegisterHotKey(None, self.NOW, mods, 0x79)
        if not (ok1 and ok2):
            log("⚠ 快捷键 Ctrl+Alt+F9 / Ctrl+Alt+F10 被别的程序占了，用不了")
        msg = wt.MSG()
        while user32.GetMessageW(ctypes.byref(msg), None, 0, 0) > 0:
            if msg.message == 0x0312:  # WM_HOTKEY
                if msg.wParam == self.PAUSE:
                    self.paused = not self.paused
                    self.toggled.set()
                elif msg.wParam == self.NOW:
                    self.force.set()


# ---------- 主循环 ----------

class FishEye:
    TICK = 0.5            # 每 0.5 秒看一眼画面有没有变
    CHANGED = 0.06        # 画面变化到这个程度才让模型看
    LOOK_GAP = 2.5        # 模型两次看之间至少隔几秒
    LOOK_IDLE = 12.0      # 画面不变也每隔这么久看一次
    RECAP_EVERY = 180.0   # 每三分钟更新一次前情

    def __init__(self, config: dict) -> None:
        self.config = config
        self.pet = Pet(config["plugin_dir"])
        self.eyes = Eyes(config["vision_url"])
        key = config.get("deepseek_api_key") or ""
        base, model = config["deepseek_base_url"], config["deepseek_model"]
        if not key:
            key, plugin_base, plugin_model = self.pet.llm_settings()
            base, model = plugin_base or base, plugin_model or model
        with open(os.path.join(HERE, "人设.md"), encoding="utf-8") as file:
            persona = file.read()
        self.writer = Writer(base, key, model, persona) if key else None
        if not key:
            log("⚠ 还没填 DeepSeek 的 API key（config.json 的 deepseek_api_key，或插件设置的 LlmApiKey）：先只看不说。")
        self.screen = screen.Screen()
        self.ear = Ear(config["plugin_dir"])
        from eye.reader import Reader
        self.reader = Reader()
        self.control = Control(HERE, float(config.get("chattiness", 1.0)))
        self.control.mark_running()
        self.pacer = Pacer(self.control.chattiness)
        self.last_control = 0.0
        self.pet_lost_since = 0.0
        self.hotkeys = Hotkeys()
        self.ignore = {name.lower() for name in config.get("ignore_apps", [])}
        self.console = screen.own_console()

        self.last_window: screen.Window | None = None
        self.last_print = 0.0
        self.last_fp = None
        self.last_look_at = 0.0
        self.look: dict = {}
        self.notes: deque[str] = deque(maxlen=30)
        self.notes_since_recap = 0
        self.recap = ""
        self.last_recap = time.time()
        self.said: deque[str] = deque(maxlen=8)
        self.last_ocr = 0.0
        self.pet_state: dict = {}
        self.last_state = 0.0
        self.event_seq = 0
        self.last_events = 0.0
        self.restore_fullscreen: bool | None = None
        self.stopping = False

    # --- 和桌宠打交道 ---

    def refresh_pet(self) -> None:
        now = time.time()
        if now - self.last_state >= 4:
            self.last_state = now
            try:
                self.pet_state = self.pet.state()
                self.pet_lost_since = 0.0
            except Exception as error:
                self.pet_state = {"error": str(error)}
                self.pet_lost_since = self.pet_lost_since or now
        if now - self.last_events >= 3:
            self.last_events = now
            try:
                events = self.pet.events_after(self.event_seq)
                if events:
                    self.event_seq = max(event.get("sequence", 0) for event in events)
                    self.pacer.touched()
            except Exception:
                pass

    def can_speak(self) -> str:
        state = self.pet_state
        if "error" in state:
            return "连不上大肥鱼"
        if state.get("hidden"):
            return "她收起来了"
        if state.get("saying"):
            return "她正在说话"
        if (state.get("focus") or {}).get("phase") == "work":
            return "番茄钟专注中"
        return ""

    def say(self, text: str) -> None:
        if self.config.get("dry_run"):
            log("（试运行，不说出来）" + text.replace("\n", " ／ "))
            return
        self.pet.say(text)

    # --- 看 ---

    def current_window(self) -> screen.Window | None:
        window = screen.foreground()
        if window is None or window.minimized or window.hwnd == self.console or window.process in screen.SELF_PROCESSES \
                or window.area < 200 * 200:
            return self.last_window
        self.last_window = window
        return window

    def subtitles_matter(self) -> bool:
        return self.look.get("scene") in ("story_video", "casual_video", "game_calm", None) or bool(self.reader.recent(20))

    def gap(self) -> float:
        """对白空档：最近有字幕就看字幕停了多久；没字幕就不卡。"""
        return self.reader.gap() if self.reader.recent(30) else 99.0

    def look_context(self, window: screen.Window) -> str:
        parts = [f"窗口标题：{window.title or '（无）'}", f"程序：{window.process}"]
        if self.look:
            parts.append(f"上一次看到的（{time.time() - self.last_look_at:.0f} 秒前）：{self.look.get('what', '')}")
        subtitles = self.reader.recent(30, 3)
        if subtitles:
            parts.append("最近的字幕：" + " / ".join(subtitles))
        parts.append("声音：" + self.ear.describe())
        return "\n".join(parts)

    def observe(self, window: screen.Window, image) -> dict | None:
        try:
            look = self.eyes.look(screen.scaled(image, 1536), self.look_context(window))
        except Exception as error:
            log(f"⚠ 视觉模型出错：{error}")
            return None
        self.look, self.last_look_at = look, time.time()
        subtitle = (look.get("subtitle") or "").strip()
        if subtitle and look.get("scene") in ("story_video", "casual_video", "game_calm", "game_fight") and not look.get("private"):
            self.reader.remember(subtitle)
            # 还不知道字幕在哪一条：拿这句去整窗 OCR 里找位置（单独线程）
            if self.reader.region is None and not self.reader.busy and look.get("scene") in ("story_video", "casual_video", "game_calm"):
                threading.Thread(target=self._locate, args=(image, subtitle), daemon=True).start()
        # 干活、聊天、隐私画面不记进局势记录（这份记录会发给 DeepSeek 写台词）
        if look.get("scene") not in ("working", "chatting") and not look.get("private") and                 (look.get("change") != "none" or look.get("salience", 0) >= 0.5):
            stamp = datetime.datetime.now().strftime("%H:%M:%S")
            self.notes.append(f"{stamp} {look['what']}" + (f"（{look['detail']}）" if look.get("detail") else ""))
            self.notes_since_recap += 1
        log(f"👀 [{look['scene']}|{look['mood']}|{look['salience']:.2f}{'|忙' if look.get('busy') else ''}] {look['what']}"
            + (f" ／ {look['detail']}" if look.get("detail") else ""), show=True)
        return look

    # --- 说 ---

    def situation(self, window: screen.Window, look: dict, forced: bool) -> str:
        parts = [f"老大现在在{SCENE_NAMES.get(look.get('scene'), '用电脑')}（窗口：{window.title}，程序：{window.process}）。"]
        if self.recap:
            parts.append("前情：" + self.recap)
        if len(self.notes) > 1:
            parts.append("最近的画面记录：\n" + "\n".join(list(self.notes)[-6:-1]))
        parts.append("此刻的画面：" + look.get("what", ""))
        if look.get("detail"):
            parts.append("值得说的细节：" + look["detail"])
        parts.append("气氛：" + look.get("mood", "") + ("，老大正在紧张操作，只说一句很短的" if look.get("busy") else ""))
        subtitles = self.reader.recent(40, 4)
        if subtitles:
            parts.append("最近的字幕/台词：" + " / ".join(subtitles))
        parts.append("声音：" + self.ear.describe())
        if self.said:
            parts.append("你最近说过的话（不要重复，也别换个说法再说一遍）：\n" + "\n".join(self.said))
        if forced:
            parts.append("老大按了快捷键想听你说一句，这次一定要说，不要回 PASS。")
        parts.append("现在用一到两行说你的反应。")
        return "\n\n".join(parts)

    def speak(self, window: screen.Window, look: dict, forced: bool = False) -> None:
        if self.writer is None:
            log("💭 （这时候她想说一句，但还没填 DeepSeek key）")
            self.pacer.spoke(look)
            return
        try:
            line = self.writer.write(self.situation(window, look, forced))
        except Exception as error:
            log(f"⚠ DeepSeek 出错：{error}")
            self.pacer.last_spoke = time.time() - 5  # 别马上又问一遍
            return
        if not line:
            log("💭 （觉得没什么好说的）")
            self.pacer.last_topic = look.get("what", "")
            self.pacer.last_spoke = max(self.pacer.last_spoke, time.time() - 20)
            return
        log("🐟 " + line.replace("\n", " ／ "))
        try:
            self.say(line)
        except Exception as error:
            log(f"⚠ 让她说话失败：{error}")
            return
        self.said.append(line.replace("\n", " "))
        spoken = re.sub(r"〔[^〕]*〕", "", line).replace("\n", " ")
        self.control.report("陪着你", self.control.status.get("detail", ""), said=spoken, force=True)
        self.pacer.spoke(look)

    def maybe_recap(self) -> None:
        if self.writer is None or self.notes_since_recap < 4 or time.time() - self.last_recap < self.RECAP_EVERY:
            return
        self.last_recap = time.time()
        try:
            self.recap = self.writer.recap(self.recap, list(self.notes)[-12:])
            self.notes_since_recap = 0
            log("📒 前情：" + self.recap, show=False)
        except Exception as error:
            log(f"⚠ 更新前情失败：{error}", show=False)

    # --- 一圈 ---

    def tick(self) -> None:
        now = time.time()
        if now - self.last_control >= 1:
            self.last_control = now
            self.control.read()
            if abs(self.pacer.chattiness - self.control.chattiness) > 1e-6:
                self.pacer.chattiness = self.control.chattiness
                log(f"话量调成 {self.control.chattiness:.1f}")
            if not self.control.enabled:
                log("托盘里关掉了鱼眼")
                self.stopping = True
                return
        # 心跳：托盘靠 status.json 新不新判断鱼眼还在不在，所以不管这一圈干了什么都报一声（两秒最多写一次）
        self.control.report(self.control.status.get("state") or "陪着你", self.control.status.get("detail", ""))
        self.refresh_pet()
        if self.pet_lost_since and now - self.pet_lost_since > 60:
            log("大肥鱼一分钟都连不上（多半是关掉了），鱼眼也跟着退出")
            self.stopping = True
            return
        if self.hotkeys.toggled.is_set():
            self.hotkeys.toggled.clear()
            if self.hotkeys.paused:
                log("⏸ 暂停（再按 Ctrl+Alt+F9 继续）")
                self._safe_say("〔点头×2 微笑〕好，我先不看啦")
            else:
                log("▶ 继续")
                self._safe_say("〔笑眼〕那我接着陪你看~")
        if self.hotkeys.paused:
            self.control.report("暂停中", "按 Ctrl+Alt+F9 继续")
            return
        window = self.current_window()
        if window is None:
            return
        if window.process in self.ignore:
            self._status(f"🙈 {window.process} 是隐私程序，不看")
            self.control.report("陪着你", "隐私程序在前面，先不看")
            return
        forced = self.hotkeys.force.is_set()
        image = self.screen.grab(window)
        now = time.time()

        # 看视频时读字幕（单独的线程，不卡主循环）
        self.reader.forget_region((window.hwnd, window.rect[2] - window.rect[0], window.rect[3] - window.rect[1]))
        if self.reader.region and self.subtitles_matter() and now - self.last_ocr >= 1.0 and not self.reader.busy:
            self.last_ocr = now
            threading.Thread(target=self._read, args=(image,), daemon=True).start()

        fp = screen.fingerprint(image)
        moved = screen.change(self.last_fp, fp)
        due = forced or (moved >= self.CHANGED and now - self.last_look_at >= self.LOOK_GAP) \
            or now - self.last_look_at >= self.LOOK_IDLE
        look = None
        if due:
            self.last_fp = fp
            look = self.observe(window, image)
            if look:
                self.control.report("陪着你", "看到：" + look.get("what", ""))

        blocked = self.can_speak()
        if forced:
            self.hotkeys.force.clear()
            if blocked and blocked != "她正在说话":
                log(f"（{blocked}，没法说）")
            elif look:
                self.speak(window, look, forced=True)
            return
        if blocked:
            if look:
                self._status(f"🤐 {blocked}")
            return
        waiting = self.pacer.pending_ready(self.gap())
        if waiting:
            self.speak(window, waiting)
        elif look:
            decision = self.pacer.decide(look, self.gap())
            if decision == "speak":
                self.speak(window, look)
            elif decision == "wait":
                log("⏳ 等台词的空档再说", show=False)
            else:
                log("   不说：" + decision, show=False)
        self.maybe_recap()

    def _locate(self, image, subtitle: str) -> None:
        try:
            self.reader.busy = True
            if self.reader.locate(image, subtitle):
                log(f"🔎 找到字幕的位置了，之后每秒读一次", show=False)
        except Exception as error:
            log(f"⚠ 找字幕位置出错：{error}", show=False)
        finally:
            self.reader.busy = False

    def _read(self, image) -> None:
        try:
            text = self.reader.feed(image)
            if text:
                log("💬 字幕：" + text, show=False)
        except Exception as error:
            log(f"⚠ 读字幕出错：{error}", show=False)

    def _status(self, text: str) -> None:
        if time.time() - self.last_print > 30:
            self.last_print = time.time()
            log(text)

    def _safe_say(self, text: str) -> None:
        try:
            if not self.can_speak():
                self.say(text)
        except Exception:
            pass

    # --- 开始和收尾 ---

    def start(self) -> None:
        self.hotkeys.start()
        try:
            self.pet_state = self.pet.state()
        except Exception as error:
            raise SystemExit(f"连不上大肥鱼（{error}）：先双击桌面上的「DeepFish」图标把她叫出来，再开鱼眼。")
        if self.config.get("unhide_in_fullscreen") and self.pet_state.get("hide_on_fullscreen"):
            self.pet.call("update_settings", {"hide_on_fullscreen": False})
            self.restore_fullscreen = True
            log("（陪玩期间先关掉了“全屏时藏起来”，退出鱼眼时会改回去）")
        if self.config.get("show_on_start") and self.pet_state.get("hidden"):
            self.pet.call("show")
            time.sleep(2)
        self.last_state = 0
        self.refresh_pet()
        try:
            self.event_seq = max([e.get("sequence", 0) for e in self.pet.events_after(0)] or [0])
        except Exception:
            pass
        self._safe_say("〔挑一下眉 微笑〕鱼眼开好啦〔点头×2〕我陪你一起看~")
        self.control.report("陪着你", "刚开始看", force=True)
        log("开始陪你啦。Ctrl+Alt+F9 暂停/继续，Ctrl+Alt+F10 让她现在说一句。托盘面板“更多设置 → 陪玩陪看”里可以关掉。")

    def stop(self) -> None:
        if self.stopping:
            return
        self.stopping = True
        if self.restore_fullscreen:
            try:
                self.pet.call("update_settings", {"hide_on_fullscreen": True}, timeout=3)
            except Exception:
                pass
        self.ear.close()


def main() -> None:
    # 只开一个：托盘和双击 bat 都可能拉起它
    ctypes.windll.kernel32.CreateMutexW(None, False, "Local\\DeepFishFishEye")
    if ctypes.windll.kernel32.GetLastError() == 183:  # ERROR_ALREADY_EXISTS
        print("鱼眼已经开着了。")
        return
    if sys.stdout:
        os.system("title 鱼眼 · 大肥鱼陪玩")
    screen.make_dpi_aware()
    config = load_config()
    control = Control(HERE, float(config.get("chattiness", 1.0)))
    control.report("正在打开", "加载视觉模型……", force=True)
    vision = None
    fisheye = None
    error = ""
    try:
        fisheye = FishEye(config)
        fisheye.control.status = control.status
        vision = start_vision(config, fisheye.eyes, beat=lambda: control.report("正在打开", "加载视觉模型……"))

        # 有控制台时，直接点窗口右上角关掉也要收尾（改回设置）
        @ctypes.WINFUNCTYPE(wt.BOOL, wt.DWORD)
        def on_close(kind):
            fisheye.stop()
            return kind == 0  # Ctrl+C 自己处理；关窗口、注销时放行
        ctypes.windll.kernel32.SetConsoleCtrlHandler(on_close, True)
        main.on_close = on_close  # 别被回收

        fisheye.start()
        while not fisheye.stopping:
            started = time.time()
            try:
                fisheye.tick()
            except Exception as problem:
                log(f"⚠ 出错了，接着看：{problem!r}")
                time.sleep(2)
            time.sleep(max(0.05, FishEye.TICK - (time.time() - started)))
    except KeyboardInterrupt:
        pass
    except SystemExit as problem:
        error = str(problem)
        log("✗ " + error)
    except Exception as problem:
        error = repr(problem)
        log("✗ 鱼眼出错退出：" + error)
    finally:
        if fisheye:
            fisheye._safe_say("〔微笑 点头〕那我先不看啦")
            fisheye.stop()
        if vision and vision.poll() is None:
            vision.terminate()
        control.closed(error)
        log("鱼眼关了")
    if error and sys.stdout:
        raise SystemExit(1)


if __name__ == "__main__":
    main()
