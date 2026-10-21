"""给目标窗口发按键并截图，用于验证交互。仅开发期使用。

用法: python keyshot.py <pid> <out.png> <vk1,vk2,...> [等待秒数]
VK: DOWN=0x28 UP=0x26 SPACE=0x20 F=0x46 M=0x4D ESC=0x1B LEFT=0x25 RIGHT=0x27
"""
import ctypes
import sys
import time
from ctypes import wintypes

from PIL import ImageGrab

user32 = ctypes.windll.user32
user32.SetProcessDPIAware()

pid = int(sys.argv[1])
out = sys.argv[2]
keys = [int(k, 0) for k in sys.argv[3].split(",")] if len(sys.argv) > 3 else []
wait = float(sys.argv[4]) if len(sys.argv) > 4 else 2.0

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

# SetForegroundWindow 对"调用者不是前台进程"的情况会静默失败，
# 用 AttachThreadInput 借一下前台线程的输入队列，才能真正把焦点抢过来
fg = user32.GetForegroundWindow()
fg_thread = user32.GetWindowThreadProcessId(fg, None)
my_thread = ctypes.windll.kernel32.GetCurrentThreadId()
target_thread = user32.GetWindowThreadProcessId(hwnd, None)
user32.AttachThreadInput(my_thread, fg_thread, True)
user32.AttachThreadInput(my_thread, target_thread, True)
user32.BringWindowToTop(hwnd)
user32.SetForegroundWindow(hwnd)
user32.SetFocus(hwnd)
user32.AttachThreadInput(my_thread, target_thread, False)
user32.AttachThreadInput(my_thread, fg_thread, False)
time.sleep(0.5)

for vk in keys:
    user32.keybd_event(vk, 0, 0, 0)
    time.sleep(0.05)
    user32.keybd_event(vk, 0, 2, 0)
    time.sleep(0.15)

time.sleep(wait)

rect = wintypes.RECT()
user32.GetWindowRect(hwnd, ctypes.byref(rect))
box = (rect.left, rect.top, rect.right, rect.bottom)
img = ImageGrab.grab(bbox=box)
img.save(out)
print(f"saved {out} keys={keys} wait={wait}s")
