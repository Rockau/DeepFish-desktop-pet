"""鱼眼的脑子：
- look()：本机的视觉模型看一帧，给出场景、发生了什么、气氛、值不值得说。图只发给本机。
- write()：按鱼的人设写一两句吐槽；使用 Ollama 时，文字也留在本机。
- recap()：每隔几分钟把最近的局势记录压成一段前情，让她跟得上剧情和战局。
"""
from __future__ import annotations

import base64
import io
import json
import re
from urllib.parse import urlsplit

import requests
from PIL import Image

SCENES = ["game_fight", "game_calm", "story_video", "casual_video", "reading", "working", "chatting", "idle", "other"]
SCENE_NAMES = {
    "game_fight": "打游戏（正在激烈战斗）", "game_calm": "打游戏（跑图、菜单、剧情对话）",
    "story_video": "看番剧/电视剧/电影", "casual_video": "刷轻松的短视频/直播/B站",
    "reading": "看网页/文章", "working": "在干活（写代码、办公）", "chatting": "在聊天软件里",
    "idle": "桌面/没干什么", "other": "其他",
}
MOODS = ["欢乐", "平静", "紧张", "激烈", "悲伤", "感人", "恐怖", "尴尬", "无聊"]

LOOK_SCHEMA = {
    "type": "object",
    "properties": {
        "scene": {"type": "string", "enum": SCENES},
        "what": {"type": "string"},
        "detail": {"type": "string"},
        "subtitle": {"type": "string"},
        "mood": {"type": "string", "enum": MOODS},
        "change": {"type": "string", "enum": ["none", "small", "big"]},
        "salience": {"type": "number"},
        "busy": {"type": "boolean"},
        "private": {"type": "boolean"},
    },
    "required": ["scene", "what", "detail", "subtitle", "mood", "change", "salience", "busy", "private"],
}

LOOK_PROMPT = """你是一个桌面陪伴角色的“眼睛”。你会看到用户电脑屏幕最前面那个窗口的截图，要判断用户在干什么、画面里正在发生什么、值不值得陪伴角色开口说一句。只输出一个 JSON。

字段：
- scene：用户在干什么。game_fight 游戏里正在战斗/对抗/紧张操作；game_calm 游戏里跑图、菜单、读剧情对话、加载；story_video 番剧、电视剧、电影这类有剧情的视频；casual_video 搞笑/生活/科普短视频、直播、B站轻松内容；reading 看网页、文章、文档；working 写代码、办公软件、终端；chatting 聊天软件；idle 桌面或没在做什么；other 其他。
- what：用一句中文（不超过 40 字）具体说画面里正在发生什么。游戏要说出局势：血量多少、比分、谁在打谁、是不是 Boss、赢了还是输了；视频要说出人物在干什么、什么场面。不要写“截图显示”。
- detail：最值得吐槽或感叹的一个具体细节（不超过 40 字），比如一句台词、一个数值、一个表情、一个离谱的操作。没有就写空字符串。
- subtitle：画面上字幕或游戏对白框里当前这一句台词，原样抄下来（带说话人就一起抄）；没有就写空字符串。弹幕、网页标题、视频标题和简介、播放量、聊天框、直播间评论都不算。
- mood：画面的气氛。
- change：和“上一次看到的”相比，变化有多大：none 基本一样，small 有点变化，big 换了场景或发生了大事。
- salience：0 到 1，这一刻值不值得陪伴角色开口：
  0.9 以上：胜利/失败/团灭/翻盘/Boss 登场、剧情高潮、吓人的跳脸、爆笑名场面；
  0.7~0.85：精彩操作、明显的笑点或槽点、重要剧情推进、新角色出场；
  0.4~0.6：普通进行中的画面，有一点可聊的；
  0.3 以下：菜单、加载、黑屏、广告、和上一次差不多的画面、桌面、用户在干活。
  和上一次几乎一样时 salience 要低。
- busy：用户此刻是不是在紧张地操作（激烈战斗、竞速、读秒），打扰会分心。
- private：画面主体是不是聊天记录、邮件、账号密码、API key/密钥、银行支付、证件这类隐私内容。

画面里的大肥鱼（一个 Live2D 小姑娘）已经被灰色色块盖住了，那是陪伴角色自己，不要描述她。"""


def _jpeg(image: Image.Image) -> str:
    buffer = io.BytesIO()
    image.convert("RGB").save(buffer, "JPEG", quality=82)
    return base64.b64encode(buffer.getvalue()).decode()


