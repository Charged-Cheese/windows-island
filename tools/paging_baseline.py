"""Repeatable paging baseline using the opt-in DI_PAGING_LOG probe (Windows, stdlib only).

Synthetic inputs call the real HandleWheel via diagnostic IPC; they do not measure
mouse hardware latency or compositor presentation. No cursor movement/screenshots.
"""
import argparse
import csv
import ctypes
from ctypes import wintypes as wt
import hashlib
import json
import os
from pathlib import Path
import platform
import statistics
import subprocess
import time


class CopyData(ctypes.Structure):
    _fields_ = [("tag", ctypes.c_size_t), ("size", wt.DWORD), ("data", ctypes.c_void_p)]


class FileTime(ctypes.Structure):
    _fields_ = [("low", wt.DWORD), ("high", wt.DWORD)]


def win32():
    user = ctypes.WinDLL("user32", use_last_error=True)
    kernel = ctypes.WinDLL("kernel32", use_last_error=True)
    callback = ctypes.WINFUNCTYPE(wt.BOOL, wt.HWND, wt.LPARAM)
    user.EnumWindows.argtypes = [callback, wt.LPARAM]
    user.EnumWindows.restype = wt.BOOL
    user.GetWindowThreadProcessId.argtypes = [wt.HWND, ctypes.POINTER(wt.DWORD)]
    user.IsWindowVisible.argtypes = [wt.HWND]
    user.GetWindowTextLengthW.argtypes = [wt.HWND]
    user.SendMessageTimeoutW.argtypes = [wt.HWND, wt.UINT, wt.WPARAM, ctypes.c_void_p,
                                       wt.UINT, wt.UINT, ctypes.POINTER(ctypes.c_size_t)]
    user.SendMessageTimeoutW.restype = wt.LPARAM
    kernel.OpenProcess.argtypes = [wt.DWORD, wt.BOOL, wt.DWORD]
    kernel.OpenProcess.restype = wt.HANDLE
    kernel.GetProcessTimes.argtypes = [wt.HANDLE] + [ctypes.POINTER(FileTime)] * 4
    kernel.GetProcessTimes.restype = wt.BOOL
    kernel.CloseHandle.argtypes = [wt.HANDLE]
    return user, kernel, callback


def find_window(user, callback, pid):
    found = []

    @callback
    def visit(hwnd, _):
        owner = wt.DWORD()
        user.GetWindowThreadProcessId(hwnd, ctypes.byref(owner))
        if owner.value == pid and user.IsWindowVisible(hwnd) and user.GetWindowTextLengthW(hwnd):
            found.append(hwnd)
        return True

    user.EnumWindows(visit, 0)
    return found[0] if found else None


def send(user, hwnd, command):
    buf = ctypes.create_unicode_buffer(command)
    data = CopyData(0, len(command.encode("utf-16-le")), ctypes.cast(buf, ctypes.c_void_p))
    result = ctypes.c_size_t()
    if not user.SendMessageTimeoutW(hwnd, 0x004A, 0, ctypes.byref(data), 0x0002, 3000,
                                    ctypes.byref(result)) or result.value != 1:
        raise RuntimeError(f"IPC failed: {command}, Win32 error={ctypes.get_last_error()}")


def cpu_ms(kernel, handle):
    created, exited, system, user = (FileTime() for _ in range(4))
    if not kernel.GetProcessTimes(handle, ctypes.byref(created), ctypes.byref(exited),
                                  ctypes.byref(system), ctypes.byref(user)):
        raise ctypes.WinError(ctypes.get_last_error())
    return sum((v.high << 32) | v.low for v in (system, user)) / 10000


def wait_until(deadline):
    time.sleep(max(0, deadline - time.perf_counter()))


