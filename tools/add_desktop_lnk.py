"""把桌面快捷方式加入灵动岛图标坞（在 Python 内部取路径，避免 shell 编码破坏中文）。"""
import glob
import os
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from island_cmd import main_window, send  # noqa: E402


def main():
    if len(sys.argv) < 2:
        print("usage: add_desktop_lnk.py <pid> [count]")
        return 2

    pid = int(sys.argv[1])
    count = int(sys.argv[2]) if len(sys.argv) > 2 else 3

    windows = main_window(pid)
    if not windows:
        print(f"ERROR: pid {pid} 没有可见窗口")
        return 1

    hwnd = windows[0][0]
    desktop = os.path.join(os.path.expanduser("~"), "Desktop")
    lnks = sorted(glob.glob(os.path.join(desktop, "*.lnk")))[:count]

    if not lnks:
        print(f"桌面上没有 .lnk：{desktop}")
        return 1

    for lnk in lnks:
        print(f"send launcher: {os.path.basename(lnk)}")
        result = send(hwnd, "launcher:" + lnk)
        print(f"  -> {result}")
        time.sleep(0.5)

    send(hwnd, "tab:3")
    return 0


if __name__ == "__main__":
    sys.exit(main())
