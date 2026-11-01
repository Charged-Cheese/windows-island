"""滚轮切页中间帧序列：SendInput 注入真实滚轮 + GDI 连拍 + 纯标准库写 PNG。
只做自测，不参与发布。用法：python tools/wheel_frames.py <格数> [--up]"""
import ctypes, time, sys, os, zlib, struct
from ctypes import wintypes as wt

u = ctypes.windll.user32
gdi32 = ctypes.windll.gdi32

class MOUSEINPUT(ctypes.Structure):
    _fields_ = [("dx", wt.LONG), ("dy", wt.LONG), ("mouseData", wt.DWORD),
                ("dwFlags", wt.DWORD), ("time", wt.DWORD), ("dwExtraInfo", ctypes.POINTER(wt.ULONG))]
class _U(ctypes.Union):
    _fields_ = [("mi", MOUSEINPUT)]
class INPUT(ctypes.Structure):
    _fields_ = [("type", wt.DWORD), ("u", _U)]

class BIH(ctypes.Structure):
    _fields_ = [("biSize", wt.DWORD), ("biWidth", wt.LONG), ("biHeight", wt.LONG),
                ("biPlanes", wt.WORD), ("biBitCount", wt.WORD), ("biCompression", wt.DWORD),
                ("biSizeImage", wt.DWORD), ("biXPelsPerMeter", wt.LONG),
                ("biYPelsPerMeter", wt.LONG), ("biClrUsed", wt.DWORD), ("biClrImportant", wt.DWORD)]

def wheel(n, up=False):
    d = 120 if up else -120
    for _ in range(n):
        u.SendInput(1, ctypes.byref(INPUT(type=0, u=_U(mi=MOUSEINPUT(0, 0, d & 0xFFFFFFFF, 0x0800, 0, None)))),
                    ctypes.sizeof(INPUT))

def grab(x, y, w, h):
    hdc = u.GetDC(0)
    mem = gdi32.CreateCompatibleDC(hdc)
    bmp = gdi32.CreateCompatibleBitmap(hdc, w, h)
    gdi32.SelectObject(mem, bmp)
    gdi32.BitBlt(mem, 0, 0, w, h, hdc, x, y, 0x00CC0020)
    bi = BIH(biSize=ctypes.sizeof(BIH), biWidth=w, biHeight=-h, biPlanes=1, biBitCount=32)
    buf = ctypes.create_string_buffer(w * h * 4)
    gdi32.GetDIBits(mem, bmp, 0, h, buf, ctypes.byref(bi), 0)
    gdi32.DeleteObject(bmp); gdi32.DeleteDC(mem); u.ReleaseDC(0, hdc)
    return buf.raw

def write_png(path, bgra, w, h):
    raw = bytearray()
    for y in range(h):
        raw.append(0)
        row = bgra[y*w*4:(y+1)*w*4]
        for x in range(w):
            i = x*4
            raw += bytes((row[i+2], row[i+1], row[i]))
    def chunk(t, d):
        c = struct.pack(">I", len(d)) + t + d
        return c + struct.pack(">I", zlib.crc32(t + d) & 0xFFFFFFFF)
    png = b"\x89PNG\r\n\x1a\n"
    png += chunk(b"IHDR", struct.pack(">IIBBBBB", w, h, 8, 2, 0, 0, 0))
    png += chunk(b"IDAT", zlib.compress(bytes(raw), 6))
    png += chunk(b"IEND", b"")
    open(path, "wb").write(png)

if __name__ == "__main__":
    steps = int(sys.argv[1]) if len(sys.argv) > 1 else 1
    up = "--up" in sys.argv
    outdir = os.path.join(os.path.dirname(__file__), "..", "artifacts", "wheelseq")
    os.makedirs(outdir, exist_ok=True)
    for f in os.listdir(outdir):
        if f.endswith(".png"): os.remove(os.path.join(outdir, f))
    X, Y, W, H = (2560-900)//2, 0, 900, 470
    wheel(steps, up)
    t0 = time.perf_counter()
    for i in range(20):
        time.sleep(0.018)
        b = grab(X, Y, W, H)
        ms = (time.perf_counter()-t0)*1000
        write_png(os.path.join(outdir, f"s{i:02d}.png"), b, W, H)
        print(f"s{i:02d} @ {ms:.0f}ms")
