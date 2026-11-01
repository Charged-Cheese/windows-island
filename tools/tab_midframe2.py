"""密集抓帧（只存内存，事后写盘）：验证切页第 0 帧是否硬跳。
GDI 单帧 ~8ms，可在 240ms 内拿到约 25 帧。"""
import ctypes, time, sys, os, subprocess
sys.path.insert(0, os.path.dirname(__file__))
from wheel_frames import grab, write_png

W, H = 900, 705
X, Y = (2560 - W) // 2, 0
ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))

def run(prep, cmd, dur=0.40):
    subprocess.run([sys.executable, os.path.join(os.path.dirname(__file__), "island_cmd.py"), "22236", f"tab:{prep}"],
                   capture_output=True)
    time.sleep(1.5)
    grab(X, Y, W, H)          # 预热
    t0 = time.perf_counter()
    p = subprocess.Popen([sys.executable, os.path.join(os.path.dirname(__file__), "island_cmd.py"), "22236", cmd],
                         stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    frames = []
    while True:
        t = (time.perf_counter() - t0) * 1000
        buf = grab(X, Y, W, H)
        frames.append(((time.perf_counter() - t0) * 1000, buf))
        if t > dur * 1000:
            break
    p.wait()
    return frames

def diff(a, b):
    n = 0; step = 12
    for i in range(0, len(a), step):
        if abs(a[i] - b[i]) > 8: n += 1
    return n / (len(a) / step)

frames = run(0, "tab:1")
out = os.path.join(ROOT, "artifacts", "tabmid2")
os.makedirs(out, exist_ok=True)
print(f"共 {len(frames)} 帧，间隔 {frames[-1][0]/len(frames):.1f}ms 平均")
print(" idx   t_ms   与上一帧diff   与首帧diff")
first = frames[0][1]
prev = frames[0][1]
for i, (t, buf) in enumerate(frames):
    d1 = diff(prev, buf); d0 = diff(first, buf)
    print(f" {i:3d}  {t:6.1f}   {d1:10.3f}   {d0:10.3f}")
    prev = buf
    write_png(os.path.join(out, f"f{i:02d}_{t:05.0f}ms.png"), buf, W, H)
print("saved ->", out)
