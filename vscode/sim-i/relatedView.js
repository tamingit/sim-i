// The Related Files view (in the Explorer): for the source file open, what it uses (display, printer and database
// files and tables, copybooks, programs it calls, the modules whose procedures it uses) and what uses it, each a
// click away. It reads the workspace's source itself (related.js), so it needs neither Sim i nor an IBM i, and keeps
// up as files are saved, added and deleted.
"use strict";

const vscode = require("vscode");
const path = require("path");
const { read, READERS, OBJECTS } = require("./related");

const EXTENSIONS = [...new Set([...Object.keys(READERS), ...Object.keys(OBJECTS)])].map(e => e.slice(1));
const GLOB = `**/*.{${EXTENSIONS.flatMap(e => [e, e.toUpperCase()]).join(",")}}`;
const EXCLUDE = "**/{node_modules,.git}/**";

const GROUPS = [
  { id: "display", label: "Display files", icon: "window" },
  { id: "printer", label: "Printer files", icon: "output" },
  { id: "database", label: "Database files and tables", icon: "database" },
  { id: "include", label: "Copybooks", icon: "file-code" },
  { id: "program", label: "Programs it calls", icon: "run" },
  { id: "procedure", label: "Procedures from", icon: "symbol-function" },
  { id: "defines", label: "Defines", icon: "table" },
  { id: "usedBy", label: "Used by", icon: "references" },
];

class RelatedFiles {
  /** `rootOf(uri)` is the folder laid out LIBRARY/FILE/member.ext that the file is in. */
  constructor(rootOf) {
    this.rootOf = rootOf;
    this.entries = new Map();   // uri string -> { uri, entry }
    this.byDefine = new Map();  // "kind NAME" -> [{ uri, entry, define }]
    this.loading = null;
    this.changed = new vscode.EventEmitter();
    this.onDidChangeTreeData = this.changed.event;
    this.timer = null;
  }

  refresh() {
    clearTimeout(this.timer);
    this.timer = setTimeout(() => this.changed.fire(), 150);
  }

  async load() {
    if (!this.loading) {
      this.loading = (async () => {
        const files = await vscode.workspace.findFiles(GLOB, EXCLUDE, 50000);
        await Promise.all(files.map(uri => this.readFile(uri, false)));
        this.reindex();
      })();
    }
    return this.loading;
  }

  async readFile(uri, reindex = true) {
    try {
      const text = Buffer.from(await vscode.workspace.fs.readFile(uri)).toString("utf8");
      const root = this.rootOf(uri);
      const relative = root ? path.relative(root, uri.fsPath) : path.basename(uri.fsPath);
      this.entries.set(uri.toString(), { uri, entry: read(relative.startsWith("..") ? path.basename(uri.fsPath) : relative, text) });
    } catch {
      this.entries.delete(uri.toString());
    }
    if (reindex) {
      this.reindex();
      this.refresh();
    }
  }

  forget(uri) {
    this.entries.delete(uri.toString());
    this.reindex();
    this.refresh();
  }

  reindex() {
    this.byDefine.clear();
    for (const item of this.entries.values()) {
      for (const define of item.entry.defines) {
        const key = `${define.kind} ${define.name}`;
        if (!this.byDefine.has(key)) this.byDefine.set(key, []);
        this.byDefine.get(key).push({ ...item, define });
      }
    }
  }

  /** Where a reference leads: the definitions of that name and kind, those in its library (or the referrer's) first. */
  resolve(reference, from) {
    const found = (this.byDefine.get(`${reference.kind} ${reference.name}`) || []).filter(d => d.uri.toString() !== from.uri.toString());
    const library = reference.library || from.entry.library;
    const here = found.filter(d => d.define.library === library);
    return here.length ? here : found;
  }

