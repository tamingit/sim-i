// The "Files of this program" panel: the files an RPG program uses (its file declarations, the
// tables of its embedded SQL, the files its data structures are described by), what each is on the
// Sim i, where it is on a real IBM i and how big, and a way to copy the ones chosen from
// there, each with a filter on its records if it has one.
"use strict";

const vscode = require("vscode");
const path = require("path");

/** Opens the panel for a source file. `sim` talks to Sim i; `remote` says how to reach the real IBM i. */
async function show(context, uri, sim, remote) {
  const name = path.basename(uri.fsPath, path.extname(uri.fsPath)).toUpperCase();
  const library = sim.libraryOf(uri);
  const panel = vscode.window.createWebviewPanel("simIFiles", `Files of ${name}`, vscode.ViewColumn.Beside, { enableScripts: true, retainContextWhenHidden: true });
  const state = { program: name, library, files: [], remote: null, storage: null, message: "", busy: "", log: [], result: null, settings: remote.settings() };
  const post = () => panel.webview.postMessage({ type: "state", state });
  let disposed = false;
  panel.onDidDispose(() => { disposed = true; });

  async function load() {
    try {
      const found = await sim.web("GET", `/api/program/files?name=${encodeURIComponent(name)}${library ? "&library=" + encodeURIComponent(library) : ""}`);
      state.files = found.files;
      state.storage = found.storage;
      state.message = "";
    } catch (error) {
      state.files = [];
      state.message = error.message;
    }
    post();
  }

  /** Where the program's database files are on the real IBM i, with the physical files their logical files need. */
  async function find(afterCopy = false) {
    const login = await remote.login();
    if (!login) return;
    state.settings = remote.settings();
    state.busy = `Looking in ${login.libraries.join(", ")} on ${login.host}…`;
    if (!afterCopy) state.result = null;
    post();
    try {
      const names = state.files.filter(f => f.database).map(f => f.name);
      state.remote = await sim.web("POST", "/api/remote/files", JSON.stringify({ ...login, objects: names }), "application/json");
      state.storage = state.remote.storage;
      state.message = "";
    } catch (error) {
      state.message = error.message;
      if (/sign|log on|password/i.test(error.message)) await remote.forget();
    }
    state.busy = "";
    post();
  }

  /** Copies the files chosen, a library at a time, following each receive to its end. */
  async function copy(chosen, filters, data) {
    const login = await remote.login();
    if (!login) return;
    state.log = [];
    state.result = null;
    const byLibrary = new Map();
    for (const file of state.remote.files.filter(f => chosen.includes(f.library + "/" + f.name))) {
      if (!byLibrary.has(file.library)) byLibrary.set(file.library, []);
      byLibrary.get(file.library).push(file.name);
    }
    let failed = false;
    for (const [from, objects] of byLibrary) {
      const only = {};
      for (const object of objects) if (filters[from + "/" + object]) only[object] = filters[from + "/" + object];
      state.busy = `Copying ${objects.length} file${objects.length === 1 ? "" : "s"} from ${from} on ${login.host}…`;
      post();
      try {
        let transfer = await sim.web("POST", "/api/remote/pull", JSON.stringify({ ...login, library: from, objects, filters: only, data }), "application/json");
        let next = 0;
        while (!disposed) {
          transfer = await sim.web("GET", "/api/remote/pull?after=" + next);
          next = transfer.next;
          state.log.push(...transfer.lines);
          state.busy = `Copying from ${from}: ${transfer.restored + transfer.failed} of ${objects.length} files, ${Number(transfer.records).toLocaleString("en-US")} records`;
          state.storage = transfer.storage;
          post();
          if (!transfer.running) break;
          await new Promise(resolve => setTimeout(resolve, 1000));
        }
        state.log.push(transfer.message || "");
        failed = failed || !transfer.ok;
        state.result = { ok: !failed, message: transfer.message };
      } catch (error) {
        state.result = { ok: false, message: error.message };
        failed = true;
        break;
      }
    }
    state.busy = "";
    // With its files here, the program is checked again: what was missing is no longer a problem.
    await sim.recompile();
    await load();
    if (state.remote) await find(true);
  }

  panel.webview.onDidReceiveMessage(async message => {
    if (message.type === "ready" || message.type === "refresh") await load();
    else if (message.type === "find") await find();
    else if (message.type === "copy") await copy(message.chosen, message.filters, message.data);
    else if (message.type === "cancel") await sim.web("POST", "/api/remote/cancel").catch(() => {});
    else if (message.type === "settings") await vscode.commands.executeCommand("workbench.action.openSettings", "simI.remote");
    else if (message.type === "goto") {
      const document = await vscode.workspace.openTextDocument(uri);
      const editor = await vscode.window.showTextDocument(document, vscode.ViewColumn.One);
      const line = Math.max(0, (message.line || 1) - 1);
      editor.revealRange(new vscode.Range(line, 0, line, 0), vscode.TextEditorRevealType.InCenter);
      editor.selection = new vscode.Selection(line, 0, line, 0);
    }
  });
  panel.webview.html = page();
}

