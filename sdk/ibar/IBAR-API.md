# IBAR API — connecting an app to the notch

For developers of apps such as **Alita** (Python, Go, Rust or anything that can open a Windows named pipe).

> **خلاصهٔ فارسی:** برنامهٔ شما با یک named pipe محلی (فقط همان کاربر ویندوز) به IBAR وصل می‌شود و در هر خط یک
> JSON می‌فرستد. اولین بار با یک manifest ثبت می‌شود و کاربر روی ناچ «Allow» می‌زند؛ IBAR یک token برمی‌گرداند که در
> همهٔ پیام‌های بعدی لازم است. بعد از آن برنامه می‌تواند: یک «کار» با دایرهٔ پیشرفت کنار ناچ نشان دهد (start/update/end)،
> اعلان بفرستد، صدای شروع/پایان/خطا پخش کند، یک chip و یک داشبورد کوچک بگذارد. فقط دادهٔ اعلامی (JSON)؛ هیچ کد،
> HTML یا تصویری از اینترنت. دکمه‌ها فقط فرمان یا آدرسی را اجرا می‌کنند که در manifest تأییدشده آمده است. SDK آماده
> برای Python، Go و Rust در همین پوشه است و `ibar-ctl.exe` برای اسکریپت‌ها.

---

## 1. Transport

| | |
|---|---|
| Pipe | `\\.\pipe\ibar-<user>` — `<user>` is the Windows user name, letters and digits only, lower case, at most 32 characters (e.g. `ibar-arsha`). `IBAR_PIPE` overrides it (tests). |
| Who can connect | Only processes of the same Windows user (the pipe is created *current user only*). |
| Framing | One UTF-8 JSON object per line (`\n`), at most 32 KB per line. IBAR answers each line with one line. |
| Connections | Open, send, read the answer, close — or keep one connection open and send many lines. |
| Schema | Every request carries `"v": 1`. A request with another `v` is refused with an error naming the supported version. New fields and operations are added without changing `v`; a removed or changed one would raise it. |
| Answer | `{"status":"ok"}`, `{"status":"ok","token":"…"}` (register), `{"status":"pending"}`, `{"status":"denied","error":"…"}` or `{"status":"error","error":"…"}`. |

## 2. Registering (once) — the user approves on the notch

```json
{"v":1,"op":"register","plugin":"alita","manifest":{
  "schema":1,
  "id":"alita",
  "name":"Alita",
  "publisher":"Alita team",
  "icon":"C:\\Program Files\\Alita\\alita.png",
  "actions":[
    {"id":"open","title":"Open Alita","command":"C:\\Program Files\\Alita\\Alita.exe","args":["--show"]},
    {"id":"docs","title":"Docs","url":"https://alita.example/docs"}
  ]}}
```

* The notch shows an **Allow / Deny** request with the app's name, publisher and its actions. The call waits for the
  answer (up to two minutes); the answer to "Allow" is `{"status":"ok","token":"<48 hex>"}`.