  /**
   * A file's references, with the programs and procedures its copybooks' prototypes name that it calls by those
   * prototypes' names (a copybook of prototypes doesn't make every program that includes it call them all).
   */
  references(item, seen = new Set(), words = item.entry.words) {
    seen.add(item.uri.toString());
    const all = [...item.entry.refs.map(r => ({ ...r, via: null }))];
    for (const include of item.entry.refs.filter(r => r.kind === "include")) {
      for (const target of this.resolve(include, item)) {
        if (seen.has(target.uri.toString())) continue;
        const inner = this.references(target, seen, words).filter(r => (r.kind === "procedure" || r.kind === "program") && r.alias && words.has(r.alias));
        all.push(...inner.map(r => ({ ...r, line: include.line, via: r.via || target.entry.name })));
      }
    }
    // One per thing: what the file names itself wins over what it has through a copybook.
    const once = new Set();
    return all.filter(r => !once.has(`${r.kind} ${r.name}`) && once.add(`${r.kind} ${r.name}`));
  }

  // ---- the tree ----

  getTreeItem(node) {
    return node.item;
  }

  async getChildren(node) {
    if (node) return node.children;
    const editor = vscode.window.activeTextEditor;
    if (!editor || editor.document.uri.scheme !== "file") return [];
    await this.load();
    const current = this.entries.get(editor.document.uri.toString());
    if (!current) return [];
    return this.groups(current).filter(g => g.children.length);
  }

  groups(current) {
    const nodes = Object.fromEntries(GROUPS.map(g => [g.id, []]));
    // What it uses.
    const procedures = new Map();
    const seen = new Set();
    for (const reference of this.references(current)) {
      const targets = this.resolve(reference, current);
      if (reference.kind === "procedure") {
        // Grouped by the module that exports them; a prototype of the program's own procedure isn't shown.
        for (const target of targets) {
          const key = target.uri.toString();
          if (!procedures.has(key)) procedures.set(key, { target, names: [], line: reference.line });
          if (!procedures.get(key).names.includes(reference.name)) procedures.get(key).names.push(reference.name);
        }
        continue;
      }
      const group = reference.kind === "file" ? fileGroup(reference, targets) : reference.kind;
      const key = `${group} ${reference.name}`;
      if (seen.has(key)) continue;
      seen.add(key);
      if (targets.length === 0) nodes[group].push(missing(reference));
      for (const target of targets) nodes[group].push(leaf(reference.name, target, target.define.line, use(reference)));
    }
    for (const { target, names, line } of procedures.values()) {
      nodes.procedure.push(leaf(target.entry.name, target, 1, { line, how: names.join(", ") }, names.length === 1 ? names[0] : `${names.length} procedures`));
    }
    // What it defines, when that's more than itself: an SQL script's tables, a module's exported procedures.
    const own = current.entry.defines.filter(d => !(d.line === 1 && d.name === current.entry.name));
    if (own.length > 1 || (own.length === 1 && current.entry.extension === ".sql")) {
      for (const define of own) nodes.defines.push(leaf(define.name, { ...current, define }, define.line, null, `${define.how} · line ${define.line}`, ""));
    }
    // What uses it: any file with a reference that leads here.
    const users = new Map();
    for (const other of this.entries.values()) {
      if (other.uri.toString() === current.uri.toString()) continue;
      for (const reference of this.references(other)) {
        if (!this.resolve(reference, other).some(t => t.uri.toString() === current.uri.toString())) continue;
        if (!users.has(other.uri.toString())) users.set(other.uri.toString(), { other, reference });
      }
    }
    for (const { other, reference } of [...users.values()].sort((a, b) => a.other.entry.relative.localeCompare(b.other.entry.relative))) {
      nodes.usedBy.push(leaf(path.basename(other.uri.fsPath), { ...other, define: { line: reference.line } }, reference.line, null,
        `${reference.how}${reference.via ? ` (through ${reference.via})` : ""} · line ${reference.line}`, path.dirname(other.entry.relative)));
    }
    return GROUPS.map(g => {
      const children = nodes[g.id];
      const item = new vscode.TreeItem(`${g.label} (${children.length})`, vscode.TreeItemCollapsibleState.Expanded);
      item.iconPath = new vscode.ThemeIcon(g.icon);
      item.id = `group:${g.id}`;
      return { item, children };
    });
  }
}

