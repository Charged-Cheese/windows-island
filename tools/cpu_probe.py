"""精确测单次切页的岛体 CPU 开销：采样窗口 = 触发→900ms，扣掉同时长静止基线。"""
import ctypes, time, sys, os, subprocess
from ctypes import wintypes as wt
k32 = ctypes.windll.kernel32
PID = 22236
PID = 22236
class FILETIME(ctypes.Structure):
    _fields_ = [("lo", wt.DWORD), ("hi", wt.DWORD)]
def ft2s(f): return (f.hi << 32 | f.lo) / 1e7
def cpu_now():
    h = k32.OpenProcess(0x1000, False, PID)
    c,e,k,u = FILETIME(),FILETIME(),FILETIME(),FILETIME()
    k32.GetProcessTimes(h, ctypes.byref(c), ctypes.byref(e), ctypes.byref(k), ctypes.byref(u))
    k32.CloseHandle(h)
    return ft2s(k)+ft2s(u)
def send(tab):
    subprocess.run([sys.executable, os.path.join(os.path.dirname(__file__),"island_cmd.py"), str(PID), f"tab:{tab}"],
                   capture_output=True)
WIN = 0.90
# 基线：静止 0.9s
send(0); time.sleep(1.5)
b = cpu_now(); time.sleep(WIN); idle = cpu_now()-b
print(f"静止 {WIN*1000:.0f}ms 基线 CPU：{idle*1000:.0f}ms（{idle/WIN*100:.1f}%）")
# 三次单次切页
res=[]
for tgt in (1,2,3):
    send(tgt-1); time.sleep(1.5)
    b = cpu_now(); t0 = time.perf_counter()
    send(tgt)
    rem = WIN - (time.perf_counter()-t0)
    if rem > 0: time.sleep(rem)
    res.append(cpu_now()-b)
    print(f"  切换到 tab{tgt}：窗口内 CPU {res[-1]*1000:.0f}ms，净增 {(res[-1]-idle)*1000:.0f}ms")
import statistics
print(f"\n单次切页净增 CPU ≈ {statistics.median(res)*1000 - idle*1000:.0f}ms（240ms 动画）")
