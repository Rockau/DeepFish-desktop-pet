"""鱼眼的耳朵：借大肥鱼插件现成的 audio\\probe.ps1（WASAPI loopback，只算音量，不录音），
听电脑正在放的声音：有没有声音、安静了多久、是哪个程序在出声。
"""
from __future__ import annotations

import json
import os
import subprocess
import threading
import time
from collections import deque


class Ear:
    # 声卡底噪大约 0.0008（插件里量过），比这个高一截才算有声音
    SOUND = 0.004

    def __init__(self, plugin_dir: str) -> None:
        self.probe = os.path.join(plugin_dir, "audio", "probe.ps1")
        self.level = 0.0
        self.apps: list[str] = []
        self.last_sound = 0.0
        self.sound_since = 0.0
        self.ok = False
        self.error = ""
        self._recent: deque[tuple[float, float]] = deque(maxlen=60)
        self._process: subprocess.Popen | None = None
        threading.Thread(target=self._run, daemon=True, name="ear").start()

    def _run(self) -> None:
        while True:
            try:
                self._listen()
            except Exception as error:  # 探针挂了：等一会儿重开
                self.error = str(error)
            self.ok = False
            time.sleep(5)

    def _listen(self) -> None:
        if not os.path.exists(self.probe):
            self.error = "找不到 " + self.probe
            time.sleep(60)
            return
        self._process = subprocess.Popen(
            ["powershell", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", self.probe,
             "-IntervalMs", "100", "-ParentPid", str(os.getpid())],
            stdout=subprocess.PIPE, stderr=subprocess.DEVNULL, text=True, encoding="utf-8",
            creationflags=0x08000000)  # CREATE_NO_WINDOW
        for line in self._process.stdout:
            try:
                data = json.loads(line)
            except ValueError:
                continue
            if "error" in data:
                self.error = data.get("message") or data["error"]
                break
            if data.get("ready"):
                self.ok, self.error = True, ""
                continue
            now = time.time()
            rms = float(data.get("rms", 0))
            self.level = rms
            self._recent.append((now, rms))
            if rms >= self.SOUND:
                if now - self.last_sound > 1.5:
                    self.sound_since = now
                self.last_sound = now
            self.apps = [name for name, _ in data.get("apps", []) if name not in ("powershell", "pwsh")]

    def playing(self) -> bool:
        return time.time() - self.last_sound < 0.8

    def quiet_for(self) -> float:
        """已经安静了几秒（一直在响就是 0）。"""
        return 0.0 if self.playing() else time.time() - self.last_sound

    def describe(self) -> str:
        if not self.ok:
            return "（听不到电脑的声音）"
        if self.playing():
            who = "、".join(self.apps[:3])
            loud = "很响" if self.level > 0.08 else "有声音" if self.level > 0.015 else "声音很小"
            return f"电脑在出声（{loud}{'，来自 ' + who if who else ''}）"
        quiet = self.quiet_for()
        return "电脑很安静" if quiet > 30 else f"刚安静了 {quiet:.0f} 秒"

    def close(self) -> None:
        if self._process and self._process.poll() is None:
            self._process.kill()
