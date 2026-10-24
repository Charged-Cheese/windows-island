"""通过 WM_COPYDATA 向灵动岛发送控制命令（不依赖子进程，避免沙箱阻塞）。

用法：
    python island_cmd.py <pid> expand
    python island_cmd.py <pid> tab:2 notify:标题|正文
"""
import ctypes
import sys
import time
from ctypes import wintypes as wt


class COPYDATASTRUCT(ctypes.Structure):
    _fields_ = [("dwData", ctypes.c_void_p),
                ("cbData", ctypes.c_int),
                ("lpData", ctypes.c_void_p)]


u = ctypes.windll.user32
u.SendMessageW.restype = ctypes.c_void_p


def main_window(pid):
    found = []

    @ctypes.WINFUNCTYPE(ctypes.c_bool, ctypes.c_void_p, ctypes.c_void_p)
    def cb(hwnd, _):
        owner = wt.DWORD()
        u.GetWindowThreadProcessId(hwnd, ctypes.byref(owner))
        if owner.value == pid and u.IsWindowVisible(hwnd):
            length = u.GetWindowTextLengthW(hwnd)
            if length > 0:
                buf = ctypes.create_unicode_buffer(length + 1)
                u.GetWindowTextW(hwnd, buf, length + 1)
                found.append((hwnd, buf.value))
        return True

    u.EnumWindows(cb, 0)
    return found


def send(hwnd, command):
    buf = ctypes.create_unicode_buffer(command)
    cds = COPYDATASTRUCT(0, len(command) * 2, ctypes.cast(buf, ctypes.c_void_p))
    return u.SendMessageW(hwnd, 0x004A, 0, ctypes.byref(cds))


def main():
    if len(sys.argv) < 3:
        print("usage: island_cmd.py <pid> <command> [command...]")
        return 2

    pid = int(sys.argv[1])
    windows = main_window(pid)
    if not windows:
        print(f"ERROR: pid {pid} 没有可见窗口")
        return 1

    hwnd = windows[0][0]
    for command in sys.argv[2:]:
        result = send(hwnd, command)
        print(f"hwnd={hwnd} cmd={command} -> {result}")
        time.sleep(0.7)
    return 0


if __name__ == "__main__":
    sys.exit(main())
