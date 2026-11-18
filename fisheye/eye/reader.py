"""鱼眼读字幕：RapidOCR（CPU）。

视频不一定铺满窗口（网页里的播放器、直播间），字幕的位置不固定。所以先让视觉模型抄下它看到的那句字幕，
再在整个窗口的 OCR 结果里找到这句字在哪，记住这一条横带；之后每秒只读这一条，又快又不会读到网页上别的字。
窗口变了（换了窗口、改了大小）就重新找。
"""
from __future__ import annotations

import difflib
import threading
import time
from collections import deque

import numpy as np
from PIL import Image


def _similar(a: str, b: str) -> float:
    return difflib.SequenceMatcher(None, a, b).ratio()


class Reader:
    def __init__(self) -> None:
        from rapidocr import RapidOCR
        self._ocr = RapidOCR(params={"Global.log_level": "error"})
        self._lock = threading.Lock()
        self.lines: deque[tuple[float, str]] = deque(maxlen=40)   # (时间, 字幕)
        self.last_change = 0.0
        self.busy = False
        self.region: tuple[float, float, float, float] | None = None   # 字幕横带（占窗口的比例）
        self.region_key = None
        self.misses = 0

    def _read(self, image: Image.Image, max_width: int) -> list[tuple[str, float, list]]:
        """OCR 这张图，返回 [(字, 置信度, 四个角的坐标（占图的比例）)]。"""
        k = min(1.0, max_width / image.width)
        small = image.resize((int(image.width * k), int(image.height * k))) if k < 1 else image
        with self._lock:
            result = self._ocr(np.asarray(small))
        if not result or result.txts is None:
            return []
        w, h = small.size
        return [(text.strip(), float(score), [(x / w, y / h) for x, y in box])
                for text, score, box in zip(result.txts, result.scores, result.boxes) if text.strip()]

    def forget_region(self, key) -> None:
        if key != self.region_key:
            self.region, self.region_key, self.misses = None, key, 0

    def locate(self, image: Image.Image, subtitle: str) -> bool:
        """在整窗 OCR 里找视觉模型抄下来的那句字幕，找到就记住它所在的横带。"""
        best, box = 0.0, None
        for text, score, corners in self._read(image, 1600):
            ratio = _similar(text, subtitle) if len(text) >= 2 else 0
            if ratio > best and score >= 0.6:
                best, box = ratio, corners
        if best < 0.55 or box is None:
            return False
        xs, ys = [x for x, _ in box], [y for _, y in box]
        top, bottom = min(ys), max(ys)
        height = bottom - top
        center = (min(xs) + max(xs)) / 2
        half = max((max(xs) - min(xs)) * 0.8, 0.22)
        # 横带：上下放宽，字幕换成两行也读得到；左右以这句字幕为中心放宽，不把旁边的弹幕栏、评论区读进来
        self.region = (max(0.0, center - half), max(0.0, top - height * 1.6), min(1.0, center + half), min(1.0, bottom + height * 1.2))
        self.misses = 0
        return True

    def feed(self, image: Image.Image) -> str | None:
        """读一次字幕横带；出现了新的一句就记下来并返回它。"""
        if self.busy or not self.region:
            return None
        self.busy = True
        try:
            l, t, r, b = self.region
            band = image.crop((int(l * image.width), int(t * image.height), int(r * image.width), int(b * image.height)))
            # 只要横带中间那一段的字（字幕居中），两边零星的字是别的东西
            found = [(corners[0], text) for text, score, corners in self._read(band, 1600)
                     if score >= 0.7 and len(text) >= 2 and 0.2 <= (corners[0][0] + corners[2][0]) / 2 <= 0.8]
        finally:
            self.busy = False
        if not found:
            return None
        # 按行（高度差不多算一行）、再从左到右排：一行字被 OCR 拆成两段时顺序也对
        found.sort(key=lambda item: (round(item[0][1] * 12), item[0][0]))
        self.remember(" ".join(text for _, text in found[-2:]))
        return self.lines[-1][1] if self.last_change > time.time() - 0.5 else None

    def remember(self, text: str) -> None:
        """记一句字幕（OCR 读到的，或者视觉模型抄的），和上一句差不多就不算新的。"""
        text = text.strip()
        if len(text) < 2:
            return
        if any(_similar(old, text) >= 0.75 for _, old in list(self.lines)[-3:]):
            return
        now = time.time()
        self.lines.append((now, text))
        self.last_change = now

    def recent(self, seconds: float = 40, count: int = 4) -> list[str]:
        now = time.time()
        return [text for t, text in self.lines if now - t <= seconds][-count:]

    def gap(self) -> float:
        """距上一句新字幕几秒。"""
        return time.time() - self.last_change if self.last_change else 999.0
