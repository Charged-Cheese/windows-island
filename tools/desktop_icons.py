"""读取桌面（SysListView32）图标的名称与屏幕坐标。

跨进程 SendMessage 时指针必须位于目标进程地址空间，因此用 VirtualAllocEx +
ReadProcessMemory 完成。仅用于自测定位拖拽起点。
"""
import ctypes
from ctypes import wintypes as wt

u = ctypes.windll.user32
k = ctypes.windll.kernel32
u.SetProcessDPIAware()
u.SendMessageW.restype = ctypes.c_void_p
u.FindWindowW.restype = ctypes.c_void_p
u.FindWindowExW.restype = ctypes.c_void_p
k.OpenProcess.restype = ctypes.c_void_p
k.VirtualAllocEx.restype = ctypes.c_void_p
k.VirtualAllocEx.argtypes = [ctypes.c_void_p, ctypes.c_void_p, ctypes.c_size_t, wt.DWORD, wt.DWORD]
k.ReadProcessMemory.argtypes = [ctypes.c_void_p, ctypes.c_void_p, ctypes.c_void_p, ctypes.c_size_t, ctypes.c_void_p]

LVM_GETITEMCOUNT = 0x1004
LVM_GETITEMPOSITION = 0x1010
LVM_GETITEMTEXTW = 0x1073

LVIF_TEXT = 0x0001


class LVITEMW(ctypes.Structure):
    _fields_ = [
        ("mask", wt.UINT),
        ("iItem", ctypes.c_int),
        ("iSubItem", ctypes.c_int),
        ("state", wt.UINT),
        ("stateMask", wt.UINT),
        ("pszText", ctypes.c_void_p),
        ("cchTextMax", ctypes.c_int),
        ("iImage", ctypes.c_int),
        ("lParam", ctypes.c_void_p),
        ("iIndent", ctypes.c_int),
        ("iGroupId", ctypes.c_int),
        ("cColumns", wt.UINT),
        ("puColumns", ctypes.c_void_p),
        ("piColFmt", ctypes.c_void_p),
        ("iGroup", ctypes.c_int),
    ]


def desktop_listview():
    progman = u.FindWindowW("Progman", None)
    defview = u.FindWindowExW(progman, None, "SHELLDLL_DefView", None)
    return u.FindWindowExW(defview, None, "SysListView32", None) if defview else None


def read_icons():
    lv = desktop_listview()
    if not lv:
        return []

    pid = wt.DWORD()
    u.GetWindowThreadProcessId(lv, ctypes.byref(pid))

    PROCESS_VM_OPERATION = 0x0008
    PROCESS_VM_READ = 0x0010
    PROCESS_VM_WRITE = 0x0020
    PROCESS_QUERY_INFORMATION = 0x0400
    proc = k.OpenProcess(
        PROCESS_VM_OPERATION | PROCESS_VM_READ | PROCESS_VM_WRITE | PROCESS_QUERY_INFORMATION,
        False, pid.value)
    if not proc:
        return []

    MEM_COMMIT_RESERVE = 0x3000
    PAGE_READWRITE = 0x04
    remote = k.VirtualAllocEx(proc, None, 4096, MEM_COMMIT_RESERVE, PAGE_READWRITE)
    if not remote:
        return []

    count = ctypes.c_long(u.SendMessageW(lv, LVM_GETITEMCOUNT, 0, None)).value
    results = []
    text_remote = ctypes.c_void_p(remote + 128)

    for index in range(count):
        # 位置
        u.SendMessageW(lv, LVM_GETITEMPOSITION, index, remote)
        point = wt.POINT()
        k.ReadProcessMemory(proc, remote, ctypes.byref(point), ctypes.sizeof(point), None)

        # 名称
        item = LVITEMW(mask=LVIF_TEXT, iItem=index, iSubItem=0,
                       pszText=text_remote, cchTextMax=260)
        k.WriteProcessMemory(proc, remote, ctypes.byref(item), ctypes.sizeof(item), None)
        u.SendMessageW(lv, LVM_GETITEMTEXTW, index, remote)

        buf = ctypes.create_unicode_buffer(260)
        k.ReadProcessMemory(proc, text_remote, buf, ctypes.sizeof(buf), None)

        screen = wt.POINT(point.x, point.y)
        u.ClientToScreen(lv, ctypes.byref(screen))
        results.append((buf.value, screen.x, screen.y))

    k.VirtualFreeEx(proc, remote, 0, 0x8000)
    k.CloseHandle(proc)
    return results


if __name__ == "__main__":
    icons = read_icons()
    print(f"桌面图标 {len(icons)} 个：")
    for name, x, y in icons:
        if name.strip():
            print(f"  {name!r:45s} at ({x}, {y})")
