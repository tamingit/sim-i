// Sim i for VS Code: source saved in the repo is sent to Sim i, which checks and
// compiles it (what it finds is listed under Problems), and RPG is debugged there through the
// Sim i's debug port, which speaks the Debug Adapter Protocol itself: this extension only says
// where it is, who signs on, and which files the workspace has.
"use strict";

const vscode = require("vscode");
const path = require("path");
const filesPanel = require("./filesPanel");
const relatedView = require("./relatedView");

/** The source types Sim i reads, by file extension. */
const SOURCE = new Set([".rpgle", ".sqlrpgle", ".rpgleinc", ".dspf", ".prtf", ".pf", ".lf", ".sql", ".clle", ".clp", ".cmd", ".table", ".view", ".index", ".cpy", ".msgf"]);
const DEBUGGED = "**/*.{rpgle,sqlrpgle,RPGLE,SQLRPGLE,Rpgle,Sqlrpgle}";

let secrets, diagnostics, output, cookie = null;

function settings() {
  const config = vscode.workspace.getConfiguration("simI");
  return {
    host: config.get("host") || "localhost", debugPort: config.get("debugPort") || 4711, webPort: config.get("webPort") || 8080,
    user: (config.get("user") || "").trim().toUpperCase(), uploadOnSave: config.get("uploadOnSave") !== false, sourceRoot: config.get("sourceRoot") || "",
    siteUrl: (config.get("siteUrl") || "").trim().replace(/\/+$/, ""),
  };
}

/** Opens a page of Sim i's site in the browser: the terminal (GreenGlow), the profiles, receiving from an IBM i. */
function openSite(page) {
  const site = settings().siteUrl;
  if (!site) {
    vscode.window.showInformationMessage("Sim i: set simI.siteUrl to the address Sim i's site has in the browser (https://sim-i.example.com) to open its pages from here.");
    return;
  }
  return vscode.env.openExternal(vscode.Uri.parse(site + page));
}

/** The folder laid out LIBRARY/FILE/member.ext: the setting, or the workspace folder the file is in. */
function sourceRoot(uri) {
  const folder = (uri && vscode.workspace.getWorkspaceFolder(uri)) || (vscode.workspace.workspaceFolders || [])[0];
  const configured = settings().sourceRoot;
  if (configured && path.isAbsolute(configured)) return configured;
  if (!folder) return null;
  return configured ? path.join(folder.uri.fsPath, configured) : folder.uri.fsPath;
}

const secretKey = (host, user) => `simI.password:${host}:${user}`;

/** Who signs on: the user from the settings (asked for once) and the password from secret storage (asked for once). */
async function credentials(ask = true, user = settings().user) {
  const { host } = settings();
  if (!user) {
    if (!ask) return null;
    user = ((await vscode.window.showInputBox({ title: "Sim i", prompt: "User profile to sign on to Sim i with", ignoreFocusOut: true })) || "").trim().toUpperCase();
    if (!user) return null;
    await vscode.workspace.getConfiguration("simI").update("user", user, vscode.ConfigurationTarget.Global);
  }
  let password = await secrets.get(secretKey(host, user));
  if (!password) {
    if (!ask) return null;
    password = await vscode.window.showInputBox({ title: "Sim i", prompt: `Password of ${user} on ${host}`, password: true, ignoreFocusOut: true });
    if (!password) return null;
    await secrets.store(secretKey(host, user), password);
  }
  return { user, password };
}

async function forget(user) {
  cookie = null;
  if (user) await secrets.delete(secretKey(settings().host, user));
}

// ---- sending source ----

/** A request to Sim i's web port, signed on (once, and again when the session has run out). */
async function web(method, url, body, contentType, again = true) {
  const { host, webPort } = settings();
  const base = `http://${host}:${webPort}`;
  if (!cookie) {
    const who = await credentials();
    if (!who) throw new Error("Not signed on to Sim i.");
    const response = await fetch(base + "/api/signon", { method: "POST", headers: { "X-Sim-i": "1", "Content-Type": "application/json" }, body: JSON.stringify(who) });
    if (!response.ok) {
      await forget(who.user);
      throw new Error(((await response.json().catch(() => null)) || {}).message || `Sim i didn't sign ${who.user} on.`);
    }
    cookie = (response.headers.getSetCookie ? response.headers.getSetCookie() : [response.headers.get("set-cookie") || ""]).map(c => c.split(";")[0]).filter(Boolean).join("; ");
  }
  const headers = { "X-Sim-i": "1", Cookie: cookie };
  if (contentType) headers["Content-Type"] = contentType;
  const response = await fetch(base + url, { method, headers, body });
  if (response.status === 401 && again) {
    cookie = null;
    return web(method, url, body, contentType, false);
  }
  const text = await response.text();
  const data = text ? JSON.parse(text) : null;
  if (!response.ok) throw new Error((data && data.message) || response.statusText);
  return data;
}

