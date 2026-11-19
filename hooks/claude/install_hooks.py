#!/usr/bin/env python3
# 把 fish_hook.py 接进 Claude Code 的 ~/.claude/settings.json（干活时的反应：开工、进度、出错、测试通过、做完）。
# setup.ps1 装完时会问一句，也可以自己运行：
#   python install_hooks.py            接上（已经接过的不重复；插件挪了位置会把路径改成新的）
#   python install_hooks.py --status   只看状态：退出码 0 = 已经接好，3 = 还没接或路径不对，2 = settings.json 读不了
#   python install_hooks.py --remove   拆掉（只拆指向 fish_hook.py 的那几条，别的 hook 不动）
#   python install_hooks.py --dry-run  只说要改什么，不写文件
# 做法：只往 hooks 里追加，不覆盖你原来的；改之前先备份成 settings.json.bak-fish-时间；settings.json 解析不了就一个字不动。
import argparse
import json
import os
import shutil
import sys
import tempfile
import time

HERE = os.path.dirname(os.path.abspath(__file__))
SCRIPT = os.path.join(HERE, "fish_hook.py")
MARK = "fish_hook.py"

# (事件, matcher)。进度简报要读任务清单，所以 PostToolUse 有两条。
EVENTS = [
    ("Notification", None),
    ("Stop", None),
    ("SubagentStop", None),
    ("PreToolUse", "Bash"),
    ("PostToolUse", "Bash"),
    ("PostToolUse", "TodoWrite|mcp__ccd_turn__update_plan|mcp__ccd_turn__wrap_up"),
    ("PostToolUseFailure", None),
    ("SessionStart", None),
    ("UserPromptSubmit", None),
    ("PreCompact", None),
]


def python_command():
    exe = shutil.which("python") or sys.executable
    # 命令里写 python（和手册一致）；PATH 里没有 python 时才用当前这个解释器的完整路径
    return "python" if shutil.which("python") else '"%s"' % exe


def wanted_command():
    return '%s "%s"' % (python_command(), SCRIPT)


def is_ours(hook):
    return isinstance(hook, dict) and MARK in str(hook.get("command", ""))


def settings_path():
    return os.path.join(os.path.expanduser("~"), ".claude", "settings.json")


def load(path):
    if not os.path.exists(path):
        return {}
    with open(path, "r", encoding="utf-8-sig") as f:
        data = json.load(f)
    if not isinstance(data, dict):
        raise ValueError("settings.json 最外面不是一个对象")
    return data


def find_group(groups, matcher):
    for group in groups:
        if isinstance(group, dict) and group.get("matcher") == matcher and any(is_ours(h) for h in group.get("hooks", [])):
            return group
    return None


def apply(data, command):
    """把 hook 补齐。返回改了几处。"""
    changes = 0
    hooks = data.setdefault("hooks", {})
    if not isinstance(hooks, dict):
        raise ValueError("settings.json 里的 hooks 不是对象，没动")
    for event, matcher in EVENTS:
        groups = hooks.setdefault(event, [])
        if not isinstance(groups, list):
            raise ValueError("hooks.%s 不是列表，没动" % event)
        group = find_group(groups, matcher)
        if group is None:
            entry = {"hooks": [{"type": "command", "command": command, "timeout": 6}]}
            if matcher:
                entry = {"matcher": matcher, **entry}
            groups.append(entry)
            changes += 1
            continue
        for hook in group["hooks"]:
            if is_ours(hook) and (hook.get("command") != command or hook.get("timeout") != 6):
                hook["type"] = "command"
                hook["command"] = command
                hook["timeout"] = 6
                changes += 1
    # 以前接过、现在不需要的（比如别处留下的指向旧路径的重复条目）不碰：只处理上面这几组
    return changes


def remove(data):
    changes = 0
    hooks = data.get("hooks")
    if not isinstance(hooks, dict):
        return 0
    for event in list(hooks):
        groups = hooks[event]
        if not isinstance(groups, list):
            continue
        kept = []
        for group in groups:
            if isinstance(group, dict) and isinstance(group.get("hooks"), list):
                before = len(group["hooks"])
                group["hooks"] = [h for h in group["hooks"] if not is_ours(h)]
                changes += before - len(group["hooks"])
                if not group["hooks"]:
                    continue
            kept.append(group)
        if kept:
            hooks[event] = kept
        else:
            del hooks[event]
    if not hooks:
        del data["hooks"]
    return changes


def save(path, data):
    os.makedirs(os.path.dirname(path), exist_ok=True)
    backup = None
    if os.path.exists(path):
        backup = path + ".bak-fish-" + time.strftime("%Y%m%d-%H%M%S")
        shutil.copy2(path, backup)
    fd, temp = tempfile.mkstemp(dir=os.path.dirname(path), prefix="settings.", suffix=".tmp")
    try:
        with os.fdopen(fd, "w", encoding="utf-8", newline="\n") as f:
            json.dump(data, f, ensure_ascii=False, indent=2)
            f.write("\n")
        os.replace(temp, path)
    except BaseException:
        if os.path.exists(temp):
            os.remove(temp)
        raise
    return backup


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--status", action="store_true")
    parser.add_argument("--remove", action="store_true")
    parser.add_argument("--dry-run", action="store_true")
    args = parser.parse_args()

    if not os.path.exists(SCRIPT):
        print("找不到 %s，没有动 settings.json。" % SCRIPT)
        return 2
    path = settings_path()
    try:
        data = load(path)
    except Exception as error:
        print("读不了 %s（%s），一个字都没动。" % (path, error))
        return 2

    if args.remove:
        changes = remove(data)
        if changes == 0:
            print("settings.json 里没有大肥鱼的 hook，不用拆。")
            return 0
        if args.dry_run or args.status:
            print("会拆掉 %d 条大肥鱼的 hook。" % changes)
            return 3 if args.status else 0
        backup = save(path, data)
        print("拆掉了 %d 条大肥鱼的 hook（备份：%s）。重启 Claude 后生效。" % (changes, backup))
        return 0

    try:
        changes = apply(data, wanted_command())
    except ValueError as error:
        print("%s，一个字都没动。" % error)
        return 2
    if changes == 0:
        print("干活时的反应已经接好了，不用再改。")
        return 0
    if args.status:
        print("还没接好（要改 %d 处）。" % changes)
        return 3
    if args.dry_run:
        print("会改 %d 处：%s" % (changes, path))
        return 0
    backup = save(path, data)
    print("接上了（改了 %d 处）。原来的 hook 都还在%s。重启 Claude 后生效。" % (changes, "，备份：" + backup if backup else ""))
    return 0


if __name__ == "__main__":
    sys.exit(main())
