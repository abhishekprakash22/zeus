#!/usr/bin/env python3
# SPDX-License-Identifier: GPL-2.0-or-later
#
# ANAN Core launch splash. Shown by AppRun the moment the AppImage starts —
# at boot, and again after an update relaunch — so the operator is never
# looking at an empty desktop wondering whether anything is happening.
#
# Fullscreen, dark backdrop, panel centred inside it: centred at any
# resolution by construction. Two attempts at placing a small window failed
# on Raspberry Pi OS (Wayland/labwc) — override-redirect landed at 0,0, a
# normal toplevel was cascaded to the left edge — because the compositor does
# the placing and does not centre. So the splash stops asking.
#
# Two jobs, and it blocks the launch for neither:
#   1. Stay up until the CONSOLE is up — a LOCAL display subscriber on the
#      streaming hub, i.e. the kiosk has loaded and asked for the panadapter.
#      Not merely the server answering (several seconds earlier), and not a
#      remote session re-subscribing (which happens the instant the server
#      is up and used to dismiss the splash after one second).
#   2. If the app process dies, or the timeout passes with no console, SAY
#      SO rather than sit there — a splash that hides a failed start is worse
#      than no splash.
#
# Pure standard library (tkinter + urllib + json). AppRun checks for tkinter
# and a display first and launches without a splash if either is absent.

import json
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
STATE_URL = f"http://127.0.0.1:{PORT}/api/state"
HUB_URL = f"http://127.0.0.1:{PORT}/api/diagnostics/streaming"
TIMEOUT_S = int(os.environ.get("ZEUS_SPLASH_TIMEOUT_S", "120"))
PARENT = os.getppid()
TITLE = os.environ.get("ZEUS_SPLASH_TITLE", "ANAN Core")
LOGS = os.path.expanduser("~/.local/share/Zeus/logs")


def server_up() -> bool:
    try:
        with urllib.request.urlopen(STATE_URL, timeout=1.0) as r:
            return 200 <= r.status < 300
    except Exception:
        return False


def console_up() -> bool:
    try:
        with urllib.request.urlopen(HUB_URL, timeout=1.0) as r:
            if not (200 <= r.status < 300):
                return False
            d = json.loads(r.read().decode("utf-8", "replace"))
            if "localDisplaySubscribers" in d:
                return int(d.get("localDisplaySubscribers", 0)) >= 1
            return int(d.get("displaySubscribers", 0)) >= 1   # older server
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
root.configure(bg="#0b0e13")
try:
    root.attributes("-fullscreen", True)
except Exception:
    root.update_idletasks()
    root.geometry(f"{root.winfo_screenwidth()}x{root.winfo_screenheight()}+0+0")
try:
    root.attributes("-topmost", True)
except Exception:
    pass

backdrop = tk.Frame(root, bg="#0b0e13")
backdrop.pack(fill="both", expand=True)
frame = tk.Frame(backdrop, bg="#0f1319", highlightthickness=1, highlightbackground="#2a3140")
frame.place(relx=0.5, rely=0.5, anchor="center", width=500, height=200)

tk.Label(frame, text=TITLE, fg="#e8ecf1", bg="#0f1319",
         font=("DejaVu Sans", 22, "bold")).pack(pady=(26, 2))
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
bar = ttk.Progressbar(frame, mode="indeterminate", length=400, style="Z.Horizontal.TProgressbar")
bar.pack(pady=(16, 6))
bar.start(12)

elapsed = tk.Label(frame, text="", fg="#5f6b7a", bg="#0f1319", font=("DejaVu Sans", 8))
elapsed.pack()

t0 = time.monotonic()
failed = False
server_seen = False


def fail(msg: str) -> None:
    global failed
    failed = True
    bar.stop()
    bar.pack_forget()
    status.configure(text=msg, fg="#ff8a80", wraplength=460, justify="center")
    elapsed.configure(text=f"Logs: {LOGS}")
    btn = tk.Button(frame, text="Close", command=root.destroy, bg="#1a2030", fg="#e8ecf1",
                    activebackground="#2a3140", activeforeground="#e8ecf1", relief="flat",
                    padx=14, pady=4)
    btn.pack(pady=(8, 0))
    root.title(f"{TITLE} — did not start")


def tick() -> None:
    global server_seen
    if failed:
        return
    dt = int(time.monotonic() - t0)
    elapsed.configure(text=f"{dt} s")
    if not server_seen and server_up():
        server_seen = True
        status.configure(text="Radio software is up — opening the console…")
    if server_seen and console_up():
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
    if dt >= 25 and not server_seen:
        status.configure(text="Still starting — the first launch after an update takes longer.")
    root.after(500, tick)


root.after(500, tick)
try:
    root.mainloop()
except Exception:
    pass
