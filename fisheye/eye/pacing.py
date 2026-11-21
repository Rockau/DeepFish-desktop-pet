"""鱼眼的分寸：这一刻该不该开口。

每种场景一个门槛（值不值得说）和一个冷却（两句之间至少隔多久）。看剧要等对白的空档，打得激烈时只喊大事，
干活、聊天时基本不出声。她说了几句老大都没理她（没摸她、没点她），就慢慢少说；老大碰了她一下，又热络起来。
长时间没说话，门槛会慢慢降一点，免得一整局一声不吭。
"""
from __future__ import annotations

import difflib
import time
from dataclasses import dataclass


@dataclass(frozen=True)
class Rule:
    threshold: float    # salience 到这个数才说
    cooldown: float     # 两句之间至少隔几秒
    wait_gap: bool      # 要不要等没有对白/声音的空档


RULES = {
    "game_fight":   Rule(0.82, 30, False),
    "game_calm":    Rule(0.62, 45, False),
    "story_video":  Rule(0.72, 50, True),
    "casual_video": Rule(0.58, 32, True),
    "reading":      Rule(0.70, 150, False),
    "working":      Rule(0.97, 900, False),
    "chatting":     Rule(9.0, 0, False),     # 聊天软件：永远不说
    "idle":         Rule(0.92, 400, False),
    "other":        Rule(0.80, 180, False),
}

MIN_GAP = 12.0        # 不管什么场景，两句之间至少隔这么久
URGENT = 0.93         # 到这个数可以不等冷却（大事：翻盘、团灭、Boss）
PENDING_FOR = 8.0     # 等对白空档最多等几秒，等不到就算了


class Pacer:
    def __init__(self, chattiness: float = 1.0) -> None:
        # chattiness 越大越话多：>1 冷却变短、门槛变低
        self.chattiness = max(0.3, min(3.0, chattiness))
        self.last_spoke = 0.0
        self.last_topic = ""
        self.ignored = 0
        self.pending: tuple[float, dict] | None = None

    def touched(self) -> None:
        """老大摸了她、点了她：说明在看她，热络一点。"""
        self.ignored = 0

    def spoke(self, look: dict) -> None:
        self.last_spoke = time.time()
        self.last_topic = look.get("what", "")
        self.ignored += 1
        self.pending = None

    def _rule(self, look: dict) -> tuple[float, float, bool]:
        rule = RULES.get(look.get("scene", "other"), RULES["other"])
        threshold, cooldown = rule.threshold, rule.cooldown
        mood = look.get("mood", "")
        if look.get("scene") == "story_video" and mood in ("悲伤", "感人", "恐怖"):
            threshold, cooldown = threshold + 0.08, cooldown * 1.5   # 虐的、吓人的地方安静陪着
        if mood in ("欢乐", "尴尬") and look.get("scene") in ("casual_video", "game_calm"):
            threshold -= 0.05
        # 没人理她就慢慢少说（第三句之后开始，最多冷却翻倍）
        cooldown *= min(2.0, 1 + 0.15 * max(0, self.ignored - 2))
        cooldown /= self.chattiness
        threshold -= (self.chattiness - 1) * 0.08
        # 很久没说话了：门槛降一点
        if time.time() - self.last_spoke > cooldown * 3:
            threshold -= 0.12
        return threshold, cooldown, rule.wait_gap

    def decide(self, look: dict, gap: float, force: bool = False) -> str:
        """返回 "speak"（现在说）、"wait"（值得说，但要等空档）或不说的原因。"""
        now = time.time()
        since = now - self.last_spoke
        if force:
            return "speak"
        if look.get("private"):
            return "隐私画面"
        if since < MIN_GAP:
            return "刚说过"
        threshold, cooldown, wait_gap = self._rule(look)
        salience = look.get("salience", 0)
        if look.get("busy") and salience < 0.9:
            return "老大正忙"
        if salience < threshold:
            return f"不够有趣（{salience:.2f} < {threshold:.2f}）"
        if since < cooldown and salience < URGENT:
            return f"冷却中（还差 {cooldown - since:.0f} 秒）"
        if self.last_topic and look.get("change") != "big" and \
                difflib.SequenceMatcher(None, self.last_topic, look.get("what", "")).ratio() > 0.7:
            return "和上次说的是一回事"
        if wait_gap and gap < 1.5 and salience < URGENT:
            self.pending = (now, look)
            return "wait"
        return "speak"

    def pending_ready(self, gap: float) -> dict | None:
        """之前在等空档的那句：等到了就返回它，等太久就丢掉。"""
        if not self.pending:
            return None
        since, look = self.pending
        if time.time() - since > PENDING_FOR:
            self.pending = None
            return None
        if gap >= 1.5:
            self.pending = None
            return look
        return None