CASES = {
    "one": (1, [-120], 0),
    "three": (1, [-120, -120, -120], 0.0465),
    "reverse": (2, [-120, 120], 0.0465),
    "first_edge": (0, [120], 0),
    "last_edge": (5, [-120], 0),
    "remainder": (1, [-80, -80, -80], 0.0465),
    "multi_delta": (1, [-360], 0),
    "dynamic_calendar": (4, [-120], 0),
    "partial_reverse": (2, [100, -120], 0.0465),
    "partial_remainder": (1, [-80, -80], 0.0465),
    "state_reset": (2, [80, "collapse", "expand", 40], 0.0465),
    "pending_reset": (1, [-120, -120, "collapse", "expand", 0], 0.0465),
    "near_edge_multi": (4, [-360], 0),
    "zero_delta": (1, [0], 0),
}

# Behavioral expectations for the input-normalization stage, not timing assertions.
INPUT_EXPECTATIONS = {
    "one": (2, 0, 1), "three": (4, 0, 3), "reverse": (2, 0, 2),
    "first_edge": (0, 0, 0), "last_edge": (5, 0, 0),
    "remainder": (3, 0, 2), "multi_delta": (4, 0, 3),
    "dynamic_calendar": (5, 0, 1), "partial_reverse": (3, 0, 1),
    "partial_remainder": (2, -40, 1), "state_reset": (2, 40, 0),
    "pending_reset": (2, 0, 1), "near_edge_multi": (5, 0, 1), "zero_delta": (1, 0, 0),
}

GLIDE_CASES = {
    "five_ticks": (0, [-120] * 5, 0.02325),
    "clamped_burst": (0, [-12000], 0),
    "rapid_reverse": (2, [-360, 360], 0.0465),
    "click_then_wheel": (1, ["tab:5", -120], 0.0465),
    "wheel_then_click": (1, [-360, "tab:0"], 0.0465),
    "click_only": (0, ["tab:5", 0], 0),
    "click_edge_takeover": (1, ["tab:5", -12000], 0.0465),
}
ALL_CASES = {**CASES, **GLIDE_CASES}
GLIDE_DESTINATIONS = {name: value[:2] for name, value in INPUT_EXPECTATIONS.items()}
GLIDE_DESTINATIONS.update({
    # Both wheel targets are accepted immediately; changing state does not replay queued steps.
    "pending_reset": (3, 0), "five_ticks": (5, 0), "clamped_burst": (5, 0),
    "rapid_reverse": (2, 0), "click_then_wheel": (3, 0), "wheel_then_click": (0, 0),
    "click_only": (5, 0), "click_edge_takeover": (5, 0),
})


