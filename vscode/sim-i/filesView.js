// The Files view (a tab in the bottom panel, beside Problems and Terminal): the files the program of the RPG source
// open in the editor uses (its file declarations, the tables of its embedded SQL, the files its data structures are
// described by), what each is on Sim i, where it is on a real IBM i and how big, and a way to copy the ones checked
// from there, each with a filter on its records if it has one. It follows the editor: another RPG file, another program.
// A tree, not a webview: code-server shows webviews only over HTTPS (or on localhost), and this works over plain HTTP too.
"use strict";

const vscode = require("vscode");
const path = require("path");

const VIEW = "simI.files";
const RPG = [".rpgle", ".sqlrpgle"];
const isRpg = uri => !!uri && uri.scheme === "file" && RPG.includes(path.extname(uri.fsPath).toLowerCase());

const count = n => Number(n).toLocaleString("en-US");
const plural = (n, word) => `${count(n)} ${word}${n === 1 ? "" : "s"}`;
function bytes(n) {
  if (n === null || n === undefined) return "";
  const units = ["bytes", "KB", "MB", "GB", "TB"];
  let i = 0, v = n;
  while (v >= 1024 && i < units.length - 1) { v /= 1024; i++; }
  return (i === 0 ? v : v >= 100 ? v.toFixed(0) : v >= 10 ? v.toFixed(1) : v.toFixed(2)) + " " + units[i];
}
const key = f => f.library + "/" + f.name;
const hasRecords = f => f.kind === "Physical file" || f.kind === "SQL table";
const hereText = (kind, library, records) => (records !== null && records !== undefined ? plural(records, "record") : kind) + (library ? " in " + library : "");

class FilesView {
  /** `sim` talks to Sim i (web, libraryOf, signedOn, upload); `remote` says how to reach the real IBM i; `output` takes a copy's log. */
  constructor(sim, remote, output) {
    this.sim = sim;
    this.remote = remote;
    this.output = output;
    this.programs = new Map();  // source file's uri -> its program's state
    this.current = null;
    this.busy = "";
    this.withData = true;
    this.copying = false;
    this.changed = new vscode.EventEmitter();
    this.onDidChangeTreeData = this.changed.event;
    this.view = null;
  }

  stateOf(uri) {
    let state = this.programs.get(uri.toString());
    if (!state) {
      state = {
        uri, program: path.basename(uri.fsPath, path.extname(uri.fsPath)).toUpperCase(), library: this.sim.libraryOf(uri),
        files: [], remote: null, storage: null, message: "", loaded: false, chosen: new Set(), filters: {}, touched: false,
      };
      this.programs.set(uri.toString(), state);
    }
    return state;
  }

  /** Shows the program of a source file: read from Sim i the first time (and when `reload`), once the view can be seen. */
  async show(uri, { ask = false, reload = false, upload = false } = {}) {
    if (!isRpg(uri)) return;
    const state = this.stateOf(uri);
    this.current = state;
    this.render();
    if ((reload || !state.loaded) && (this.view.visible || ask)) await this.load(state, ask, upload);
  }

  /** The files of the program, from Sim i. Sim i reads them from its member: one it doesn't have is sent to it first. */
  async load(state, ask, upload) {
    if (!ask && !(await this.sim.signedOn())) {
      state.message = "Not signed on to Sim i: Refresh signs on.";
      this.render();
      return;
    }
    const url = `/api/program/files?name=${encodeURIComponent(state.program)}${state.library ? "&library=" + encodeURIComponent(state.library) : ""}`;
    this.busy = `Reading ${state.program}'s files…`;
    this.render();
    try {
      await vscode.window.withProgress({ location: { viewId: VIEW } }, async () => {
        if (upload) await this.sim.upload(state.uri);
        let found;
        try {
          found = await this.sim.web("GET", url);
        } catch (error) {
          if (upload || !/save its source/.test(error.message) || !(await this.sim.upload(state.uri))) throw error;
          found = await this.sim.web("GET", url);
        }
        state.files = found.files;
        state.storage = found.storage;
        state.message = "";
      });
    } catch (error) {
      state.files = [];
      state.message = error.message;
    }
    state.loaded = true;
    this.busy = "";
    this.render();
  }

