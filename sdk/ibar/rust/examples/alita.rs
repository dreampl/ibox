use ibar::Ibar;
use serde_json::json;

fn main() {
    let mut bar = Ibar::new("alita", None);
    match bar.register(json!({"schema": 1, "id": "alita", "name": "Alita", "actions": [{"id": "docs", "title": "Docs", "url": "https://example.com/alita"}]})) {
        Ok(Some(token)) => println!("token: {token}"),
        other => { println!("approve on the notch, then run again with IBAR_TOKEN set: {other:?}"); return; }
    }
    let _ = bar.chip("#22C55E", "3");
    let _ = bar.panel(json!([{"kind": "number", "label": "Tasks", "value": "12"}, {"kind": "list", "items": ["Summarise mail", "Plan week"]}]), json!(["docs"]));
    let _ = bar.notify("Alita finished a task", "3 files summarised.", Some("docs"));

    // A task on the activity circle: unknown at first (the ring turns), then exact progress, then done.
    let _ = bar.start("summary-1", "Summarising 3 files", "task", None, None);
    for i in 1..=3 {
        let _ = bar.update("summary-1", json!({"progress": i as f64 / 3.0, "accuracy": "exact", "detail": format!("file {i} of 3")}));
    }
    let _ = bar.end("summary-1", false);
    let _ = bar.sound("done");
}