/** A database, display or printer file: what the declaration says, or what the file it leads to is. */
function fileGroup(reference, targets) {
  const extension = targets.length ? targets[0].entry.extension : "";
  if (extension === ".dspf") return "display";
  if (extension === ".prtf") return "printer";
  return reference.how === "display" || reference.how === "printer" ? reference.how : "database";
}

function use(reference) {
  return { line: reference.line, how: reference.how + (reference.via ? ` (through ${reference.via})` : "") };
}

/** One file to open, at a line. `usedAt` is where the open file refers to it, for "Go to where it's used here". */
function leaf(label, target, line, usedAt, note, folder) {
  const item = new vscode.TreeItem(label, vscode.TreeItemCollapsibleState.None);
  // Its folder (LIBRARY/FILE); a table in an SQL script, the script and the line.
  const where = folder !== undefined ? folder
    : target.entry.extension === ".sql" && line > 1 ? `${path.basename(target.entry.relative)}, line ${line}`
    : path.dirname(target.entry.relative);
  item.description = [note, where].filter(Boolean).join(" · ");
  item.resourceUri = target.uri;
  item.iconPath = vscode.ThemeIcon.File;
  item.tooltip = `${target.entry.relative}${line > 1 ? `, line ${line}` : ""}${usedAt ? `\nUsed here: ${usedAt.how}, line ${usedAt.line}` : ""}`;
  item.command = { command: "simI.related.open", title: "Open", arguments: [target.uri, line] };
  if (usedAt) {
    item.contextValue = "simIRelatedUse";
    item.usedAt = usedAt.line;
  }
  return { item, children: [] };
}

function missing(reference) {
  const item = new vscode.TreeItem(reference.name, vscode.TreeItemCollapsibleState.None);
  const system = /^Q/.test(reference.name) && reference.kind === "program";
  item.description = `${reference.how} · ${system ? "an IBM i program" : "not in the workspace"}`;
  item.iconPath = new vscode.ThemeIcon(system ? "server" : "question");
  item.tooltip = `${reference.name} isn't in the workspace. Used here: ${reference.how}, line ${reference.line}.`;
  item.contextValue = "simIRelatedUse";
  item.usedAt = reference.line;
  return { item, children: [] };
}

/** Registers the view and its commands. `programFiles` opens the Files of This Program panel for a file. */
function register(context, rootOf) {
  const related = new RelatedFiles(rootOf);
  const view = vscode.window.createTreeView("simI.related", { treeDataProvider: related, showCollapseAll: true });
  const watcher = vscode.workspace.createFileSystemWatcher(GLOB);
  const describe = () => {
    const editor = vscode.window.activeTextEditor;
    view.description = editor ? path.basename(editor.document.uri.fsPath) : "";
    view.message = editor && related.entries.size && !related.entries.has(editor.document.uri.toString()) ? "Open a source file to see the files it's related to." : undefined;
  };
  context.subscriptions.push(view, watcher,
    watcher.onDidCreate(uri => related.readFile(uri)),
    watcher.onDidChange(uri => related.readFile(uri)),
    watcher.onDidDelete(uri => related.forget(uri)),
    vscode.window.onDidChangeActiveTextEditor(() => { describe(); related.refresh(); }),
    vscode.commands.registerCommand("simI.related.refresh", () => {
      related.loading = null;
      related.entries.clear();
      related.refresh();
    }),
    vscode.commands.registerCommand("simI.related.open", async (uri, line) => {
      const position = new vscode.Position(Math.max(0, (line || 1) - 1), 0);
      await vscode.window.showTextDocument(uri, { selection: new vscode.Range(position, position), preview: true });
    }),
    vscode.commands.registerCommand("simI.related.showUse", async node => {
      const editor = vscode.window.activeTextEditor;
      const line = node && (node.usedAt || (node.item && node.item.usedAt));
      if (!editor || !line) return;
      const position = new vscode.Position(line - 1, 0);
      editor.selection = new vscode.Selection(position, position);
      editor.revealRange(new vscode.Range(position, position), vscode.TextEditorRevealType.InCenterIfOutsideViewport);
      await vscode.window.showTextDocument(editor.document, editor.viewColumn);
    }));
  related.load().then(() => { describe(); related.refresh(); });
  return related;
}

module.exports = { register };