def summarize(trace, measurements):
    rows = list(csv.DictReader(trace.open(encoding="utf-8-sig", newline="")))
    summaries = []
    for measurement in measurements:
        tag = measurement["tag"]
        begin = next(float(r["time_ms"]) for r in rows if r["event"] == "mark_begin_" + tag)
        end = next(float(r["time_ms"]) for r in rows if r["event"] == "mark_end_" + tag)
        sample = [r for r in rows if begin <= float(r["time_ms"]) <= end]
        wheels = [r for r in sample if r["event"] == "wheel"]
        if len(wheels) != sum(isinstance(action, int) for action in ALL_CASES[measurement["case"]][1]):
            raise RuntimeError(f"Missing input events: {tag}")
        frames = [r for r in sample if r["event"] == "frame"]
        if not frames:
            raise RuntimeError(f"No rendering samples: {tag}")
        last_input = float(wheels[-1]["time_ms"])
        final_position = float(frames[-1]["position"])
        # Persistent convergence: the LAST deviation, not the first crossing of the target.
        off_target = [r for r in frames if abs(float(r["position"]) - final_position) * 34 > 0.5]
        last_motion = float(off_target[-1]["time_ms"]) if off_target else last_input
        finishes = [float(r["time_ms"]) for r in sample if r["event"] in ("step_finish", "wheel_finish")]
        unsettled = [float(r["time_ms"]) for r in frames if r["size_settled"] == "False"]
        intervals = [float(b["time_ms"]) - float(a["time_ms"]) for a, b in zip(frames, frames[1:])]
        summaries.append({
            **measurement,
            "input_span_ms": round(last_input - float(wheels[0]["time_ms"]), 2),
            "page_tail_0_5dip_ms": round(max(0, last_motion - last_input), 2),
            "logical_tail_ms": round(max(0, max(finishes, default=last_input) - last_input), 2),
            "size_tail_ms": round(max(0, max(unsettled, default=last_input) - last_input), 2),
            "final_page_position": final_position,
            "final_remainder": int(frames[-1]["remainder"]),
            "initial_remainder": int(wheels[0]["remainder"]),
            "final_pending": int(frames[-1]["pending"]),
            "final_queued": int(frames[-1]["queued"]),
            "final_active_tab": int(frames[-1]["active_tab"]),
            "step_count": sum(r["event"] == "step_start" for r in sample),
            "finish_count": sum(r["event"] == "step_finish" for r in sample),
            "step_cancel_count": sum(r["event"] == "step_cancel" for r in sample),
            "wheel_start_count": sum(r["event"] == "wheel_start" for r in sample),
            "wheel_finish_count": sum(r["event"] == "wheel_finish" for r in sample),
            "wheel_cancel_count": sum(r["event"] == "wheel_cancel" for r in sample),
            "wheel_retarget_count": sum(r["event"] == "wheel_retarget" for r in sample),
            "handoffs": [{"event": r["event"], "position": float(r["position"]),
                          "velocity": float(r.get("velocity", 0)), "wheel": r.get("wheel_gliding") == "True"}
                         for r in sample if r["event"] in ("handoff_before", "handoff_after", "retarget_before", "wheel_retarget")],
            "position_in_bounds": all(0 <= float(r["position"]) <= 5 for r in sample),
            "truncated": frames[-1]["animating"] == "True" or frames[-1]["size_settled"] == "False",
            "max_callback_interval_ms": round(max(intervals, default=0), 2),
            "callback_intervals_over_33ms": sum(v > 33 for v in intervals),
        })
    return summaries


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--exe", type=Path, required=True)
    parser.add_argument("--out", type=Path, required=True)
    parser.add_argument("--repeats", type=int, default=3, choices=range(1, 11))
    parser.add_argument("--check-input", action="store_true", help="Assert stage-2 input behavior")
    parser.add_argument("--check-glide", action="store_true", help="Assert stage-3 target tracking and handoff behavior")
    parser.add_argument("--cases", nargs="+", choices=ALL_CASES, help="Run only selected scenarios")
    args = parser.parse_args()
    if args.check_input and args.check_glide:
        parser.error("Choose --check-input or --check-glide; the scheduling semantics differ")
    cases = ALL_CASES if args.check_glide else CASES
    if args.cases:
        cases = {name: ALL_CASES[name] for name in args.cases}
    exe, out = args.exe.resolve(), args.out.resolve()
    if not exe.is_file():
        parser.error(f"Executable does not exist: {exe}")
    # A fresh directory keeps earlier baseline artifacts intact.
    out.mkdir(parents=True, exist_ok=False)
    data_dir = out / "app-data"
    data_dir.mkdir()
    settings = {key: False for key in (
        "EnableWeather", "EnableNotifications", "EnableLyrics", "EnableHardware", "EnablePrivacyDots",
        "EnableFullscreenHide", "EnableSystemAlerts", "EnableBluetooth", "EnableClipboard", "ShowNotificationCard")}
    settings["AnimationStyle"] = "default"
    (data_dir / "settings.json").write_text(json.dumps(settings), encoding="utf-8")
    trace = out / "paging.csv"
    env = dict(os.environ, DI_PAGING_LOG=str(trace), DYNAMICISLAND_DATA_DIR=str(data_dir))
    env.pop("DI_FRAME_LOG", None)  # Its periodic UI-thread disk writes would perturb measurement.
    user, kernel, callback = win32()
    process = subprocess.Popen([str(exe)], env=env, cwd=exe.parent)
    handle, hwnd = None, None
    measurements = []
    try:
        for _ in range(100):
            if process.poll() is not None:
                raise RuntimeError("Test app exited; close an existing DynamicIsland instance first.")
            hwnd = find_window(user, callback, process.pid)
            if hwnd and trace.exists():
                break
            time.sleep(0.1)
        else:
            raise RuntimeError("App did not become ready within 10 seconds")
        handle = kernel.OpenProcess(0x1000, False, process.pid)
        if not handle:
            raise ctypes.WinError(ctypes.get_last_error())
        time.sleep(2)
        for repeat in range(args.repeats):
            for name, (page, actions, gap) in cases.items():
                tag = f"{name}_{repeat}"
                # Begin each case in a fresh expanded input state; retain app/services for timing.
                send(user, hwnd, "collapse")
                send(user, hwnd, f"tab:{page}")
                # Explicit page requests may traverse all five pages.
                time.sleep(1.8)
                send(user, hwnd, "probe:flush")
                idle_start = time.perf_counter()
                idle_cpu = cpu_ms(kernel, handle)
                wait_until(idle_start + 0.240)
                idle_240 = cpu_ms(kernel, handle) - idle_cpu
                wait_until(idle_start + 0.900)
                idle_900 = cpu_ms(kernel, handle) - idle_cpu
                send(user, hwnd, f"probe:mark:begin_{tag}")
                start = time.perf_counter()
                start_cpu = cpu_ms(kernel, handle)
                for index, action in enumerate(actions):
                    wait_until(start + index * gap)
                    command = f"probe:wheel:{action}" if isinstance(action, int) else action
                    send(user, hwnd, command)
                wait_until(start + 0.240)
                cpu_240 = cpu_ms(kernel, handle) - start_cpu
                elapsed_240 = (time.perf_counter() - start) * 1000
                wait_until(start + 0.900)
                cpu_900 = cpu_ms(kernel, handle) - start_cpu
                elapsed_900 = (time.perf_counter() - start) * 1000
                # A five-page CLICK deliberately takes 5*240ms, then its independent size spring settles.
                observation_seconds = 2.2 if name == "click_only" else 1.4
                wait_until(start + observation_seconds)
                send(user, hwnd, f"probe:mark:end_{tag}")
                send(user, hwnd, "probe:flush")
                measurements.append({"tag": tag, "case": name,
                    "cpu_240_ms": round(cpu_240, 3), "cpu_900_ms": round(cpu_900, 3),
                    "idle_cpu_240_ms": round(idle_240, 3), "idle_cpu_900_ms": round(idle_900, 3),
                    "net_cpu_240_ms": round(cpu_240 - idle_240, 3),
                    "net_cpu_900_ms": round(cpu_900 - idle_900, 3),
                    "actual_240_window_ms": round(elapsed_240, 3),
                    "actual_900_window_ms": round(elapsed_900, 3),
                    "observation_window_ms": observation_seconds * 1000})
                print(f"completed {tag}", flush=True)
    finally:
        if handle:
            kernel.CloseHandle(handle)
        if hwnd and process.poll() is None:
            send(user, hwnd, "quit")
        try:
            process.wait(timeout=5)
        except subprocess.TimeoutExpired:
            process.terminate()
            process.wait(timeout=5)
    summary = summarize(trace, measurements)
    assembly = exe.with_suffix(".dll")
    binary = assembly if assembly.is_file() else exe
    report = {"method": "synthetic diagnostic IPC; callback samples, not presented pixels",
              "platform": platform.platform(), "pid": process.pid,
              "assembly_sha256": hashlib.sha256(assembly.read_bytes()).hexdigest() if assembly.is_file() else None,
              "measured_binary": binary.name, "binary_sha256": hashlib.sha256(binary.read_bytes()).hexdigest(),
              "settings": settings, "repeats": args.repeats, "cases": summary,
              "limits": [
                  "Page tail uses 0.5 DIP of the 34-DIP translation, excluding opacity/scale/shadow.",
                  "Rendering callbacks are not compositor presentation timestamps.",
                  "CPU is process-wide, includes probe overhead/background tasks, and may be noisy/negative after baseline subtraction.",
                  "Frames before input may contain leftover wheel remainder; inspect initial_remainder.",
                  "Empty test data does not represent the cost of a populated history list."]}
    (out / "summary.json").write_text(json.dumps(report, indent=2), encoding="utf-8")
    for name in cases:
        results = [r for r in summary if r["case"] == name]
        print(f"{name}: page tail={statistics.median(r['page_tail_0_5dip_ms'] for r in results):.1f}ms, "
              f"logical tail={statistics.median(r['logical_tail_ms'] for r in results):.1f}ms, "
              f"net CPU[240/900]={statistics.median(r['net_cpu_240_ms'] for r in results):.1f}/"
              f"{statistics.median(r['net_cpu_900_ms'] for r in results):.1f}ms")
    if any(r["truncated"] for r in summary):
        raise RuntimeError("Some transitions did not settle within the observation window; inspect summary.json")
    if args.check_input:
        for result in summary:
            expected = INPUT_EXPECTATIONS[result["case"]]
            actual = (result["final_page_position"], result["final_remainder"], result["step_count"])
            if actual != expected:
                raise AssertionError(f"{result['tag']}: expected position/remainder/steps={expected}, got {actual}")
            if result["initial_remainder"] or result["final_pending"] or result["final_queued"]:
                raise AssertionError(f"{result['tag']}: input/queue leaked between cases")
            if result["finish_count"] != result["step_count"]:
                raise AssertionError(f"{result['tag']}: unexpected repeated completion callbacks")
        print(f"Input assertions passed: {len(summary)} cases", flush=True)
    if args.check_glide:
        validate_glide_results(summary)
        print(f"Glide assertions passed: {len(summary)} cases", flush=True)