/** The library a source file is in: its first folder under the source root (LIBRARY/FILE/member.ext), or null. */
function libraryOf(uri) {
  const root = sourceRoot(uri);
  if (!root) return null;
  const parts = path.relative(root, uri.fsPath).split(path.sep);
  return parts.length >= 2 && !parts[0].startsWith("..") ? parts[0].toUpperCase() : null;
}

// ---- the real IBM i that files are copied from ----

function remoteSettings() {
  const config = vscode.workspace.getConfiguration("simI");
  return {
    host: (config.get("remoteHost") || "").trim(), user: (config.get("remoteUser") || "").trim().toUpperCase(),
    libraries: (config.get("remoteLibraries") || []).map(l => String(l).trim().toUpperCase()).filter(Boolean),
  };
}

const remoteKey = set => `simI.remotePassword:${set.host}:${set.user}`;

/** How to sign on to the real IBM i, asking once for whatever the settings don't have; the password is kept in secret storage. */
async function remoteLogin() {
  const config = vscode.workspace.getConfiguration("simI");
  const ask = async (setting, prompt, list) => {
    const value = ((await vscode.window.showInputBox({ title: "Sim i: the real IBM i", prompt, ignoreFocusOut: true })) || "").trim();
    if (!value) return false;
    await config.update(setting, list ? value.toUpperCase().split(/[ ,]+/).filter(Boolean) : value, vscode.ConfigurationTarget.Global);
    return true;
  };
  if (!remoteSettings().host && !(await ask("remoteHost", "Host name or address of the real IBM i to copy files from"))) return null;
  if (!remoteSettings().user && !(await ask("remoteUser", "User profile on the real IBM i"))) return null;
  if (remoteSettings().libraries.length === 0 && !(await ask("remoteLibraries", "Libraries to look for the files in, in order (ERPDATA ERPSRC)", true))) return null;
  const set = remoteSettings();
  let password = await secrets.get(remoteKey(set));
  if (!password) {
    password = await vscode.window.showInputBox({ title: "Sim i: the real IBM i", prompt: `Password of ${set.user} on ${set.host}`, password: true, ignoreFocusOut: true });
    if (!password) return null;
    await secrets.store(remoteKey(set), password);
  }
  return { host: set.host, user: set.user, password, libraries: set.libraries };
}

const isSource = uri => uri.scheme === "file" && SOURCE.has(path.extname(uri.fsPath).toLowerCase());

/** The file's path as Sim i reads an uploaded folder: the folder's name, then LIBRARY/FILE/member.ext. */
function uploadPath(uri) {
  const root = sourceRoot(uri);
  if (!root) return null;
  const relative = path.relative(root, uri.fsPath);
  if (relative.startsWith("..") || path.isAbsolute(relative)) return null;
  return ["src", ...relative.split(path.sep)].join("/");
}