function page() {
  const nonce = Array.from({ length: 32 }, () => "abcdefghijklmnopqrstuvwxyz0123456789"[Math.floor(Math.random() * 36)]).join("");
  return `<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta http-equiv="Content-Security-Policy" content="default-src 'none'; style-src 'unsafe-inline'; script-src 'nonce-${nonce}'">
<style>
  body { font: var(--vscode-font-size) var(--vscode-font-family); color: var(--vscode-foreground); padding: 12px 16px; }
  h1 { font-size: 1.25em; margin: 0 0 4px; font-weight: 600; }
  p { margin: 4px 0 10px; color: var(--vscode-descriptionForeground); }
  table { border-collapse: collapse; width: 100%; margin: 8px 0 12px; }
  .scroll { overflow-x: auto; }
  th, td { text-align: left; padding: 4px 10px 4px 0; border-bottom: 1px solid var(--vscode-widget-border, rgba(128,128,128,.25)); vertical-align: middle; }
  td.num, th.num { white-space: nowrap; }
  .name { font-family: var(--vscode-editor-font-family); font-weight: 600; }
  th { color: var(--vscode-descriptionForeground); font-weight: 600; }
  td.num, th.num { text-align: right; font-variant-numeric: tabular-nums; }
  .muted { color: var(--vscode-descriptionForeground); }
  .missing { color: var(--vscode-editorWarning-foreground); }
  .bad { color: var(--vscode-errorForeground); }
  .good { color: var(--vscode-testing-iconPassed, #3a8a4f); }
  button { color: var(--vscode-button-foreground); background: var(--vscode-button-background); border: 0; padding: 5px 12px; border-radius: 2px; cursor: pointer; font: inherit; }
  button:hover { background: var(--vscode-button-hoverBackground); }
  button.secondary { color: var(--vscode-button-secondaryForeground); background: var(--vscode-button-secondaryBackground); }
  button:disabled { opacity: .5; cursor: default; }
  button.link { background: none; color: var(--vscode-textLink-foreground); padding: 0; }
  input[type=text] { font: inherit; color: var(--vscode-input-foreground); background: var(--vscode-input-background); border: 1px solid var(--vscode-input-border, transparent); padding: 3px 6px; width: 100%; min-width: 16ch; box-sizing: border-box; font-family: var(--vscode-editor-font-family); }
  .bar { display: flex; flex-wrap: wrap; gap: 8px 12px; align-items: center; margin: 8px 0; }
  .note { padding: 6px 10px; border-left: 3px solid var(--vscode-textLink-foreground); background: var(--vscode-textBlockQuote-background); margin: 8px 0; }
  .note.bad { border-color: var(--vscode-errorForeground); color: var(--vscode-foreground); }
  .note.good { border-color: var(--vscode-testing-iconPassed, #3a8a4f); color: var(--vscode-foreground); }
  pre { font-family: var(--vscode-editor-font-family); font-size: .92em; max-height: 30vh; overflow: auto; background: var(--vscode-textCodeBlock-background); padding: 8px; margin: 8px 0; white-space: pre-wrap; }
</style>
</head>
<body>
<h1 id="title">Files</h1>
<p id="lead"></p>
<div id="message"></div>
<div class="scroll"><table id="table"><thead></thead><tbody></tbody></table></div>
<div class="bar" id="actions"></div>
<div id="room" class="muted"></div>
<div id="result"></div>
<pre id="log" hidden></pre>
<script nonce="${nonce}">
const vscode = acquireVsCodeApi();
const chosen = new Set(), filters = {};
let state = null, withData = true, touched = false;
const $ = id => document.getElementById(id);
const el = (tag, attrs, ...children) => {
  const node = document.createElement(tag);
  for (const [k, v] of Object.entries(attrs || {})) {
    if (k === "class") node.className = v; else if (k.startsWith("on")) node.addEventListener(k.slice(2), v); else if (v !== false && v !== null && v !== undefined) node.setAttribute(k, v === true ? "" : v);
  }
  for (const c of children) if (c !== null && c !== undefined && c !== false) node.append(c);
  return node;
};
const count = n => Number(n).toLocaleString("en-US");
function bytes(n) {
  if (n === null || n === undefined) return "";
  const units = ["bytes", "KB", "MB", "GB", "TB"];
  let i = 0, v = n;
  while (v >= 1024 && i < units.length - 1) { v /= 1024; i++; }
  return (i === 0 ? v : v >= 100 ? v.toFixed(0) : v >= 10 ? v.toFixed(1) : v.toFixed(2)) + " " + units[i];
}
const key = f => f.library + "/" + f.name;
const hasRecords = f => f.kind === "Physical file" || f.kind === "SQL table";

function draw() {
  $("title").textContent = "Files of " + state.program;
  const database = state.files.filter(f => f.database);
  const missing = database.filter(f => !f.kind);
  $("lead").textContent = state.files.length === 0 ? "" : database.length + " database file" + (database.length === 1 ? "" : "s") + (missing.length ? ", " + missing.length + " not on Sim i" : ", all on Sim i")
    + (state.files.length > database.length ? "; " + (state.files.length - database.length) + " display or printer file" + (state.files.length - database.length === 1 ? "" : "s") + " (made by compiling their source)" : "") + ".";
  $("message").replaceChildren(state.message ? el("div", { class: "note bad" }, state.message) : "");
  const remote = state.remote ? state.remote.files : null;
  const head = el("tr", null, el("th", null, remote ? "Copy" : ""), el("th", null, "File"), el("th", null, "On Sim i"));
  if (remote) head.append(el("th", null, "On " + state.remote.host), el("th", { class: "num" }, "Estimate here"), el("th", null, "Only records where"));
  $("table").tHead.replaceChildren(head);
  const rows = [];
  const hereText = (kind, library, records) => (records !== null && records !== undefined ? count(records) + " records" : kind) + (library ? " in " + library : "");
  const simCell = f => f.kind ? el("td", { title: f.kind }, hereText(f.kind, f.hereLibrary, f.records))
    : el("td", { class: "missing" }, f.database ? "Not here" : "Not here: compile its source");
  const remoteCells = r => {
    if (!r) return [el("td", { class: "muted", colspan: 3 }, "Not found there")];
    const filter = el("input", { type: "text", placeholder: hasRecords(r) ? "all, or CUSTNO > 1000" : "", "aria-label": "Filter for " + r.name, disabled: !hasRecords(r) || !!state.busy });
    filter.value = filters[key(r)] || "";
    filter.addEventListener("input", () => { filters[key(r)] = filter.value.trim(); });
    return [
      el("td", null, r.kind + " in " + r.library, el("div", { class: "muted" },
        [hasRecords(r) ? count(r.records) + " records" + (r.deleted ? " (+" + count(r.deleted) + " deleted)" : "") : null, bytes(r.size), r.members > 1 ? r.members + " members: the first comes" : null].filter(Boolean).join(" · "))),
      el("td", { class: "num" }, r.receivable ? bytes(withData ? r.estimate : r.bare) : "not copied"), el("td", null, filter)];
  };
  const box = r => {
    if (!r || !r.receivable) return el("td");
    const input = el("input", { type: "checkbox", "aria-label": "Copy " + r.name, disabled: !!state.busy });
    input.checked = chosen.has(key(r));
    input.addEventListener("change", () => { touched = true; if (input.checked) chosen.add(key(r)); else chosen.delete(key(r)); actions(); });
    return el("td", null, input);
  };
  for (const f of state.files) {
    const r = remote && f.database ? remote.find(x => x.name === f.name && !x.neededBy) : null;
    const row = el("tr", null, remote ? box(r) : el("td"),
      el("td", null, el("span", { class: "name" }, f.name), el("div", { class: "muted" }, el("button", { class: "link", title: "Go to line " + f.line, onclick: () => vscode.postMessage({ type: "goto", line: f.line }) }, f.how), " " + f.use)),
      simCell(f));
    if (remote) row.append(...(f.database ? remoteCells(r) : [el("td", { class: "muted", colspan: 3 }, "")]));
    rows.push(row);
  }
  // The physical files the program's logical files are over: not named by the program, but needed for them.
  for (const r of (remote || []).filter(x => x.neededBy)) {
    rows.push(el("tr", null, box(r), el("td", null, el("span", { class: "name" }, r.name), el("div", { class: "muted" }, "needed by " + r.neededBy)),
      r.here ? el("td", { title: r.here.kind }, hereText(r.here.kind, r.here.library, r.here.records)) : el("td", { class: "missing" }, "Not here"), ...remoteCells(r)));
  }
  $("table").tBodies[0].replaceChildren(...rows);
  actions();
  const result = state.result;
  $("result").replaceChildren(result ? el("div", { class: "note " + (result.ok ? "good" : "bad") }, result.message) : "");
  $("log").hidden = state.log.length === 0;
  $("log").textContent = state.log.join("\\n");
  $("log").scrollTop = $("log").scrollHeight;
}

function actions() {
  const remote = state.remote ? state.remote.files : [];
  const picked = remote.filter(r => chosen.has(key(r)));
  const need = picked.reduce((sum, r) => sum + (withData ? r.estimate : r.bare), 0);
  const settings = state.settings || {};
  const data = el("input", { type: "checkbox", id: "data", disabled: !!state.busy });
  data.checked = withData;
  data.addEventListener("change", () => { withData = data.checked; draw(); });
  $("actions").replaceChildren(...[
    el("button", { class: state.remote ? "secondary" : "", disabled: !!state.busy || state.files.every(f => !f.database), onclick: () => vscode.postMessage({ type: "find" }) },
      state.remote ? "Look again on the IBM i" : "Find them on the IBM i"),
    state.remote ? el("button", { disabled: !!state.busy || picked.length === 0, onclick: () => vscode.postMessage({ type: "copy", chosen: [...chosen], filters, data: withData }) },
      picked.length ? "Copy " + picked.length + " file" + (picked.length === 1 ? "" : "s") + " to Sim i" : "Copy to Sim i") : null,
    state.remote ? el("label", null, data, " with records") : null,
    state.busy && /Copying/.test(state.busy) ? el("button", { class: "secondary", onclick: () => vscode.postMessage({ type: "cancel" }) }, "End it") : null,
    el("button", { class: "secondary", disabled: !!state.busy, onclick: () => vscode.postMessage({ type: "refresh" }) }, "Refresh"),
    el("span", { class: "muted" }, state.busy || (settings.host ? "IBM i: " + settings.host + (settings.libraries.length ? ", libraries " + settings.libraries.join(", ") : "") : "")),
    el("button", { class: "link", onclick: () => vscode.postMessage({ type: "settings" }) }, "Settings")].filter(Boolean));
  const storage = state.storage || {};
  const free = storage.free;
  const room = $("room");
  room.className = "muted";
  if (!state.remote) room.textContent = "";
  else if (picked.length === 0) room.textContent = free !== null && free !== undefined ? bytes(free) + " free on Sim i's database server." : "";
  else if (free === null || free === undefined) room.textContent = "What's chosen needs about " + bytes(need) + " (without filters). The database server doesn't say how much room it has.";
  else {
    room.textContent = "What's chosen needs about " + bytes(need) + (Object.values(filters).some(Boolean) ? " without its filters (less with them)" : "") + "; " + bytes(free) + " is free on Sim i's database server"
      + (need > free ? ": " + bytes(need - free) + " too much. Filter the big files, or copy without records." : ".");
    if (need > free) room.className = "bad";
  }
}

window.addEventListener("message", event => {
  if (event.data.type !== "state") return;
  const first = !state || !state.remote;
  state = event.data.state;
  // What isn't on Sim i yet is chosen to begin with, until the choice is changed by hand.
  if (state.remote && (first || !touched)) {
    chosen.clear();
    for (const r of state.remote.files) if (r.receivable && !r.here) chosen.add(key(r));
  }
  draw();
});
vscode.postMessage({ type: "ready" });
</script>
</body>
</html>`;
}

module.exports = { show };
