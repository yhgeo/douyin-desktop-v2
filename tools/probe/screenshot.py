"""截取指定进程的主窗口，用于人工核对界面。仅开发期使用。"""
import ctypes
import sys
import time
from ctypes import wintypes

from PIL import ImageGrab

user32 = ctypes.windll.user32
user32.SetProcessDPIAware()

target_pid = int(sys.argv[1])
out = sys.argv[2] if len(sys.argv) > 2 else "shot.png"

found = []


def enum_proc(hwnd, _):
    pid = wintypes.DWORD()
    user32.GetWindowThreadProcessId(hwnd, ctypes.byref(pid))
    if pid.value == target_pid and user32.IsWindowVisible(hwnd):
        length = user32.GetWindowTextLengthW(hwnd)
        if length > 0:
            found.append(hwnd)
    return True


EnumProc = ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HWND, wintypes.LPARAM)
user32.EnumWindows(EnumProc(enum_proc), 0)

if not found:
    print("no window")
    sys.exit(1)

hwnd = found[0]
user32.ShowWindow(hwnd, 9)  # SW_RESTORE
user32.SetForegroundWindow(hwnd)
time.sleep(1.0)

rect = wintypes.RECT()
user32.GetWindowRect(hwnd, ctypes.byref(rect))
box = (rect.left, rect.top, rect.right, rect.bottom)
img = ImageGrab.grab(bbox=box)
img.save(out)
print(f"saved {out} {img.size} hwnd={hwnd} rect={box}")