/** Sends files to Sim i, which saves, checks and compiles them in the order they depend on each other, and shows what it found. */
async function upload(uris, quiet) {
  const form = new FormData();
  const byPath = new Map();
  for (const uri of uris) {
    const name = uploadPath(uri);
    if (!name) continue;
    const open = vscode.workspace.textDocuments.find(d => d.uri.toString() === uri.toString());
    const text = open ? open.getText() : Buffer.from(await vscode.workspace.fs.readFile(uri)).toString("utf8");
    form.append("files", new Blob([text]), name);
    byPath.set(name, uri);
  }
  if (byPath.size === 0) {
    if (!quiet) vscode.window.showWarningMessage("Sim i: the file isn't source under the source folder (LIBRARY/FILE/member.ext).");
    return;
  }
  let results;
  try {
    results = await web("POST", "/api/upload", form);
  } catch (error) {
    vscode.window.showErrorMessage(`Sim i: ${error.message}`);
    return;
  }
  const counts = { ok: 0, warning: 0, error: 0 };
  for (const result of results) {
    counts[result.status] = (counts[result.status] || 0) + 1;
    const uri = byPath.get(result.path);
    output.appendLine(`${result.member || result.path}: ${result.status}${result.message ? " - " + result.message : ""}`);
    if (!uri) continue;
    const found = (result.diagnostics || []).map(d => {
      const line = Math.max(0, (d.line || 1) - 1);
      const severity = d.severity === "error" ? vscode.DiagnosticSeverity.Error : d.severity === "info" ? vscode.DiagnosticSeverity.Information : vscode.DiagnosticSeverity.Warning;
      const diagnostic = new vscode.Diagnostic(new vscode.Range(line, 0, line, 200), d.message, severity);
      diagnostic.source = "Sim i";
      return diagnostic;
    });
    // A compile that failed with nothing found by line still shows on the file.
    if (result.status === "error" && !found.some(d => d.severity === vscode.DiagnosticSeverity.Error)) {
      const diagnostic = new vscode.Diagnostic(new vscode.Range(0, 0, 0, 200), result.message || "Not compiled.", vscode.DiagnosticSeverity.Error);
      diagnostic.source = "Sim i";
      found.push(diagnostic);
    }
    diagnostics.set(uri, found);
  }
  const only = results.length === 1 ? results[0] : null;
  const text = only ? `${only.member || only.path}: ${only.status === "ok" ? "compiled" : only.status}${only.status === "ok" ? "" : " - " + (only.message || "")}`
    : `${results.length} files: ${counts.ok || 0} compiled, ${counts.warning || 0} with warnings, ${counts.error || 0} in error`;
  if (counts.error && !quiet) vscode.window.showErrorMessage(`Sim i: ${text}`);
  else vscode.window.setStatusBarMessage(`Sim i: ${text}`, 6000);
}

// ---- debugging ----

class Configurations {
  /** A launch or attach configuration with what Sim i's debug server needs: where it is, who signs on, and the workspace's source files. */
  async resolveDebugConfigurationWithSubstitutedVariables(folder, config) {
    const editor = vscode.window.activeTextEditor;
    if (!config.type && !config.request) {
      // F5 with no launch.json: call the program of the file that's open.
      if (!editor || !isSource(editor.document.uri)) {
        vscode.window.showInformationMessage("Sim i: open an RPG source file to debug its program.");
        return undefined;
      }
      config = { type: "sim-i", request: "launch", name: "Sim i: call this program" };
    }
    const set = settings();
    config.host = config.host || set.host;
    config.port = config.port || set.debugPort;
    const who = await credentials(true, (config.user || set.user || "").toUpperCase());
    if (!who) return undefined;
    config.user = who.user;
    config.password = who.password;
    const root = config.sourceRoot || sourceRoot(editor && editor.document.uri) || (folder && folder.uri.fsPath);
    if (root) {
      config.sourceRoot = root;
      const files = await vscode.workspace.findFiles(DEBUGGED, "**/{node_modules,.git}/**", 50000);
      config.files = files.map(f => path.relative(root, f.fsPath)).filter(f => !f.startsWith("..") && !path.isAbsolute(f));
    }
    if (config.request === "launch" && !config.program && editor && isSource(editor.document.uri)) config.sourceFile = editor.document.uri.fsPath;
    return config;
  }
}

class Adapter {
  /** Sim i is the debug adapter: VS Code talks to its debug port directly. */
  createDebugAdapterDescriptor(session) {
    return new vscode.DebugAdapterServer(session.configuration.port, session.configuration.host);
  }
}

