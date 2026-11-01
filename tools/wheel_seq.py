"""滚动时连续截图，存成序列，用来目视检验过渡中间帧。"""
import ctypes, time, sys, os
from ctypes import wintypes as wt
u = ctypes.windll.user32
class MOUSEINPUT(ctypes.Structure):
    _fields_ = [("dx", wt.LONG), ("dy", wt.LONG), ("mouseData", wt.DWORD),
                ("dwFlags", wt.DWORD), ("time", wt.DWORD), ("dwExtraInfo", ctypes.POINTER(wt.ULONG))]
class _U(ctypes.Union):
    _fields_ = [("mi", MOUSEINPUT)]
class INPUT(ctypes.Structure):
    _fields_ = [("type", wt.DWORD), ("u", _U)]
def wheel(n, up=False):
    d = 120 if up else -120
    for _ in range(n):
        u.SendInput(1, ctypes.byref(INPUT(type=0, u=_U(mi=MOUSEINPUT(0,0,d & 0xFFFFFFFF,0x0800,0,None)))), ctypes.sizeof(INPUT))
gdi32 = ctypes.windll.gdi32
class BIH(ctypes.Structure):
    _fields_ = [("biSize", wt.DWORD), ("biWidth", wt.LONG), ("biHeight", wt.LONG),
                ("biPlanes", wt.WORD), ("biBitCount", wt.WORD), ("biCompression", wt.DWORD),
                ("biSizeImage", wt.DWORD), ("biXPelsPerMeter", wt.LONG),
                ("biYPelsPerMeter", wt.LONG), ("biClrUsed", wt.DWORD), ("biClrImportant", wt.DWORD)]
def grab(x,y,w,h):
    hdc=u.GetDC(0); mem=gdi32.CreateCompatibleDC(hdc); bmp=gdi32.CreateCompatibleBitmap(hdc,w,h)
    gdi32.SelectObject(mem,bmp); gdi32.BitBlt(mem,0,0,w,h,hdc,x,y,0x00CC0020)
    bi=BIH(biSize=ctypes.sizeof(BIH),biWidth=w,biHeight=-h,biPlanes=1,biBitCount=32)
    buf=ctypes.create_string_buffer(w*h*4)
    gdi32.GetDIBits(mem,bmp,0,h,buf,ctypes.byref(bi),0)
    gdi32.DeleteObject(bmp);gdi32.DeleteDC(mem);u.ReleaseDC(0,hdc)
    return bytes(buf)

# 用 .NET 存 PNG（系统无 PIL）
import clr_placeholder
X,Y,W,H=(2560-900)//2,0,900,470
n=int(sys.argv[1]); outdir=sys.argv[2]; os.makedirs(outdir, exist_ok=True)
wheel(n); t0=time.perf_counter()
for i in range(24):
    time.sleep(0.020)
    b=grab(X,Y,W,H)
    img=Image.frombuffer("RGBA",(W,H),b,"raw","BGRA",0,1)
    img.convert("RGB").save(os.path.join(outdir,f"s{i:02d}.png"))
    print(f"s{i:02d} @ {(time.perf_counter()-t0)*1000:.0f}ms")
