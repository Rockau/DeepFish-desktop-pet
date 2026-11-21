"""鱼眼和托盘控制台之间的两张小纸条（都在鱼眼文件夹里）：

- control.json：托盘写，鱼眼读。{"enabled": 开没开, "chattiness": 话多话少}。鱼眼每秒看一眼，
  关掉了就自己收尾退出（改回设置、关掉视觉模型），话量改了马上生效。
- status.json：鱼眼写，托盘读。{"pid", "time", "state", "detail", "said", "error"}，每两秒更新一次，
  托盘靠 time 新不新判断鱼眼还在不在。
"""
from __future__ import annotations

import json
import os
import time


def _write(path: str, data: dict) -> None:
    temp = path + ".tmp"
    with open(temp, "w", encoding="utf-8") as file:
        json.dump(data, file, ensure_ascii=False)
    os.replace(temp, path)


class Control:
    def __init__(self, folder: str, chattiness: float) -> None:
        self.control_path = os.path.join(folder, "control.json")
        self.status_path = os.path.join(folder, "status.json")
        self.enabled = True
        self.chattiness = chattiness
        self._mtime = 0.0
        self._last_status = 0.0
        self.status: dict = {}

    def read(self) -> None:
        try:
            mtime = os.path.getmtime(self.control_path)
        except OSError:
            return
        if mtime == self._mtime:
            return
        self._mtime = mtime
        try:
            with open(self.control_path, encoding="utf-8") as file:
                data = json.load(file)
        except (OSError, ValueError):
            return
        self.enabled = bool(data.get("enabled", True))
        try:
            self.chattiness = max(0.3, min(3.0, float(data.get("chattiness", self.chattiness))))
        except (TypeError, ValueError):
            pass

    def mark_running(self) -> None:
        """自己开起来的（双击 bat）也算开着：写成 enabled，托盘的开关就跟着亮。"""
        data = {"enabled": True, "chattiness": self.chattiness}
        try:
            with open(self.control_path, encoding="utf-8") as file:
                old = json.load(file)
            data["chattiness"] = float(old.get("chattiness", self.chattiness))
            self.chattiness = data["chattiness"]
        except (OSError, ValueError, TypeError):
            pass
        _write(self.control_path, data)
        self._mtime = os.path.getmtime(self.control_path)

    def report(self, state: str, detail: str = "", said: str | None = None, error: str = "", force: bool = False) -> None:
        now = time.time()
        if said is not None:
            self.status["said"] = said
        changed = self.status.get("state") != state or self.status.get("detail") != detail
        if not force and not changed and now - self._last_status < 2:
            return
        self._last_status = now
        self.status.update(pid=os.getpid(), time=now, state=state, detail=detail, error=error, chattiness=self.chattiness)
        try:
            _write(self.status_path, self.status)
        except OSError:
            pass

    def closed(self, error: str = "") -> None:
        self.status.update(pid=0, time=time.time(), state="已关闭" if not error else "出错了", detail="", error=error)
        try:
            _write(self.status_path, self.status)
        except OSError:
            pass
