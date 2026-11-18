"""通过大肥鱼插件的本机网页接口控制她（地址和口令在插件的 api.json 里，只有这台电脑连得上）。"""
from __future__ import annotations

import json
import os

import requests


class Pet:
    def __init__(self, plugin_dir: str) -> None:
        self.plugin_dir = plugin_dir
        self._api: dict | None = None

    def _load(self) -> dict:
        if self._api is None:
            with open(os.path.join(self.plugin_dir, "api.json"), encoding="utf-8") as file:
                self._api = json.load(file)
        return self._api

    def call(self, name: str, arguments: dict | None = None, timeout: float = 15):
        api = self._load()
        try:
            reply = requests.post(f"{api['url']}/call/{name}", json=arguments or {}, timeout=timeout,
                                  headers={"Authorization": "Bearer " + api["token"]})
        except requests.ConnectionError:
            self._api = None  # 桌宠重开过，端口和口令可能变了
            raise
        reply.raise_for_status()
        content = reply.json().get("content") or [{}]
        text = content[0].get("text", "")
        try:
            return json.loads(text)
        except ValueError:
            return text

    def state(self) -> dict:
        return self.call("get_state")

    def say(self, text: str) -> None:
        self.call("say", {"text": text}, timeout=30)

    def events_after(self, sequence: int) -> list[dict]:
        events = self.call("recent_events", {"after": sequence})
        return events if isinstance(events, list) else []

    def llm_settings(self) -> tuple[str, str, str]:
        """插件设置里填过 LLM key 的话就直接用（插件自己的碎碎念也用这个）。"""
        try:
            with open(os.path.join(self.plugin_dir, "settings.json"), encoding="utf-8") as file:
                settings = json.load(file)
            return settings.get("LlmApiKey", ""), settings.get("LlmBaseUrl", ""), settings.get("LlmModel", "")
        except (OSError, ValueError):
            return "", "", ""
