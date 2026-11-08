"""Packs the DACT UI baseline into a Figma development plugin.

Usage (after `PackageSmokeTests --ui-baseline <baseline>` and `--ui-baseline-friends <friends>`,
both with DACT_NATIVE_UI_VECTOR=1):

    python tools/ui-baseline/build-figma-plugin.py <baseline> <friends> <plugin-output>

In Figma desktop: Plugins > Development > Import plugin from manifest..., pick
<plugin-output>/manifest.json, then run it inside the target file. It adds one page with every
screen as editable layers plus a hidden 1:1 reference render.
"""
import base64
import io
import json
import os
import sys

from PIL import Image

HERE = os.path.dirname(os.path.abspath(__file__))
MARGIN = 10

# Readable Chinese names for the enum-based capture names.
NAMES = {
    "Overview": "概览", "Meter": "战斗统计", "Overlays": "悬浮窗", "Extensions": "扩展",
    "Cloud": "云同步", "Diagnostics": "设置&账号",
    "账号-Login": "账号-登录", "账号-Register": "账号-注册", "账号-ResetPassword": "账号-重置密码",
    "Classic": "经典榜", "Horizontal": "横版", "RoleSplit": "职能分栏",
    "UsageNotice": "使用须知", "GettingStarted": "快速开始", "CombatMeter": "战斗统计",
    "Account": "账号", "Troubleshooting": "故障排查", "Commands": "命令", "Copyright": "版权",
}

# Friend captures come from the friends native test; its scaffolding windows are dropped.
FRIENDS = [
    ("friends-drawer-expanded", "好友抽屉（展开）", "挂在控制中心右侧"),
    ("friends-chat-zh-CN", "聊天窗口", "该截图使用测试中的雨夜皮肤"),
    ("friends-status-popup", "修改我的状态", "点击自己的状态打开"),
    ("friends-unread-requests", "好友申请（未读）", ""),
    ("friends-remark-editor", "好友备注编辑", "右键好友打开"),
    ("friends-notification-right-stack", "来信气泡（右侧堆叠）", "游戏内来信提示"),
    ("friends-notification-quick-reply", "来信气泡（快捷回复）", ""),
]
SCAFFOLDING = ("isolated", "##Tooltip")


def readable(name):
    for key, value in NAMES.items():
        if name == key or name.startswith(key + "-"):
            return value + name[len(key):]
    return name


def bounds(nodes, width, height):
    xs0, ys0, xs1, ys1 = [], [], [], []
    for n in nodes:
        h = n.get("lh", n.get("h", 0))
        xs0.append(n["x"]); ys0.append(n["y"]); xs1.append(n["x"] + n.get("w", 0)); ys1.append(n["y"] + h)
    x0 = max(0, int(min(xs0)) - MARGIN); y0 = max(0, int(min(ys0)) - MARGIN)
    x1 = min(width, int(max(xs1)) + MARGIN + 1); y1 = min(height, int(max(ys1)) + MARGIN + 1)
    return x0, y0, x1, y1


def shot(directory, stem, name, note, used):
    data = json.load(open(os.path.join(directory, stem + ".json"), encoding="utf-8"))
    nodes = [n for n in data["nodes"] if not n["layer"].startswith(SCAFFOLDING)]
    if not nodes:
        raise SystemExit(f"{stem}: nothing to import")
    x0, y0, x1, y1 = bounds(nodes, data["width"], data["height"])
    # The reference keeps the full render; JPEG keeps the plugin small enough to load.
    image = Image.open(os.path.join(directory, stem + ".png")).convert("RGB")
    buffer = io.BytesIO()
    image.save(buffer, "JPEG", quality=85)
    for n in nodes:
        if n["k"] == "image":
            used[n["src"]] = os.path.join(directory, "images", n["src"])
    return {
        "name": name, "note": note, "w": x1 - x0, "h": y1 - y0, "ox": x0, "oy": y0,
        "refW": data["width"], "refH": data["height"],
        "png": base64.b64encode(buffer.getvalue()).decode(), "nodes": nodes,
    }


def main(baseline, friends, output):
    index = json.load(open(os.path.join(baseline, "index.json"), encoding="utf-8"))
    used, groups, background = {}, {}, None
    for entry in index["shots"]:
        stem = os.path.splitext(entry["File"])[0]
        group = groups.setdefault(entry["Group"], [])
        group.append(shot(baseline, stem, readable(entry["Name"]), entry["Note"], used))
        if background is None:
            background = json.load(open(os.path.join(baseline, stem + ".json"), encoding="utf-8"))["background"]
    groups["08 好友"] = [shot(friends, stem, name, note, used) for stem, name, note in FRIENDS]

    version = index["version"]
    payload = {
        "pageName": f"现状 v{version}（1:1）",
        "title": f"DACT v{version} 界面现状（1:1 复刻）",
        "subtitle": "由真实 cimgui 绘制命令导出的可编辑图层（简体中文 · 默认皮肤 · 100% 字号）。"
                    "每个画板底部有隐藏锁定的「参考截图」，可开关对比。",
        "background": background[:3],
        "groups": [{"name": name, "shots": shots} for name, shots in groups.items()],
        "images": {name: base64.b64encode(open(path, "rb").read()).decode() for name, path in used.items()},
    }
    os.makedirs(output, exist_ok=True)
    template = open(os.path.join(HERE, "figma-import.js"), encoding="utf-8").read()
    code = template.replace("/*__DATA__*/ null", json.dumps(payload, ensure_ascii=False, separators=(",", ":")))
    open(os.path.join(output, "code.js"), "w", encoding="utf-8").write(code)
    manifest = {
        "name": f"DACT UI 基线导入 v{version}",
        "id": "dact-ui-baseline-import",
        "api": "1.0.0",
        "main": "code.js",
        "editorType": ["figma"],
        "documentAccess": "dynamic-page",
        "networkAccess": {"allowedDomains": ["none"]},
    }
    json.dump(manifest, open(os.path.join(output, "manifest.json"), "w", encoding="utf-8"), ensure_ascii=False, indent=2)
    count = sum(len(g["shots"]) for g in payload["groups"])
    nodes = sum(len(s["nodes"]) for g in payload["groups"] for s in g["shots"])
    print(f"{count} screens, {nodes} layers, {len(used)} images, code.js {len(code) / 1048576:.1f} MiB -> {output}")


if __name__ == "__main__":
    if len(sys.argv) != 4:
        raise SystemExit(__doc__)
    main(*sys.argv[1:])
