"""滚轮翻页实测探针：注入真实 WM_MOUSEWHEEL（SendInput），同时高频抓岛区域，
用帧间像素差判断"页面还在动 / 已静止"，验证位移时间线。只做自测，不参与发布。"""
import ctypes, time, sys, os
from ctypes import wintypes as wt

u = ctypes.windll.user32

# --- SendInput 结构 ---
class MOUSEINPUT(ctypes.Structure):
    _fields_ = [("dx", wt.LONG), ("dy", wt.LONG), ("mouseData", wt.DWORD),
                ("dwFlags", wt.DWORD), ("time", wt.DWORD),
                ("dwExtraInfo", ctypes.POINTER(wt.ULONG))]

class _INPUTunion(ctypes.Union):
    _fields_ = [("mi", MOUSEINPUT)]

class INPUT(ctypes.Structure):
    _fields_ = [("type", wt.DWORD), ("u", _INPUTunion)]

MOUSEEVENTF_WHEEL = 0x0800
WHEEL_DELTA = 120

def wheel(steps=1, up=False):
    data = WHEEL_DELTA if up else -WHEEL_DELTA
    for _ in range(abs(steps)):
        inp = INPUT(type=0, u=_INPUTunion(mi=MOUSEINPUT(0, 0, data & 0xFFFFFFFF, MOUSEEVENTF_WHEEL, 0, None)))
        u.SendInput(1, ctypes.byref(inp), ctypes.sizeof(INPUT))

# --- 抓图 ---
from ctypes import windll
gdi32 = windll.gdi32
user32 = windll.user32

class BITMAPINFOHEADER(ctypes.Structure):
    _fields_ = [("biSize", wt.DWORD), ("biWidth", wt.LONG), ("biHeight", wt.LONG),
                ("biPlanes", wt.WORD), ("biBitCount", wt.WORD), ("biCompression", wt.DWORD),
                ("biSizeImage", wt.DWORD), ("biXPelsPerMeter", wt.LONG),
                ("biYPelsPerMeter", wt.LONG), ("biClrUsed", wt.DWORD), ("biClrImportant", wt.DWORD)]

def grab(x, y, w, h):
    hdc = user32.GetDC(0)
    memdc = gdi32.CreateCompatibleDC(hdc)
    bmp = gdi32.CreateCompatibleBitmap(hdc, w, h)
    gdi32.SelectObject(memdc, bmp)
    gdi32.BitBlt(memdc, 0, 0, w, h, hdc, x, y, 0x00CC0020)
    bi = BITMAPINFOHEADER(biSize=ctypes.sizeof(BITMAPINFOHEADER), biWidth=w, biHeight=-h,
                          biPlanes=1, biBitCount=32, biCompression=0)
    buf = ctypes.create_string_buffer(w * h * 4)
    gdi32.GetDIBits(memdc, bmp, 0, h, buf, ctypes.byref(bi), 0)
    gdi32.DeleteObject(bmp); gdi32.DeleteDC(memdc); user32.ReleaseDC(0, hdc)
    return bytes(buf)

def diff(a, b):
    if len(a) != len(b): return 999.0
    s = 0; n = 0
    for i in range(0, len(a), 4 * 97):   # 稀疏采样
        s += abs(a[i] - b[i]); n += 1
    return s / max(n, 1)

def main():
    pid = int(sys.argv[1])
    outdir = sys.argv[2] if len(sys.argv) > 2 else "."
    # 岛区域：屏幕顶部居中，600x470 逻辑 px @1.5 = 900x705 物理 px
    W, H = 900, 470
    X = (2560 - W) // 2
    Y = 0
    frames = []
    t0 = time.perf_counter()
    prev = grab(X, Y, W, H)
    wheel(1)
    for i in range(40):
        time.sleep(0.016)
        cur = grab(X, Y, W, H)
        d = diff(prev, cur)
        frames.append(((time.perf_counter() - t0) * 1000, d))
        prev = cur
    print("t_ms\tdiff")
    for t, d in frames:
        mark = "  <-- 仍在移动" if d > 0.6 else ""
        print(f"{t:7.1f}\t{d:6.2f}{mark}")

if __name__ == "__main__":
    main()