  /** Where the program's database files are on the real IBM i, with the physical files their logical files need. */
  async find(afterCopy = false) {
    const state = this.current;
    if (!state || this.busy) return;
    const login = await this.remote.login();
    if (!login) return;
    this.busy = `Looking in ${login.libraries.join(", ")} on ${login.host}…`;
    this.render();
    try {
      await vscode.window.withProgress({ location: { viewId: VIEW } }, async () => {
        const names = state.files.filter(f => f.database).map(f => f.name);
        state.remote = await this.sim.web("POST", "/api/remote/files", JSON.stringify({ ...login, objects: names }), "application/json");
      });
      state.storage = state.remote.storage;
      state.message = "";
      // What isn't on Sim i yet is checked to begin with, until the checks are changed by hand.
      if (!afterCopy || !state.touched) {
        state.chosen.clear();
        for (const r of state.remote.files) if (r.receivable && !r.here) state.chosen.add(key(r));
      }
    } catch (error) {
      state.message = error.message;
      if (/sign|log on|password/i.test(error.message)) await this.remote.forget();
    }
    this.busy = "";
    this.render();
  }

  /** Copies the files checked, a library at a time, following each receive to its end; its log goes to the output. */
  async copy() {
    const state = this.current;
    if (!state || !state.remote || this.busy) return;
    const picked = state.remote.files.filter(f => f.receivable && state.chosen.has(key(f)));
    if (picked.length === 0) return vscode.window.showInformationMessage("Sim i: check the files to copy first.");
    const login = await this.remote.login();
    if (!login) return;
    const byLibrary = new Map();
    for (const file of picked) {
      if (!byLibrary.has(file.library)) byLibrary.set(file.library, []);
      byLibrary.get(file.library).push(file.name);
    }
    this.copying = true;
    this.output.show(true);
    let result = { ok: true, message: "" };
    await vscode.window.withProgress({ location: { viewId: VIEW } }, async () => {
      for (const [from, objects] of byLibrary) {
        const only = {};
        for (const object of objects) if (state.filters[from + "/" + object]) only[object] = state.filters[from + "/" + object];
        this.busy = `Copying ${plural(objects.length, "file")} from ${from} on ${login.host}…`;
        this.render();
        try {
          let transfer = await this.sim.web("POST", "/api/remote/pull", JSON.stringify({ ...login, library: from, objects, filters: only, data: this.withData }), "application/json");
          let next = 0;
          for (;;) {
            transfer = await this.sim.web("GET", "/api/remote/pull?after=" + next);
            next = transfer.next;
            for (const line of transfer.lines) this.output.appendLine(line);
            this.busy = `Copying from ${from}: ${transfer.restored + transfer.failed} of ${objects.length} files, ${count(transfer.records)} records`;
            state.storage = transfer.storage;
            this.render();
            if (!transfer.running) break;
            await new Promise(resolve => setTimeout(resolve, 1000));
          }
          if (transfer.message) this.output.appendLine(transfer.message);
          result = { ok: result.ok && transfer.ok, message: transfer.message || "" };
        } catch (error) {
          result = { ok: false, message: error.message };
          break;
        }
      }
    });
    this.copying = false;
    this.busy = "";
    if (result.ok) vscode.window.setStatusBarMessage(`Sim i: ${result.message || "copied"}`, 6000);
    else vscode.window.showErrorMessage(`Sim i: ${result.message || "the copy didn't finish"}`);
    // With its files here, the program is checked again: what was missing is no longer a problem.
    await this.load(state, true, true);
    await this.find(true);
  }

  async editFilter(node) {
    const state = this.current;
    if (!state || !node || !node.r) return;
    const value = await vscode.window.showInputBox({
      title: `Sim i: copy only some of ${node.r.name}'s records`, prompt: "Only records where (an SQL condition), or empty for all of them",
      placeHolder: "CUSTNO > 1000", value: state.filters[key(node.r)] || "", ignoreFocusOut: true,
    });
    if (value === undefined) return;
    state.filters[key(node.r)] = value.trim();
    this.render();
  }

  /** Every file that can be copied checked (to copy them all again: a copy replaces what's on Sim i), or none. */
  checkAll(on) {
    const state = this.current;
    if (!state || !state.remote) return;
    state.chosen.clear();
    if (on) for (const r of state.remote.files) if (r.receivable) state.chosen.add(key(r));
    state.touched = true;
    this.render();
  }

  checked(items) {
    const state = this.current;
    if (!state) return;
    for (const [node, checkbox] of items) {
      if (!node.r) continue;
      if (checkbox === vscode.TreeItemCheckboxState.Checked) state.chosen.add(key(node.r)); else state.chosen.delete(key(node.r));
    }
    state.touched = true;
    this.render();
  }

  // ---- what the view shows ----