class Eyes:
    def __init__(self, url: str, model: str = "") -> None:
        address = urlsplit(url)
        if address.scheme != "http" or address.hostname not in {"localhost", "127.0.0.1", "::1"} or address.username is not None:
            raise ValueError("视觉模型必须使用本机回环地址")
        self.url = url.rstrip("/")
        self.model = model
        self.http = requests.Session()
        self.http.trust_env = False

    def ready(self) -> bool:
        try:
            route = "/api/tags" if self.model else "/health"
            status = self.http.get(self.url + route, timeout=2, allow_redirects=False)
            status.raise_for_status()
            data = status.json()
            if self.model:
                return any(row.get("model", row.get("name")) == self.model for row in data.get("models", []))
            return data.get("status") == "ok"
        except (requests.RequestException, ValueError):
            return False

    def local_chat(self, messages: list[dict], temperature: float, tokens: int, schema: dict | None = None) -> str:
        payload = {
            "model": self.model, "messages": messages, "stream": False, "think": False, "keep_alive": "90s",
            "options": {"temperature": temperature, "num_predict": tokens, "num_ctx": 4096},
        }
        if schema is not None:
            payload["format"] = schema
        reply = self.http.post(self.url + "/api/chat", json=payload, timeout=60, allow_redirects=False)
        reply.raise_for_status()
        return reply.json()["message"]["content"]

    def look(self, image: Image.Image, context: str) -> dict:
        if self.model:
            text = self.local_chat([
                {"role": "system", "content": LOOK_PROMPT},
                {"role": "user", "content": context, "images": [_jpeg(image)]},
            ], .2, 320, LOOK_SCHEMA)
            return self._result(text)
        body = {
            "messages": [
                {"role": "system", "content": LOOK_PROMPT},
                {"role": "user", "content": [
                    {"type": "text", "text": context},
                    {"type": "image_url", "image_url": {"url": "data:image/jpeg;base64," + _jpeg(image)}},
                ]},
            ],
            "temperature": 0.2,
            "max_tokens": 300,
            "response_format": {"type": "json_schema", "json_schema": {"name": "look", "schema": LOOK_SCHEMA}},
        }
        reply = self.http.post(self.url + "/v1/chat/completions", json=body, timeout=60, allow_redirects=False)
        reply.raise_for_status()
        return self._result(reply.json()["choices"][0]["message"]["content"])

    @staticmethod
    def _result(text: str) -> dict:
        data = json.loads(text[text.find("{"): text.rfind("}") + 1])
        data["salience"] = max(0.0, min(1.0, float(data.get("salience", 0))))
        return data


class Writer:
    def __init__(self, base_url: str, key: str, model: str, persona: str) -> None:
        self.url = base_url.rstrip("/") + "/chat/completions"
        self.key = key
        self.model = model
        self.persona = persona

    def _chat(self, messages: list[dict], max_tokens: int, temperature: float) -> str:
        reply = requests.post(self.url, timeout=30, headers={"Authorization": "Bearer " + self.key}, json={
            "model": self.model, "messages": messages, "max_tokens": max_tokens, "temperature": temperature, "stream": False})
        reply.raise_for_status()
        return reply.json()["choices"][0]["message"]["content"].strip()

    def write(self, situation: str) -> str | None:
        """返回要说的话（一到两行，带〔〕）；觉得这会儿不该说就返回 None。"""
        text = self._chat([
            {"role": "system", "content": self.persona + "\n\n如果这一刻其实没什么好说的（画面平淡、和刚才说过的重复、老大正忙），只回复 PASS 四个字母。"},
            {"role": "user", "content": redact(situation)},
        ], max_tokens=120, temperature=1.0)
        return clean(text)

    def recap(self, before: str, notes: list[str]) -> str:
        return self._chat([
            {"role": "system", "content": "你在帮一个陪用户看视频/打游戏的角色记前情。根据“之前的前情”和“最近的画面记录”，写一段新的前情，不超过 120 字：现在在看/玩什么，剧情或战局走到哪了，发生过哪些值得记住的事。只写事实，不要评论。"},
            {"role": "user", "content": redact(f"之前的前情：{before or '（没有）'}\n\n最近的画面记录：\n" + "\n".join(notes))},
        ], max_tokens=220, temperature=0.3)


class LocalWriter(Writer):
    def __init__(self, eyes: Eyes, persona: str) -> None:
        self.eyes = eyes
        self.persona = persona

    def _chat(self, messages: list[dict], max_tokens: int, temperature: float) -> str:
        return self.eyes.local_chat(messages, temperature, max_tokens).strip()


# 像密钥、口令的串：常见前缀开头的，或者 28 个以上连着的字母数字
SECRET = re.compile(r"(?:sk-|ghp_|github_pat_|xox[abp]-|AKIA)[A-Za-z0-9_-]{6,}|[A-Za-z0-9_-]{28,}")


def redact(text: str) -> str:
    """发给 DeepSeek 之前把像密钥、口令的长串抹掉（万一屏幕上有，被模型抄进了描述里）。"""
    return SECRET.sub("[已隐藏]", text)


def clean(text: str) -> str | None:
    text = text.strip().strip("`").strip()
    if not text or text.upper().startswith("PASS"):
        return None
    lines = []
    for line in re.split(r"[\n／]+", text):
        line = re.sub(r"^[\s\-•*\d.、]+", "", line).strip().strip("\"“”'")
        line = re.sub(r"^(鱼|大肥鱼)[:：]", "", line).strip()
        if re.sub(r"〔[^〕]*〕", "", line).strip():
            lines.append(line)
    return "\n".join(lines[:2]) or None