* **Keep the token** (for example in your app's settings folder) and send it as `"token"` in every later request.
  Registering again with the same manifest and token returns the same token without asking.
* Any change to the manifest (another command, a new action, a new icon) is a new request the user must approve.
  A manifest the user declined is not asked about again until it changes.
* Rules: `id` 2–40 of `a–z 0–9 - _`; `name` 1–40 printable characters; `icon` a full path to a local `.png`/`.ico`
  (≤ 512 KB); at most 8 actions, each either `url` (http/https only) **or** `command` (the full path of an `.exe` with
  up to 16 fixed arguments — no shells, no scripts). At most 16 plugins.

## 3. Tasks on the activity circle — `activity.start` / `activity.update` / `activity.end`

One circle beside the notch shows the newest running task: its icon and a ring of how much is done. A click on it
brings out the other tasks, each with its own ring and a caption. A finished task's circle goes back into the notch.

```json
{"v":1,"op":"activity.start","plugin":"alita","token":"…","task":"summary-42","title":"Summarising 3 files","kind":"task"}
{"v":1,"op":"activity.update","plugin":"alita","token":"…","task":"summary-42","progress":0.4,"accuracy":"exact","detail":"file 2 of 3"}
{"v":1,"op":"activity.end","plugin":"alita","token":"…","task":"summary-42","state":"done"}
```

| Field | Meaning |
|---|---|
| `task` | Your id for the task (2–40 of `a–z 0–9 - _`), unique within your app. Required. |
| `title` | Required for `activity.start` (≤ 80). |
| `kind` | `task`, `download`, `render`, `export`, `save`, `copy`… (a short word). |
| `progress` | 0…1. **Leave it out when you do not know** — the ring then turns and no number is shown. IBAR never shows an invented number. |
| `accuracy` | Required with `progress`: `exact` (counted), `good` (an app's own figure), `estimate` (derived — shown as "≈ 40 %"). |
| `done`, `total` | Units done / in all (bytes for downloads). With `total` and an accuracy they also make the progress. |
| `rate` | Units per second (shown as speed for bytes). |
| `detail` | One short line (≤ 120). |
| `state` | `running` (default), `paused`, `waiting` (amber: waiting for the user). `activity.end`: `done` (default) or `failed`. |
| `color` | `#RRGGBB` for the ring (default: your app's colour). |

At most 4 tasks per app at a time. A task that gets no update for 10 minutes is cleared. IBAR plays its *start*
sound when a task starts and *done* / *error* when it ends (one sound at a time; silent in full-screen apps and games
and when the user turned sounds off).

## 4. Notifications — `notify`

```json
{"v":1,"op":"notify","plugin":"alita","token":"…","title":"Alita finished","body":"3 files summarised.","priority":"normal","duration":6,"link":"docs"}
```

`title` 1–80, `body` ≤ 300, `priority` `low`/`normal`/`high` (high stays until seen), `duration` 2–30 s, `link` the
`id` of a declared action run when the notification is clicked (in place of opening your app). At most one
notification every two seconds (bursts of 3).

Notifications sent through IBAR's pipe are the only ones whose buttons fully work: IBAR runs the declared action.
(Other apps' Windows notifications can be shown on the notch, but Windows does not let one app press another app's
notification buttons.)

## 5. Sounds — `sound`

```json
{"v":1,"op":"sound","plugin":"alita","token":"…","sound":"done"}
```

`start`, `done` or `error`: three soft synthesized sounds of one family (140–200 ms). At most one every five
seconds per app; the user's volume / mute and full-screen silence apply.

## 6. Chip and dashboard — `chip`, `panel`, `clear`

```json
{"v":1,"op":"chip","plugin":"alita","token":"…","color":"#22C55E","value":"3"}
{"v":1,"op":"panel","plugin":"alita","token":"…","widgets":[
   {"kind":"number","label":"Tasks","value":"12"},
   {"kind":"bar","label":"CPU","fraction":0.42},
   {"kind":"spark","label":"Load","values":[3,5,4,8,6,9,7]}],
 "buttons":["open","docs"]}
{"v":1,"op":"clear","plugin":"alita","token":"…"}
```

Chip: a coloured circle on the notch's left with up to 3 characters. Panel: 1–3 widgets — `number` (`value` ≤ 12),
`bar` (`fraction` 0…1), `list` (`items`, ≤ 5 lines of ≤ 60), `spark` (`values`, 2–60 numbers) — each with an
optional `label` (≤ 30), and up to 3 `buttons` naming declared actions. `clear` removes the chip and the panel.

## 7. Limits and security

* Same Windows user only; the token is compared in constant time; a request with a wrong or missing token is
  `denied`.
* Rate limit per app: 2 requests a second (bursts of 10); notifications 1 per 2 s; sounds 1 per 5 s.
* Everything is declarative data. No code, markup or remote pictures are ever shown; actions run exactly as
  approved (an `.exe` with fixed arguments, or an http(s) address), never anything built from a message.
* The user can remove an app in IBAR's settings (Plugins → Remove); its token stops working at once.

## 8. Ready-made clients

| | |
|---|---|
| Python | `python/ibar.py` (no dependencies) — example `python/example.py` |
| Go | `go/ibar.go` (standard library only) — example `go/cmd/alita-demo` |
| Rust | `rust/src/lib.rs` (`serde_json`) — example `rust/examples/alita.rs` |
| Command line | `ibar-ctl.exe` (in IBAR's folder) |

### Python

```python
from ibar import Ibar

bar = Ibar("alita", token=saved_token)          # saved_token: None the first time
token = bar.register(MANIFEST)                  # the user approves on the notch; save the token
task = bar.start("summary-42", "Summarising 3 files")   # turning ring (unknown amount)
task.update(progress=0.4, accuracy="exact", detail="file 2 of 3")
task.end()                                      # or task.end(failed=True)
bar.sound("done")
bar.notify("Alita finished", "3 files summarised.", link="docs")
```

### Go

```go
bar := ibar.New("alita", savedToken)
token, err := bar.Register(manifest)
_ = bar.Start(ibar.Activity{Task: "summary-42", Title: "Summarising 3 files"})
_ = bar.Update(ibar.Activity{Task: "summary-42", Progress: ibar.F(0.4), Accuracy: "exact"})
_ = bar.End("summary-42", false)
_ = bar.Sound("done")
```

### Rust

```rust
let mut bar = Ibar::new("alita", saved_token);
bar.register(manifest)?;
bar.start("summary-42", "Summarising 3 files", "task", None, None)?;
bar.update("summary-42", json!({"progress": 0.4, "accuracy": "exact"}))?;
bar.end("summary-42", false)?;
bar.sound("done")?;
```

### Command line

```
ibar-ctl register manifest.json                       (prints the token)
set IBAR_TOKEN=<token>
ibar-ctl activity start --plugin alita --task r1 --title "Rendering intro" --kind render
ibar-ctl activity update --plugin alita --task r1 --progress 0.25 --accuracy estimate
ibar-ctl activity end --plugin alita --task r1
ibar-ctl sound done --plugin alita
ibar-ctl notify --plugin alita --title "Done" --body "Intro rendered" --link open
```

Exit codes: 0 ok, 1 refused (the reason is printed), 2 bad arguments, 3 IBAR not running.

## 9. Claude Code and Codex

Not through this API: IBAR's own hooks (`ibar-ctl hook claude|codex`) bring their permission requests (Allow /
Deny) and their working / done states to the notch; connect them in IBAR's settings.