  render() {
    const state = this.current;
    const view = this.view;
    view.title = state ? `Files of ${state.program}` : "Files of This Program";
    view.description = state && state.library ? state.library : "";
    view.message = this.message();
    vscode.commands.executeCommand("setContext", "simI.files.remote", !!(state && state.remote));
    vscode.commands.executeCommand("setContext", "simI.files.withData", this.withData);
    vscode.commands.executeCommand("setContext", "simI.files.copying", this.copying);
    const receivable = state && state.remote ? state.remote.files.filter(r => r.receivable) : [];
    vscode.commands.executeCommand("setContext", "simI.files.allChecked", receivable.length > 0 && receivable.every(r => state.chosen.has(key(r))));
    this.changed.fire();
  }

  message() {
    const state = this.current;
    if (!state) return "Open an RPG source file to see the files its program uses.";
    if (this.busy) return this.busy;
    if (state.message) return state.message;
    if (!state.loaded) return "";
    const database = state.files.filter(f => f.database);
    const missing = database.filter(f => !f.kind).length;
    const devices = state.files.length - database.length;
    let text = state.files.length === 0 ? "The program uses no files."
      : `${plural(database.length, "database file")}${missing ? `, ${count(missing)} not on Sim i` : database.length ? ", all on Sim i" : ""}`
        + (devices ? `; ${plural(devices, "display or printer file")} (made by compiling their source)` : "") + ".";
    if (!state.remote) return text + (database.length ? " Find (search icon) looks for them on the IBM i." : "");
    const picked = state.remote.files.filter(r => r.receivable && state.chosen.has(key(r)));
    const need = picked.reduce((sum, r) => sum + (this.withData ? r.estimate : r.bare), 0);
    const free = state.storage ? state.storage.free : null;
    if (picked.length === 0) return text + " Check the files to copy (the double check copies them all, again for those on Sim i)." + (free !== null && free !== undefined ? ` ${bytes(free)} free on Sim i's database server.` : "");
    text = `${plural(picked.length, "file")} checked, ${this.withData ? "with" : "without"} records: about ${bytes(need)}`
      + (Object.values(state.filters).some(Boolean) ? " without their filters (less with them)" : "");
    if (free === null || free === undefined) return text + ". The database server doesn't say how much room it has.";
    return text + `; ${bytes(free)} free on Sim i's database server` + (need > free ? `: ${bytes(need - free)} too much. Filter the big files, or copy without records.` : ".");
  }

  getChildren(node) {
    const state = this.current;
    if (!state || !state.loaded) return [];
    const remote = state.remote ? state.remote.files : null;
    if (!node) {
      const rows = state.files.map(f => ({ kind: "file", f, r: remote && f.database ? remote.find(x => x.name === f.name && !x.neededBy) || null : null }));
      // The physical files the program's logical files are over: not named by the program, but needed for them.
      for (const r of (remote || []).filter(x => x.neededBy)) rows.push({ kind: "needed", r });
      return rows;
    }
    if (node.kind !== "file" && node.kind !== "needed") return [];
    const { f, r } = node;
    const details = [];
    if (f) details.push({ kind: "detail", label: `${f.how}, line ${count(f.line)}`, description: f.use, icon: "go-to-file", line: f.line });
    const here = f ? (f.kind ? hereText(f.kind, f.hereLibrary, f.records) : null) : r.here ? hereText(r.here.kind, r.here.library, r.here.records) : null;
    details.push(here ? { kind: "detail", label: "On Sim i", description: here, icon: "pass" }
      : { kind: "detail", label: "On Sim i", description: f && !f.database ? "not here: compile its source" : "not here", icon: "warning", warn: true });
    if (remote && (!f || f.database)) {
      if (!r) details.push({ kind: "detail", label: `On ${state.remote.host}`, description: "not found there", icon: "circle-slash" });
      else {
        details.push({ kind: "detail", label: `On ${state.remote.host}`, icon: "cloud", description: [r.kind + " in " + r.library,
          hasRecords(r) ? plural(r.records, "record") + (r.deleted ? ` (+${count(r.deleted)} deleted)` : "") : null, bytes(r.size),
          r.members > 1 ? `${r.members} members: the first comes` : null].filter(Boolean).join(" · ") });
        details.push({ kind: "detail", label: "Estimate here", icon: "dashboard", description: r.receivable ? bytes(this.withData ? r.estimate : r.bare) : "not copied" });
        if (r.receivable && hasRecords(r)) details.push({ kind: "filter", r, label: "Only records where", description: state.filters[key(r)] || "all of them (click to filter)", icon: "filter" });
      }
    }
    return details;
  }

