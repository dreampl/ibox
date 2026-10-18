//! Thin IBAR plugin client: one JSON object per line over IBAR's named pipe (schema 1). Windows only.
use serde_json::{json, Value};
use std::fs::OpenOptions;
use std::io::{BufRead, BufReader, Write};

pub struct Ibar {
    pub pipe: String,
    pub plugin: String,
    pub token: Option<String>,
}

impl Ibar {
    pub fn new(plugin: &str, token: Option<String>) -> Self {
        let pipe = std::env::var("IBAR_PIPE").unwrap_or_else(|_| {
            let user: String = std::env::var("USERNAME").unwrap_or_default().chars().filter(|c| c.is_ascii_alphanumeric()).take(32).collect();
            format!("ibar-{}", user.to_lowercase())
        });
        Ibar { pipe, plugin: plugin.to_string(), token: token.or_else(|| std::env::var("IBAR_TOKEN").ok()) }
    }

    pub fn send(&self, mut request: Value) -> Result<Value, String> {
        request["v"] = json!(1);
        let mut pipe = OpenOptions::new().read(true).write(true).open(format!(r"\\.\pipe\{}", self.pipe)).map_err(|e| e.to_string())?;
        pipe.write_all(format!("{}\n", request).as_bytes()).map_err(|e| e.to_string())?;
        let mut line = String::new();
        BufReader::new(pipe).read_line(&mut line).map_err(|e| e.to_string())?;
        let reply: Value = serde_json::from_str(&line).map_err(|e| e.to_string())?;
        match reply["status"].as_str() {
            Some("ok") | Some("pending") => Ok(reply),
            _ => Err(reply["error"].as_str().unwrap_or("refused").to_string()),
        }
    }

    pub fn register(&mut self, manifest: Value) -> Result<Option<String>, String> {
        let reply = self.send(json!({"op": "register", "plugin": manifest["id"], "token": self.token, "manifest": manifest}))?;
        if let Some(t) = reply["token"].as_str() {
            self.token = Some(t.to_string());
        }
        Ok(self.token.clone())
    }

    fn op(&self, op: &str, mut fields: Value) -> Result<Value, String> {
        fields["op"] = json!(op);
        fields["plugin"] = json!(self.plugin);
        fields["token"] = json!(self.token);
        self.send(fields)
    }

    pub fn notify(&self, title: &str, body: &str, link: Option<&str>) -> Result<Value, String> {
        self.op("notify", strip(json!({"title": title, "body": body, "link": link})))
    }

    pub fn chip(&self, color: &str, value: &str) -> Result<Value, String> {
        self.op("chip", json!({"color": color, "value": value}))
    }

    pub fn panel(&self, widgets: Value, buttons: Value) -> Result<Value, String> {
        self.op("panel", json!({"widgets": widgets, "buttons": buttons}))
    }

    /// Starts a task on the notch's activity circle. `progress`: None = unknown (the ring turns, no number);
    /// Some(0..1) needs an `accuracy` of "exact", "good" or "estimate".
    pub fn start(&self, task: &str, title: &str, kind: &str, progress: Option<f64>, accuracy: Option<&str>) -> Result<Value, String> {
        self.op("activity.start", strip(json!({"task": task, "title": title, "kind": kind, "progress": progress, "accuracy": accuracy})))
    }

    /// Any of: title, detail, state (running|paused|waiting), progress, accuracy, done, total, rate, color.
    pub fn update(&self, task: &str, mut fields: Value) -> Result<Value, String> {
        fields["task"] = json!(task);
        self.op("activity.update", strip(fields))
    }

    pub fn end(&self, task: &str, failed: bool) -> Result<Value, String> {
        self.op("activity.end", json!({"task": task, "state": if failed { "failed" } else { "done" }}))
    }

    /// "start", "done" or "error".
    pub fn sound(&self, kind: &str) -> Result<Value, String> {
        self.op("sound", json!({"sound": kind}))
    }
}

/// Leaves out unset (null) fields: IBAR reads a missing progress as "unknown".
fn strip(mut v: Value) -> Value {
    if let Some(map) = v.as_object_mut() {
        map.retain(|_, x| !x.is_null());
    }
    v
}