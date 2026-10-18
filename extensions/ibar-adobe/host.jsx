// IBAR Bridge — ExtendScript side. Returns the render queue's state as JSON: item number, comp name, state. No
// paths, no project content.
function ibarQueue() {
    try {
        if (typeof app === "undefined" || !app.project || !app.project.renderQueue) {
            return "{\"busy\":false,\"items\":[]}";
        }
        var rq = app.project.renderQueue;
        var items = [];
        var busy = false;
        for (var i = 1; i <= rq.numItems; i++) {
            var it = rq.item(i);
            var state = "queued";
            if (it.status === RQItemStatus.RENDERING) { state = "rendering"; busy = true; }
            else if (it.status === RQItemStatus.DONE) { state = "done"; }
            else if (it.status === RQItemStatus.ERR_STOPPED || it.status === RQItemStatus.USER_STOPPED) { state = "failed"; }
            else if (it.status === RQItemStatus.QUEUED) { busy = busy || rq.rendering; }
            var name = (it.comp && it.comp.name) ? String(it.comp.name).substring(0, 60) : ("Item " + i);
            items.push("{\"id\":" + i + ",\"name\":" + JSON_quote(name) + ",\"state\":\"" + state + "\"}");
        }
        return "{\"busy\":" + (busy || rq.rendering ? "true" : "false") + ",\"items\":[" + items.join(",") + "]}";
    } catch (e) {
        return "{\"busy\":false,\"items\":[]}";
    }
}

function JSON_quote(s) {
    return "\"" + String(s).replace(/\\/g, "\\\\").replace(/"/g, "\\\"").replace(/[\u0000-\u001f]/g, " ") + "\"";
}
