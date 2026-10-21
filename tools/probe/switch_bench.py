"""切换延迟基准测试。按方向键切视频，再从日志里算每次 loadfile -> file-loaded 的间隔。

用法: python switch_bench.py <pid> <次数> <间隔秒> [键: down/up]
输出: 每次耗时 + 统计（命中预取时应 < 300ms）
"""
import ctypes
import os
import sys
import time
from ctypes import wintypes
from datetime import datetime

user32 = ctypes.windll.user32

pid = int(sys.argv[1])
count = int(sys.argv[2]) if len(sys.argv) > 2 else 8
gap = float(sys.argv[3]) if len(sys.argv) > 3 else 3.0
key = sys.argv[4] if len(sys.argv) > 4 else "down"

VK = {"down": 0x28, "up": 0x26}
vk = VK[key]

log_path = os.path.join(
    os.environ["LOCALAPPDATA"], "DouyinDesktop", "logs",
    f"app-{datetime.now():%Y%m%d}.log",
)

found = []


def enum_proc(hwnd, _):
    p = wintypes.DWORD()
    user32.GetWindowThreadProcessId(hwnd, ctypes.byref(p))
    if p.value == pid and user32.IsWindowVisible(hwnd):
        if user32.GetWindowTextLengthW(hwnd) > 0:
            found.append(hwnd)
    return True


EnumProc = ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HWND, wintypes.LPARAM)
user32.EnumWindows(EnumProc(enum_proc), 0)
if not found:
    print("no window")
    sys.exit(1)

hwnd = found[0]
user32.ShowWindow(hwnd, 9)
user32.SetForegroundWindow(hwnd)
time.sleep(0.5)

start_mark = datetime.now()
print(f"开始：{count} 次 {key}，间隔 {gap}s")

for i in range(count):
    user32.keybd_event(vk, 0, 0, 0)
    time.sleep(0.04)
    user32.keybd_event(vk, 0, 2, 0)
    time.sleep(gap)

time.sleep(2.0)


def to_sec(ts: str) -> float:
    h, m, rest = ts.split(":")
    return int(h) * 3600 + int(m) * 60 + float(rest)


events = []
with open(log_path, encoding="utf-8") as f:
    for line in f:
        if len(line) < 14 or line[2] != ":":
            continue
        ts, _, msg = line.partition("  ")
        msg = msg.strip()
        if to_sec(ts) < to_sec(start_mark.strftime("%H:%M:%S")) - 1:
            continue
        if msg.startswith("loadfile "):
            events.append(("load", to_sec(ts), msg))
        elif msg == "file-loaded":
            events.append(("loaded", to_sec(ts), ""))

results = []
for idx, (kind, ts, msg) in enumerate(events):
    if kind != "load":
        continue
    for kind2, ts2, _ in events[idx + 1:]:
        if kind2 == "loaded":
            results.append((ts2 - ts) * 1000)
            break

print(f"\n采样 {len(results)} 次：")
for i, ms in enumerate(results):
    flag = "预取命中" if ms < 300 else "走网络"
    print(f"  #{i + 1:2d}  {ms:7.0f} ms   {flag}")

if results:
    hit = [x for x in results if x < 300]
    miss = [x for x in results if x >= 300]
    print(f"\n命中 {len(hit)}/{len(results)}")
    if hit:
        print(f"  命中平均 {sum(hit) / len(hit):.0f} ms   最小 {min(hit):.0f}   最大 {max(hit):.0f}")
    if miss:
        print(f"  未命中平均 {sum(miss) / len(miss):.0f} ms")
    print(f"  总体平均 {sum(results) / len(results):.0f} ms")
