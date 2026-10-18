// IBAR Bridge (CEP, Node enabled): passes render / export state to IBAR through IBAR's own ibar-host.exe (standard
// input, one JSON line per change). Only state, a short title and — where the app reports one — a progress
// figure; never project content or file paths. Nothing runs while nothing renders.
/* global __adobe_cep__, require */
(function () {
  var cs = window.__adobe_cep__;
  if (!cs) return;
  var host = JSON.parse(cs.getHostEnvironment()).appName; // AEFT, PPRO, PHXS, PHSP, ILST, AME
  var app = { AEFT: "aftereffects", PPRO: "premiere", PHXS: "photoshop", PHSP: "photoshop", ILST: "illustrator", AME: "mediaencoder" }[host];
  if (!app) return;

  var cp = require("child_process");
  var path = require("path");
  var fs = require("fs");
  var exe = path.join(process.env.LOCALAPPDATA || "", "Programs", "IBAR", "ibar-host.exe");
  if (!fs.existsSync(exe)) return;
  var bridge = null;
  var last = {};

  function send(msg) {
    var line = JSON.stringify(msg);
    if (last[msg.task] === line) return;
    last[msg.task] = line;
    try {
      if (!bridge || bridge.exitCode !== null) {
        bridge = cp.spawn(exe, ["adobe", app], { stdio: ["pipe", "ignore", "ignore"], windowsHide: true });
      }
      bridge.stdin.write(line + "\n");
    } catch (e) {
      bridge = null;
    }
  }

  function evalJsx(code, cb) {
    cs.evalScript(code, cb || function () {});
  }

  // After Effects / Premiere: the render queue's state, read only while something is queued or rendering.
  var polling = null;
  function watchQueue() {
    evalJsx("ibarQueue()", function (json) {
      var q;
      try { q = JSON.parse(json); } catch (e) { return; }
      (q.items || []).forEach(function (it) {
        var task = "rq-" + it.id;
        if (it.state === "rendering") send({ op: "activity.update", task: task, title: it.name, kind: "render", state: "running" });
        else if (it.state === "done") send({ op: "activity.end", task: task, state: "done" });
        else if (it.state === "failed") send({ op: "activity.end", task: task, state: "failed" });
      });
      if (!q.busy && polling) { clearInterval(polling); polling = null; }
    });
  }

  if (app === "aftereffects" || app === "premiere") {
    // A render starts from the app's own button: the queue is looked at when the app is used, and every
    // 2 s only while it renders.
    cs.addEventListener("com.adobe.csxs.events.ApplicationActivate", function () {
      evalJsx("ibarQueue()", function (json) {
        try { if (JSON.parse(json).busy && !polling) polling = setInterval(watchQueue, 2000); } catch (e) { /* not ready */ }
      });
    });
  }

  // Photoshop: its own notifications for save and export (sent when they finish).
  if (app === "photoshop") {
    var ids = ["save", "export"];
    ids.forEach(function (name) {
      evalJsx("stringIDToTypeID('" + name + "')", function (id) {
        var e = new CSEvent("com.adobe.PhotoshopRegisterEvent", "APPLICATION");
        e.extensionId = "app.jacki.ibar.bridge.main";
        e.data = id;
        cs.dispatchEvent(e);
      });
    });
    cs.addEventListener("com.adobe.PhotoshopJSONCallback" + "app.jacki.ibar.bridge.main", function () {
      var task = "ps-" + Date.now();
      send({ op: "activity.start", task: task, title: "Photoshop", kind: "save", state: "running" });
      send({ op: "activity.end", task: task, state: "done" });
    });
  }
})();
