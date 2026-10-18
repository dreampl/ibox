"""Thin IBAR client for plugins (Windows named pipe, one JSON object per line, schema 1).

    from ibar import Ibar
    bar = Ibar("alita")
    token = bar.register(manifest)          # the user approves once on the notch; keep the token
    bar.notify("Done", "3 files summarised", link="docs")
    bar.chip("#22C55E", "3")
    bar.panel([{"kind": "number", "label": "Tasks", "value": "12"}], buttons=["open"])

    task = bar.start("sum-1", "Summarising 3 files", kind="task")      # a circle beside the notch (turning: unknown)
    task.update(progress=0.4, accuracy="exact", detail="file 2 of 3")  # a ring: 40 %
    task.end()                                                         # or task.end(failed=True)
    bar.sound("done")                                                  # start | done | error
"""
import getpass
import json
import os
import re


class IbarError(Exception):
    pass


class Ibar:
    def __init__(self, plugin_id, token=None, pipe=None):
        user = re.sub(r"[^A-Za-z0-9]", "", getpass.getuser())[:32].lower()
        self.pipe = pipe or os.environ.get("IBAR_PIPE") or ("ibar-" + user)
        self.plugin = plugin_id
        self.token = token or os.environ.get("IBAR_TOKEN")

    def send(self, request):
        request = {"v": 1, **request}
        with open("\\\\.\\pipe\\" + self.pipe, "r+b", buffering=0) as pipe:
            pipe.write((json.dumps(request) + "\n").encode("utf-8"))
            line = b""
            while not line.endswith(b"\n"):
                chunk = pipe.read(1)
                if not chunk:
                    break
                line += chunk
        reply = json.loads(line.decode("utf-8"))
        if reply.get("status") not in ("ok", "pending"):
            raise IbarError(reply.get("error") or reply.get("status"))
        return reply

    def register(self, manifest):
        reply = self.send({"op": "register", "plugin": manifest["id"], "token": self.token, "manifest": manifest})
        self.token = reply.get("token") or self.token
        return self.token

    def _op(self, op, **fields):
        return self.send({"op": op, "plugin": self.plugin, "token": self.token, **{k: v for k, v in fields.items() if v is not None}})

    def notify(self, title, body=None, priority=None, duration=None, link=None):
        return self._op("notify", title=title, body=body, priority=priority, duration=duration, link=link)

    def chip(self, color, value):
        return self._op("chip", color=color, value=value)

    def panel(self, widgets, buttons=None):
        return self._op("panel", widgets=widgets, buttons=buttons)

    def clear(self):
        return self._op("clear")

    # ---- activity: one task on the notch's circle (start / update / end), and sounds

    def start(self, task, title, kind="task", progress=None, accuracy=None, detail=None, done=None, total=None, rate=None, color=None):
        """Starts a task. Leave progress out when the amount is unknown: the ring turns and no number is shown."""
        self._op("activity.start", task=task, title=title, kind=kind, progress=progress, accuracy=accuracy, detail=detail, done=done, total=total, rate=rate, color=color)
        return Task(self, task)

    def update(self, task, title=None, progress=None, accuracy=None, detail=None, done=None, total=None, rate=None, state=None):
        """progress 0..1 needs accuracy: exact, good or estimate. state: running, paused or waiting."""
        return self._op("activity.update", task=task, title=title, progress=progress, accuracy=accuracy, detail=detail, done=done, total=total, rate=rate, state=state)

    def end(self, task, failed=False, detail=None):
        return self._op("activity.end", task=task, state="failed" if failed else "done", detail=detail)

    def sound(self, kind):
        """start, done or error (IBAR plays it unless muted or a full-screen app is in front)."""
        return self._op("sound", sound=kind)


class Task:
    def __init__(self, bar, task):
        self.bar = bar
        self.task = task

    def update(self, **fields):
        return self.bar.update(self.task, **fields)

    def end(self, failed=False, detail=None):
        return self.bar.end(self.task, failed=failed, detail=detail)