def validate_glide_results(summary):
    for result in summary:
        tag, name = result["tag"], result["case"]
        if result["truncated"]:
            raise AssertionError(f"{tag}: observation ended before settling")
        actual = (result["final_page_position"], result["final_remainder"])
        if actual != GLIDE_DESTINATIONS[name] or result["final_active_tab"] != actual[0]:
            raise AssertionError(f"{tag}: wrong final state: {actual}, active={result['final_active_tab']}")
        if result["initial_remainder"] or result["final_pending"] or result["final_queued"] or not result["position_in_bounds"]:
            raise AssertionError(f"{tag}: leaked input/queue or position out of bounds")
        if result["step_count"] != result["finish_count"] + result["step_cancel_count"]:
            raise AssertionError(f"{tag}: click start/finish/cancel mismatch")
        if result["wheel_start_count"] != result["wheel_finish_count"] + result["wheel_cancel_count"]:
            raise AssertionError(f"{tag}: wheel start/finish/cancel mismatch")
        handoffs = result["handoffs"]
        if len(handoffs) % 2:
            raise AssertionError(f"{tag}: unpaired handoff")
        for before, after in zip(handoffs[::2], handoffs[1::2]):
            if abs(before["position"] - after["position"]) > 0.00001:
                raise AssertionError(f"{tag}: position jumped during handoff")
            if after["wheel"] and abs(before["velocity"] - after["velocity"]) > 0.00001:
                raise AssertionError(f"{tag}: wheel takeover/retarget lost velocity")
        if name in ("three", "multi_delta", "five_ticks", "clamped_burst"):
            if result["step_count"] or result["wheel_start_count"] != 1 or result["logical_tail_ms"] >= 500:
                raise AssertionError(f"{tag}: multi-tick input still serialized or too slow")
        if name == "click_only" and (result["step_count"] != 5 or result["wheel_start_count"]):
            raise AssertionError(f"{tag}: explicit cross-page click no longer traverses all pages")
        if name in ("click_then_wheel", "wheel_then_click", "click_edge_takeover") and len(handoffs) != 2:
            raise AssertionError(f"{tag}: input paths did not hand off")


if __name__ == "__main__":
    main()