function activate(context) {
  secrets = context.secrets;
  diagnostics = vscode.languages.createDiagnosticCollection("sim-i");
  output = vscode.window.createOutputChannel("Sim i");
  relatedView.register(context, uri => sourceRoot(uri));
  context.subscriptions.push(diagnostics, output,
    vscode.debug.registerDebugConfigurationProvider("sim-i", new Configurations()),
    vscode.debug.registerDebugAdapterDescriptorFactory("sim-i", new Adapter()),
    // A sign-on Sim i refuses is forgotten, so the next try asks for the password again.
    vscode.debug.registerDebugAdapterTrackerFactory("sim-i", {
      createDebugAdapterTracker: session => ({
        onDidSendMessage: message => {
          if (message.type === "response" && !message.success && (message.command === "launch" || message.command === "attach")) forget(session.configuration.user);
        },
      }),
    }),
    // A program with screens runs in a 5250 session: with none waiting at a command line, Sim i asks for the terminal.
    vscode.debug.onDidReceiveDebugSessionCustomEvent(e => {
      if (e.session.type === "sim-i" && e.event === "simI.needTerminal") openSite("/terminal/");
    }),
    vscode.commands.registerCommand("simI.openTerminal", () => openSite("/terminal/")),
    // Data from a real IBM i: the open program's files (Files of This Program), or else a whole library (the Receive page).
    vscode.commands.registerCommand("simI.copyData", () => {
      const editor = vscode.window.activeTextEditor;
      if (editor && [".rpgle", ".sqlrpgle"].includes(path.extname(editor.document.uri.fsPath).toLowerCase())) return vscode.commands.executeCommand("simI.programFiles", editor.document.uri);
      return openSite("/receive");
    }),
    vscode.commands.registerCommand("simI.openProfiles", () => openSite("/profile")),
    vscode.commands.registerCommand("simI.openReceive", () => openSite("/receive")),
    terminalButton(),
    vscode.workspace.onDidSaveTextDocument(document => {
      if (settings().uploadOnSave && isSource(document.uri) && uploadPath(document.uri)) upload([document.uri], true);
    }),
    vscode.workspace.onDidChangeConfiguration(e => {
      if (e.affectsConfiguration("simI")) cookie = null;
    }),
    vscode.commands.registerCommand("simI.uploadFile", uri => {
      const target = uri || (vscode.window.activeTextEditor && vscode.window.activeTextEditor.document.uri);
      if (target) return upload([target], false);
    }),
    vscode.commands.registerCommand("simI.uploadFolder", async uri => {
      const folder = uri || ((await vscode.window.showOpenDialog({ canSelectFolders: true, canSelectFiles: false, defaultUri: (vscode.workspace.workspaceFolders || [])[0]?.uri, openLabel: "Send to Sim i" })) || [])[0];
      if (!folder) return;
      const files = (await vscode.workspace.findFiles(new vscode.RelativePattern(folder, "**/*"), "**/{node_modules,.git}/**", 50000)).filter(isSource);
      if (files.length === 0) return vscode.window.showInformationMessage("Sim i: there's no source in that folder.");
      await vscode.window.withProgress({ location: vscode.ProgressLocation.Notification, title: `Sim i: sending ${files.length} files` }, async progress => {
        // In lots, so no one request is too big; each lot compiles in the order its files depend on each other.
        for (let at = 0; at < files.length; at += 200) {
          progress.report({ message: `${Math.min(at + 200, files.length)} of ${files.length}`, increment: 200 / files.length * 100 });
          await upload(files.slice(at, at + 200), true);
        }
      });
      output.show(true);
    }),
    vscode.commands.registerCommand("simI.programFiles", async uri => {
      const target = uri || (vscode.window.activeTextEditor && vscode.window.activeTextEditor.document.uri);
      if (!target || ![".rpgle", ".sqlrpgle"].includes(path.extname(target.fsPath).toLowerCase())) return vscode.window.showInformationMessage("Sim i: open an RPG source file to see its program's files.");
      // Sim i reads the files from the member, so it has to be there as it is here.
      if (uploadPath(target)) await upload([target], true);
      return filesPanel.show(context, target, { web, libraryOf, recompile: () => (uploadPath(target) ? upload([target], true) : undefined) }, { settings: remoteSettings, login: remoteLogin, forget: () => secrets.delete(remoteKey(remoteSettings())) });
    }),
    vscode.commands.registerCommand("simI.signOn", async () => {
      await forget(settings().user);
      await vscode.workspace.getConfiguration("simI").update("user", "", vscode.ConfigurationTarget.Global);
      if (await credentials()) vscode.window.setStatusBarMessage("Sim i: signed on", 4000);
    }));
}

/** The status bar's way to the terminal, where programs with screens run. */
function terminalButton() {
  const item = vscode.window.createStatusBarItem(vscode.StatusBarAlignment.Left, 0);
  item.text = "$(terminal) Terminal";
  item.tooltip = "Open the 5250 terminal (GreenGlow): programs with screens run there when you debug them";
  item.command = "simI.openTerminal";
  const show = () => (settings().siteUrl ? item.show() : item.hide());
  show();
  vscode.workspace.onDidChangeConfiguration(e => { if (e.affectsConfiguration("simI.siteUrl")) show(); });
  return item;
}

function deactivate() {}

module.exports = { activate, deactivate };
