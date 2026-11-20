// What a source file refers to, and what it defines, read from its text: the files an RPG or CL program declares
// (display, printer and database files), the tables its embedded SQL names, the files its data structures are
// described by, the copybooks it includes, the programs it calls, the procedures it uses and exports; and the tables,
// views and files that SQL and DDS source define. No VS Code here, so it can be checked on its own.
"use strict";

const NAME = "[A-Za-z#$@§][\\w#$@§]*";

/** A reference: what's referred to (upper case), what kind of thing it is, how, and the line it's on (1-based). */
function ref(kind, name, how, line, library) {
  return { kind, name: name.toUpperCase(), how, line, library: library ? library.toUpperCase() : null };
}

/** The 1-based line of a character offset. */
function lineAt(text, offset) {
  let line = 1;
  for (let i = 0; i < offset; i++) if (text.charCodeAt(i) === 10) line++;
  return line;
}

/** "LIB/NAME" or "NAME": the name and its library. */
function qualified(text) {
  const parts = text.replace(/['"]/g, "").trim().split(/[/.]/);
  return parts.length >= 2 ? { library: parts[parts.length - 2], name: parts[parts.length - 1] } : { library: null, name: parts[0] };
}

/** RPG with its comments blanked out (offsets kept): // to the end of a line, and fixed form's * in column 7. */
function rpgCode(text) {
  return text.split("\n").map(line => {
    if (/^.{6}\*/.test(line) || /^\s*\*\*(?!free)/i.test(line)) return " ".repeat(line.length);
    let quoted = false;
    for (let i = 0; i < line.length; i++) {
      if (line[i] === "'") quoted = !quoted;
      else if (!quoted && line[i] === "/" && line[i + 1] === "/") return line.slice(0, i) + " ".repeat(line.length - i);
    }
    return line;
  }).join("\n");
}

const SQL_TABLE = new RegExp(`\\b(FROM|JOIN|INTO|UPDATE|TABLE)\\s+(?!SELECT\\b|TABLE\\b|LATERAL\\b|FINAL\\b|\\()((?:${NAME}[./])?${NAME})`, "gi");
const NOT_TABLES = new Set(["AS", "SET", "VALUES", "DEFAULT", "CURRENT", "SQLCA", "ONLY", "SYSDUMMY1", "SESSION", "INPUT", "OUTPUT", "DESCRIPTOR", "ROWS", "NEXT", "PRIOR", "FIRST", "LAST"]);

/** The tables embedded SQL names (as Sim i's Files of This Program reads them). */
function sqlTables(code, refs) {
  const statements = /EXEC\s+SQL\b([\s\S]*?)(?:;|END-EXEC)/gi;
  let statement;
  while ((statement = statements.exec(code))) {
    const sql = statement[1];
    const verb = (sql.trim().split(/\s+/)[0] || "").toUpperCase();
    if (["FETCH", "OPEN", "CLOSE", "SET", "PREPARE", "EXECUTE", "GET", "INCLUDE", "WHENEVER", "COMMIT", "ROLLBACK"].includes(verb)) continue;
    let table;
    SQL_TABLE.lastIndex = 0;
    while ((table = SQL_TABLE.exec(sql))) {
      const word = table[1].toUpperCase();
      if (word === "INTO" && !["INSERT", "MERGE"].includes(verb)) continue;
      if (word === "UPDATE" && verb !== "UPDATE") continue;
      const { library, name } = qualified(table[2]);
      const upper = (library || "").toUpperCase();
      if (NOT_TABLES.has(name.toUpperCase()) || ["QTEMP", "SYSIBM", "QSYS2", "SESSION"].includes(upper)) continue;
      refs.push(ref("file", name, "SQL", lineAt(code, statement.index), library));
    }
  }
}

function rpg(text) {
  const code = rpgCode(text);
  const refs = [], defines = [];
  // **FREE source has no columns: an f in column 6 is only code.
  const fixed = !/^\s*\*\*free\b/i.test(text);
  let m;
  // Free-form file declarations: the device says what the file is; EXTFILE or EXTDESC names the real one.
  const dclf = new RegExp(`\\bdcl-f\\s+(${NAME})([^;]*);`, "gi");
  while ((m = dclf.exec(code))) {
    const rest = m[2];
    const device = /\bworkstn\b/i.test(rest) ? "display" : /\bprinter\b/i.test(rest) ? "printer" : "database";
    const external = /\bext(?:file|desc)\s*\(\s*'([^'*][^']*)'/i.exec(rest);
    const { library, name } = external ? qualified(external[1]) : { library: null, name: m[1] };
    refs.push(ref("file", name, device, lineAt(code, m.index), library));
  }
  // Fixed-form F specs: the name in columns 7-16, the device in 36-42.
  if (fixed) code.split("\n").forEach((line, i) => {
    if (!/^.{5}[Ff][^*]/.test(line)) return;
    const name = line.slice(6, 16).trim();
    if (!name || !new RegExp(`^${NAME}$`).test(name)) return;
    const device = line.slice(35, 42).trim().toUpperCase();
    refs.push(ref("file", name, device === "WORKSTN" ? "display" : device === "PRINTER" ? "printer" : "database", i + 1));
  });
  // Copybooks: /COPY and /INCLUDE, as LIB/FILE,MEMBER, FILE,MEMBER, MEMBER or a path.
  const include = /^.{0,6}?\/(copy|include)\s+(\S+)/gim;
  while ((m = include.exec(code))) {
    const target = m[2].replace(/['"]/g, "");
    let library = null, name;
    if (target.includes(",")) {
      const [file, member] = target.split(",");
      name = member;
      if (file.includes("/")) library = file.split("/")[0];
    } else {
      name = target.split("/").pop().replace(/\.[^.]*$/, "");
    }
    refs.push(ref("include", name, `/${m[1].toLowerCase()}`, lineAt(code, m.index), library));
  }
  // Prototypes: EXTPGM calls a program; anything else is a procedure, found where it's exported.
  const dclpr = new RegExp(`\\bdcl-pr\\s+(${NAME})([^;]*);`, "gi");
  while ((m = dclpr.exec(code))) {
    const rest = m[2];
    const pgm = /\bextpgm\b(?:\s*\(\s*'([^']+)'\s*\))?/i.exec(rest);
    const proc = /\bextproc\s*\(\s*'([^']+)'\s*\)/i.exec(rest);
    const line = lineAt(code, m.index);
    // The prototype's own name, which code calls it by.
    const alias = m[1].toUpperCase();
    if (pgm) {
      const { library, name } = qualified(pgm[1] || m[1]);
      refs.push({ ...ref("program", name, "EXTPGM", line, library), alias });
    } else {
      refs.push({ ...ref("procedure", proc ? proc[1] : m[1], "prototype", line), alias });
    }
  }
  // Fixed-form CALL 'PGM'.
  const call = /^.{5}[Cc].{19}\s*CALL\s+'([^']+)'/gim;
  while (fixed && (m = call.exec(code))) {
    const { library, name } = qualified(m[1]);
    refs.push(ref("program", name, "CALL", lineAt(code, m.index), library));
  }
  // Data structures described by a file.
  const extname = /\bextname\s*\(\s*'?([\w#$@§/.]+)'?/gi;
  while ((m = extname.exec(code))) {
    const { library, name } = qualified(m[1]);
    refs.push(ref("file", name, "EXTNAME", lineAt(code, m.index), library));
  }
  sqlTables(code, refs);
  // Exported procedures: what other programs' prototypes find here.
  const exported = new RegExp(`\\bdcl-proc\\s+(${NAME})\\b[^;]*\\bexport\\b`, "gi");
  while ((m = exported.exec(code))) defines.push(ref("procedure", m[1], "exported", lineAt(code, m.index)));
  const fixedExport = new RegExp(`^.{5}[Pp](${NAME})\\s+B\\b.*\\bEXPORT\\b`, "gim");
  while (fixed && (m = fixedExport.exec(code))) defines.push(ref("procedure", m[1], "exported", lineAt(code, m.index)));
  // Every name the code uses, so a copybook's prototypes count only for the programs that call them.
  const words = new Set((code.match(new RegExp(NAME, "g")) || []).map(w => w.toUpperCase()));
  return { refs, defines, words };
}

function cl(text) {
  const code = text.replace(/\/\*[\s\S]*?\*\//g, c => c.replace(/[^\n]/g, " "));
  const refs = [];
  let m;
  const call = new RegExp(`\\bcall\\s+(?:pgm\\s*\\(\\s*)?((?:${NAME}\\/)?${NAME})`, "gi");
  while ((m = call.exec(code))) {
    const { library, name } = qualified(m[1]);
    refs.push(ref("program", name, "CALL", lineAt(code, m.index), library));
  }
  const dclf = new RegExp(`\\bdclf\\s+(?:file\\s*\\(\\s*)?((?:${NAME}\\/)?${NAME})`, "gi");
  while ((m = dclf.exec(code))) {
    const { library, name } = qualified(m[1]);
    refs.push(ref("file", name, "DCLF", lineAt(code, m.index), library));
  }
  return { refs, defines: [] };
}

function sql(text) {
  // Comments and string constants blanked out (offsets kept): their words aren't tables.
  const code = text.replace(/--[^\n]*|'(?:[^']|'')*'/g, c => c.replace(/[^\n]/g, " "));
  const defines = [], refs = [];
  let m;
  const create = new RegExp(`\\bcreate\\s+(?:or\\s+replace\\s+)?(table|view|index|alias)\\s+((?:${NAME}[./])?${NAME})`, "gi");
  while ((m = create.exec(code))) {
    const { library, name } = qualified(m[2]);
    defines.push(ref("file", name, `SQL ${m[1].toLowerCase()}`, lineAt(code, m.index), library));
  }
  // The tables a script fills, reads, changes or drops: those it creates itself aren't references.
  const made = new Set(defines.map(d => d.name));
  SQL_TABLE.lastIndex = 0;
  while ((m = SQL_TABLE.exec(code))) {
    const { library, name } = qualified(m[2]);
    if (made.has(name.toUpperCase()) || NOT_TABLES.has(name.toUpperCase())) continue;
    refs.push(ref("file", name, "SQL", lineAt(code, m.index), library));
  }
  return { refs, defines };
}

function dds(text) {
  const refs = [];
  let m;
  // A logical file's physical files, and fields referred to another file.
  const keyword = new RegExp(`\\b(PFILE|JFILE|REF|REFFLD)\\s*\\(([^)]*)\\)`, "gi");
  while ((m = keyword.exec(text))) {
    const words = m[2].trim().split(/\s+/).filter(Boolean);
    const files = m[1].toUpperCase() === "REFFLD" ? words.slice(1, 2).map(w => w.replace(/^\*SRC$/i, "")) : words;
    for (const file of files) {
      if (!file || file.startsWith("*")) continue;
      const { library, name } = qualified(file);
      refs.push(ref("file", name, m[1].toUpperCase(), lineAt(text, m.index), library));
    }
  }
  return { refs, defines: [] };
}

const READERS = {
  ".rpgle": rpg, ".sqlrpgle": rpg, ".rpgleinc": rpg, ".cpy": rpg, ".rpg": rpg, ".sqlrpg": rpg,
  ".clle": cl, ".clp": cl,
  ".sql": sql,
  ".pf": dds, ".lf": dds, ".dspf": dds, ".prtf": dds,
};

/** What kind of object a member is, by its extension: what other source refers to it as. */
const OBJECTS = {
  ".rpgle": "program", ".sqlrpgle": "program", ".clle": "program", ".clp": "program", ".rpg": "program", ".sqlrpg": "program",
  ".pf": "file", ".lf": "file", ".dspf": "file", ".prtf": "file", ".table": "file", ".view": "file", ".index": "file",
};

/** The name a folder or file (without its extension) gives a library or member: ARCAD writes # as #035. */
const nameFromPath = part => part.replace(/#035/g, "#").toUpperCase();

/**
 * One source file: its member name and library (from LIBRARY/FILE/member.ext), what it refers to, and what it
 * defines: itself (a program, a copybook or a file) and the tables or procedures in it.
 */
function read(relative, text) {
  const parts = relative.split(/[\\/]/).filter(Boolean);
  const file = parts[parts.length - 1];
  const dot = file.lastIndexOf(".");
  const extension = dot > 0 ? file.slice(dot).toLowerCase() : "";
  const name = nameFromPath(dot > 0 ? file.slice(0, dot) : file);
  const library = parts.length >= 2 ? nameFromPath(parts[0]) : null;
  const found = (READERS[extension] || (() => ({ refs: [], defines: [] })))(text.replace(/\r\n/g, "\n"));
  const defines = [...found.defines];
  if (OBJECTS[extension]) defines.unshift(ref(OBJECTS[extension], name, extension.slice(1), 1, library));
  // Any RPG member can be included; a copybook is only that.
  if (READERS[extension] === rpg) defines.push(ref("include", name, extension.slice(1), 1, library));
  for (const d of defines) if (!d.library) d.library = library;
  // One reference per thing, at the line where it's first named.
  const seen = new Set();
  const refs = found.refs.filter(r => {
    const key = `${r.kind} ${r.name}`;
    if (seen.has(key) || (r.kind === "include" && r.name === name)) return false;
    seen.add(key);
    return true;
  });
  return { relative, name, library, extension, refs, defines, words: found.words || new Set() };
}

module.exports = { read, nameFromPath, READERS, OBJECTS };
