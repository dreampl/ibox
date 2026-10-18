"""Alita-style demo: register once (approve on the notch), then a notification, a chip and a dashboard."""
import os
import sys

from ibar import Ibar

MANIFEST = {
    "schema": 1,
    "id": "alita",
    "name": "Alita",
    "publisher": "Alita team",
    "actions": [
        {"id": "open", "title": "Open", "command": r"C:\Windows\notepad.exe", "args": []},
        {"id": "docs", "title": "Docs", "url": "https://example.com/alita"},
    ],
}

token_file = os.path.join(os.path.dirname(__file__), ".ibar-token")
bar = Ibar("alita", token=open(token_file).read().strip() if os.path.exists(token_file) else None)
token = bar.register(MANIFEST)
if not token:
    sys.exit("Waiting for approval on the notch; run again.")
open(token_file, "w").write(token)

bar.chip("#22C55E", "3")
bar.panel(
    [
        {"kind": "number", "label": "Tasks", "value": "12"},
        {"kind": "bar", "label": "CPU", "fraction": 0.42},
        {"kind": "spark", "label": "Load", "values": [3, 5, 4, 8, 6, 9, 7]},
    ],
    buttons=["open", "docs"],
)
# A task on the notch's activity circle: first an unknown amount (the ring turns), then exact progress, then done.
import time

task = bar.start("summary-1", "Summarising 3 files", kind="task", detail="reading")
time.sleep(1.5)
for i in range(1, 4):
    task.update(progress=i / 3, accuracy="exact", detail=f"file {i} of 3")
    time.sleep(1)
task.end()
bar.sound("done")
bar.notify("Alita finished a task", "3 files summarised in your work folder.", link="docs")
print("ok")