  getTreeItem(node) {
    const state = this.current;
    if (node.kind === "file" || node.kind === "needed") {
      const { f, r } = node;
      const name = f ? f.name : r.name;
      const item = new vscode.TreeItem(name, vscode.TreeItemCollapsibleState.Collapsed);
      item.id = `${state.program}|${node.kind}|${name}|${f ? f.database : "needed"}`;
      const onSim = f ? f.kind : r.here;
      item.description = f ? `${f.how} ${f.use}` + (onSim ? "" : " · not on Sim i") : `needed by ${r.neededBy}` + (onSim ? "" : " · not on Sim i");
      const icon = !f || f.database ? "database" : f.use === "printer file" ? "output" : "window";
      item.iconPath = new vscode.ThemeIcon(icon, onSim ? undefined : new vscode.ThemeColor("list.warningForeground"));
      if (r && r.receivable) item.checkboxState = state.chosen.has(key(r)) ? vscode.TreeItemCheckboxState.Checked : vscode.TreeItemCheckboxState.Unchecked;
      if (f) item.command = { command: "simI.files.goto", title: "Go to where it's declared", arguments: [state.uri, f.line] };
      item.tooltip = f ? `${f.name}: ${f.how} ${f.use}, line ${f.line}` : `${r.name}: needed by ${r.neededBy}`;
      return item;
    }
    const item = new vscode.TreeItem(node.label, vscode.TreeItemCollapsibleState.None);
    item.description = node.description;
    item.iconPath = new vscode.ThemeIcon(node.icon, node.warn ? new vscode.ThemeColor("list.warningForeground") : undefined);
    if (node.line) item.command = { command: "simI.files.goto", title: "Go to where it's declared", arguments: [state.uri, node.line] };
    if (node.kind === "filter") {
      item.contextValue = "simIFilter";
      item.command = { command: "simI.files.filter", title: "Filter its records", arguments: [node] };
    }
    return item;
  }
}

/** Makes the view and its commands; returns it, for the editor's button and for saves to reach it. */
function register(context, sim, remote, output) {
  const files = new FilesView(sim, remote, output);
  const view = vscode.window.createTreeView(VIEW, { treeDataProvider: files, manageCheckboxStateManually: true });
  files.view = view;
  const follow = editor => { if (editor && isRpg(editor.document.uri)) files.show(editor.document.uri); };
  context.subscriptions.push(view,
    vscode.window.onDidChangeActiveTextEditor(follow),
    view.onDidChangeVisibility(e => { if (e.visible) follow(vscode.window.activeTextEditor); }),
    view.onDidChangeCheckboxState(e => files.checked(e.items)),
    vscode.commands.registerCommand("simI.files.refresh", () => files.current && files.load(files.current, true, false)),
    vscode.commands.registerCommand("simI.files.find", () => files.find()),
    vscode.commands.registerCommand("simI.files.checkAll", () => files.checkAll(true)),
    vscode.commands.registerCommand("simI.files.uncheckAll", () => files.checkAll(false)),
    vscode.commands.registerCommand("simI.files.copy", () => files.copy()),
    vscode.commands.registerCommand("simI.files.withData", () => { files.withData = true; files.render(); }),
    vscode.commands.registerCommand("simI.files.withoutData", () => { files.withData = false; files.render(); }),
    vscode.commands.registerCommand("simI.files.cancel", () => sim.web("POST", "/api/remote/cancel").catch(() => {})),
    vscode.commands.registerCommand("simI.files.settings", () => vscode.commands.executeCommand("workbench.action.openSettings", "simI.remote")),
    vscode.commands.registerCommand("simI.files.filter", node => files.editFilter(node)),
    vscode.commands.registerCommand("simI.files.goto", async (uri, line) => {
      const position = new vscode.Position(Math.max(0, (line || 1) - 1), 0);
      await vscode.window.showTextDocument(uri, { selection: new vscode.Range(position, position), preview: false, viewColumn: vscode.ViewColumn.One });
    }));
  files.render();
  follow(vscode.window.activeTextEditor);
  return {
    isRpg,
    /** The editor's button: the view, on this file's program, sent to Sim i first so what it reads is the file as it is. */
    async open(uri) {
      await vscode.commands.executeCommand(`${VIEW}.focus`);
      await files.show(uri, { ask: true, reload: true, upload: true });
    },
    /** A file was saved and compiled: its program's files may be others now. */
    saved(uri) {
      const state = files.programs.get(uri.toString());
      if (state && state.loaded && state === files.current && view.visible) files.load(state, false, false);
      else if (state) state.loaded = false;
    },
  };
}

module.exports = { register };
