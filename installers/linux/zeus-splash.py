#!/usr/bin/env python3
# SPDX-License-Identifier: GPL-2.0-or-later
#
# ANAN Core launch splash. Shown by AppRun the moment the AppImage starts —
# at boot, and again after an update relaunch — so the operator is never
# looking at an empty desktop wondering whether anything is happening.
#
# It has exactly two jobs, and it does not block the launch for either:
#   1. Close itself the moment the server answers on :6060.
#   2. If the server has not answered after a generous timeout, or the app
#      process has died, SAY SO rather than sit there — a splash that hides
#      a failed start is worse than no splash.
#
# Pure standard library (tkinter + urllib). If tkinter is missing, AppRun
# never gets this far: it checks first and launches without a splash.

import os
import sys
import time
import urllib.request

try:
    import tkinter as tk
    from tkinter import ttk
except Exception:
    sys.exit(0)

PORT = int(os.environ.get("ZEUS_SPLASH_PORT", "6060"))
URL = f"http://127.0.0.1:{PORT}/api/state"
TIMEOUT_S = int(os.environ.get("ZEUS_SPLASH_TIMEOUT_S", "90"))
PARENT = os.getppid()
TITLE = os.environ.get("ZEUS_SPLASH_TITLE", "ANAN Core")
LOGS = os.path.expanduser("~/.local/share/Zeus/logs")


def server_up() -> bool:
    try:
        with urllib.request.urlopen(URL, timeout=1.0) as r:
            return 200 <= r.status < 300
    except Exception:
        return False


def parent_alive() -> bool:
    try:
        os.kill(PARENT, 0)
        return True
    except Exception:
        return False


root = tk.Tk()
root.title(TITLE)
root.overrideredirect(True)          # no chrome — it is a notice, not a window
root.configure(bg="#0f1319")
root.attributes("-topmost", True)

W, H = 460, 170
sw, sh = root.winfo_screenwidth(), root.winfo_screenheight()
root.geometry(f"{W}x{H}+{(sw - W) // 2}+{(sh - H) // 2}")

frame = tk.Frame(root, bg="#0f1319", highlightthickness=1, highlightbackground="#2a3140")
frame.pack(fill="both", expand=True)

tk.Label(frame, text=TITLE, fg="#e8ecf1", bg="#0f1319",
         font=("DejaVu Sans", 20, "bold")).pack(pady=(22, 2))
status = tk.Label(frame, text="Starting the radio software…", fg="#9aa4b1", bg="#0f1319",
                  font=("DejaVu Sans", 10))
status.pack()

style = ttk.Style(root)
try:
    style.theme_use("clam")
except Exception:
    pass
style.configure("Z.Horizontal.TProgressbar", troughcolor="#1a2030", background="#f0b33a",
                bordercolor="#1a2030", lightcolor="#f0b33a", darkcolor="#f0b33a")
bar = ttk.Progressbar(frame, mode="indeterminate", length=380, style="Z.Horizontal.TProgressbar")
bar.pack(pady=(14, 6))
bar.start(12)

elapsed = tk.Label(frame, text="", fg="#5f6b7a", bg="#0f1319", font=("DejaVu Sans", 8))
elapsed.pack()

t0 = time.monotonic()
failed = False


def fail(msg: str) -> None:
    global failed
    failed = True
    bar.stop()
    bar.pack_forget()
    status.configure(text=msg, fg="#ff8a80", wraplength=420, justify="center")
    elapsed.configure(text=f"Logs: {LOGS}")
    btn = tk.Button(frame, text="Close", command=root.destroy, bg="#1a2030", fg="#e8ecf1",
                    activebackground="#2a3140", activeforeground="#e8ecf1", relief="flat",
                    padx=14, pady=4)
    btn.pack(pady=(8, 0))
    root.overrideredirect(False)     # give it a title bar so it can be moved/closed normally
    root.title(f"{TITLE} — did not start")


def tick() -> None:
    if failed:
        return
    dt = int(time.monotonic() - t0)
    elapsed.configure(text=f"{dt} s")
    if server_up():
        root.destroy()
        return
    if not parent_alive():
        fail("ANAN Core exited before the radio came up.\n"
             "Try starting it again; if it keeps happening, power-cycle the radio.")
        return
    if dt >= TIMEOUT_S:
        fail(f"ANAN Core has not responded after {TIMEOUT_S} seconds.\n"
             "It may still be starting, or it may have stalled.")
        return
    if dt >= 25:
        status.configure(text="Still starting — the first launch after an update takes longer.")
    root.after(500, tick)


root.after(500, tick)
try:
    root.mainloop()
except Exception:
    pass
