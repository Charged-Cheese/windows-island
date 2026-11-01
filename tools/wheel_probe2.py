"""测多格滚动的总时长：滚 3 格，记录页面何时彻底静止。"""
import ctypes, time, sys
from ctypes import wintypes as wt
u = ctypes.windll.user32
class MOUSEINPUT(ctypes.Structure):
    _fields_ = [("dx", wt.LONG), ("dy", wt.LONG), ("mouseData", wt.DWORD),
                ("dwFlags", wt.DWORD), ("time", wt.DWORD), ("dwExtraInfo", ctypes.POINTER(wt.ULONG))]
class _U(ctypes.Union):
    _fields_ = [("mi", MOUSEINPUT)]
class INPUT(ctypes.Structure):
    _fields_ = [("type", wt.DWORD), ("u", _U)]
gdi32 = ctypes.windll.gdi32
class BIH(ctypes.Structure):
    _fields_ = [("biSize", wt.DWORD), ("biWidth", wt.LONG), ("biHeight", wt.LONG),
                ("biPlanes", wt.WORD), ("biBitCount", wt.WORD), ("biCompression", wt.DWORD),
                ("biSizeImage", wt.DWORD), ("biXPelsPerMeter", wt.LONG),
                ("biYPelsPerMeter", wt.LONG), ("biClrUsed", wt.DWORD), ("biClrImportant", wt.DWORD)]
def mkwheel(delta):
    return INPUT(type=0, u=_U(mi=MOUSEINPUT(0,0,delta & 0xFFFFFFFF,0x0800,0,None)))
def grab(x,y,w,h):
    hdc=u.GetDC(0); mem=gdi32.CreateCompatibleDC(hdc); bmp=gdi32.CreateCompatibleBitmap(hdc,w,h)
    gdi32.SelectObject(mem,bmp); gdi32.BitBlt(mem,0,0,w,h,hdc,x,y,0x00CC0020)
    bi=BIH(biSize=ctypes.sizeof(BIH),biWidth=w,biHeight=-h,biPlanes=1,biBitCount=32)
    buf=ctypes.create_string_buffer(w*h*4)
    gdi32.GetDIBits(mem,bmp,0,h,buf,ctypes.byref(bi),0)
    gdi32.DeleteObject(bmp);gdi32.DeleteDC(mem);u.ReleaseDC(0,hdc)
    return bytes(buf)
def diff(a,b):
    s=0;n=0
    for i in range(0,len(a),4*97):
        s+=abs(a[i]-b[i]);n+=1
    return s/max(n,1)

W,H=900,470; X=(2560-W)//2; Y=0
n_steps=int(sys.argv[1]) if len(sys.argv)>1 else 3
prev=grab(X,Y,W,H); t0=time.perf_counter()
for _ in range(n_steps):
    u.SendInput(1, ctypes.byref(mkwheel(-120)), ctypes.sizeof(INPUT))
rows=[]
while True:
    time.sleep(0.014)
    cur=grab(X,Y,W,H); d=diff(prev,cur); prev=cur
    t=(time.perf_counter()-t0)*1000
    rows.append((t,d))
    if t>400 and d<0.25:
        # 连续 5 帧都静才算停
        stable=0
        for _ in range(5):
            time.sleep(0.014); cur=grab(X,Y,W,H); d=diff(prev,cur); prev=cur
            if d<0.25: stable+=1
        if stable>=4: break
    if t>4000:
        break
first=next((t for t,d in rows if d>0.6), None)
last=next((t for t,d in reversed(rows) if d>0.6), None)
print(f"滚 {n_steps} 格：首帧动={first and round(first,1)}ms  最后明显动={last and round(last,1)}ms  总时长≈{rows[-1][0]:.0f}ms")
print(f"  理论（每页240ms串行）={240*n_steps}ms，实测差 {rows[-1][0]-240*n_steps:+.0f}ms")
