# IBAR plugin API (schema 1)

IBAR shows plugins on the notch: notifications, a status chip (coloured circle + short value), a small dashboard
(number, bar, list, sparkline) and buttons for actions the plugin declared. Plugins talk to IBAR over a local
named pipe that only the same Windows user can open: `\\.\pipe\ibar-<user>` (user = the Windows user name, letters
and digits only, lower case). One JSON object per line each way. `IBAR_PIPE` overrides the name.

Thin clients: [`python/ibar.py`](python/ibar.py), [`go/ibar.go`](go/ibar.go), [`rust/src/lib.rs`](rust/src/lib.rs),
each with an Alita-style example. From any language or a script you can also use `ibar-ctl` (next to IBAR.exe).

## Security model

* **Register once, approved by the user.** A plugin sends its manifest; IBAR asks on the notch (Allow / Deny). On
  Allow it answers with a secret `token`; every later message must carry it. Keep it (a file only you can read).
* **Any change to the manifest is a new request** (IBAR stores a fingerprint of what was approved).
* **Data only.** Plugins never send code, markup or pictures from the network. Widgets have four fixed kinds, all
  drawn by IBAR. The icon is a local `.png` / `.ico` path.
* **Actions are declared and approved:** a full path to an `.exe` with fixed arguments (no shells, no scripts), or an
  `http(s)` address. A button or a notification click can only name an action id.
* **Rate limits** per plugin (about 2 messages a second with short bursts; one notification every 2 s).
* Only programs of the same Windows user can reach the pipe. Chat with Claude / Codex is never started from a plugin.

## Messages

Every request has `"v": 1` and `"op"`; every answer is `{"status": "ok" | "pending" | "denied" | "error", "token"?, "error"?}`.

| op | fields |
| --- | --- |
| `register` | `manifest` (below), `token` (the old one, if any). Blocks until the user decides (up to ~2 min). |
| `notify` | `plugin`, `token`, `title` (≤ 80), `body` (≤ 300), `priority` (`low`/`normal`/`high`), `duration` (2–30 s), `link` (action id) |
| `chip` | `plugin`, `token`, `color` (`#RRGGBB`), `value` (≤ 3 characters) |
| `panel` | `plugin`, `token`, `widgets` (1–3), `buttons` (≤ 3 action ids) |
| `clear` | `plugin`, `token` — removes the chip and the panel |

Manifest:

```json
{
  "schema": 1,
  "id": "alita",
  "name": "Alita",
  "publisher": "Alita team",
  "icon": "C:\\Program Files\\Alita\\alita.png",
  "actions": [
    { "id": "open", "title": "Open", "command": "C:\\Program Files\\Alita\\alita.exe", "args": ["--show"] },
    { "id": "docs", "title": "Docs", "url": "https://example.com/alita" }
  ]
}
```

Widgets: `{"kind":"number","label":"Tasks","value":"12"}`, `{"kind":"bar","label":"CPU","fraction":0.42}`,
`{"kind":"list","label":"Next","items":["…"]}` (≤ 5 lines), `{"kind":"spark","label":"Load","values":[3,5,4,8]}`.

## ibar-ctl

```
ibar-ctl register alita.json
ibar-ctl notify --plugin alita --token TOKEN --title "Done" --body "3 files" --link docs
ibar-ctl chip   --plugin alita --token TOKEN --color #22C55E --value 3
ibar-ctl panel  --plugin alita --token TOKEN panel.json
ibar-ctl clear  --plugin alita --token TOKEN
```

`IBAR_TOKEN` can replace `--token`.

---

### خلاصهٔ فارسی

افزونه (مثل Alita) یک بار manifest خود را می‌فرستد و کاربر روی ناچ Allow می‌دهد؛ IBAR یک token برمی‌گرداند که در
همهٔ پیام‌های بعدی لازم است. پیام‌ها فقط دادهٔ JSON هستند (اعلان، چیپ وضعیت، داشبورد با چهار نوع ویجت، دکمه برای
اکشن‌های اعلام‌شده). اکشن فقط یک ‎.exe با مسیر کامل و آرگومان ثابت، یا یک آدرس http(s) است که در manifest
تأییدشده آمده باشد. هر تغییر در manifest دوباره تأیید می‌خواهد. سرعت پیام‌ها محدود است و فقط برنامه‌های همان کاربر
ویندوز به pipe دسترسی دارند.
