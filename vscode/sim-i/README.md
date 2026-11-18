# Sim i for VS Code

Edit RPG in your repository, have every save compiled on Sim i, and debug it there.

## Set up

1. Run Sim i with its web and debug ports: `--web-port 8080 --debug-port 4711` (the Docker image and `compose.yaml` do).
2. Install this extension: copy this folder to `~/.vscode/extensions/sim-i.sim-i-0.1.0` and restart VS Code, or build a package with `npx @vscode/vsce package` and install the `.vsix` (Extensions view, "Install from VSIX").
3. Open the folder that holds your source as `LIBRARY/FILE/member.ext` (`MYLIB/QRPGLESRC/CUSTMNT.sqlrpgle`). If that folder isn't the workspace folder, name it in `simI.sourceRoot`.
4. In Settings, set `simI.host` (and the ports, when they aren't 8080 and 4711) and `simI.user`. The password is asked for the first time it's needed and kept in VS Code's secret storage; "Sim i: Sign On" changes the user or the password.

## Use

- **Save** a source file: it's sent to Sim i, checked and compiled. The status bar says how it went and Problems lists what was found. "Sim i: Send a Folder and Compile It" sends a whole library or source file folder, in the order its members depend on each other. `simI.uploadOnSave` turns saving's part off.
- **Related Files** (in the Explorer) follows the file that's open: the display, printer and database files and tables it uses (a table made in an SQL script opens at its `CREATE`), its copybooks, the programs it calls, the modules whose procedures it uses (through a copybook's prototypes, when it calls them), what an SQL script or a module defines, and every file that uses it. Click one to open it; the arrow beside it goes to where the open file uses it. It reads the workspace's source itself, so it needs neither Sim i nor an IBM i, and keeps up as files are saved. Its cloud button copies data from an IBM i: the open program's files (below), or a whole library on Sim i's Receive page.
- **Files of this program** (the database icon above an RPG source file, or the command of that name) lists the files the program uses and what each is on Sim i. "Find them on the IBM i" looks them up on a real IBM i (`simI.remoteHost`, `simI.remoteUser`, `simI.remoteLibraries`; its password is asked for once), with sizes and an estimate of the room each takes on Sim i. Tick the ones to copy, give a big file a filter (`ORDDAT >= 20260101`, or `RRN(A) <= 5000` for its first 5,000 records), and copy them.
- **F5** on an RPG source file calls its program under the debugger. For parameters, or a program other than the open one, add a launch configuration:

  ```json
  { "type": "sim-i", "request": "launch", "name": "Call CUSTRPT", "program": "MYLIB/CUSTRPT", "parms": ["A", 100] }
  ```

- **A program with screens** (one with a display file) runs in your 5250 session: sign on in the terminal (GreenGlow, or any 5250 client) as the same user and leave it on the main menu, then press F5. The program starts there as though you'd typed the `CALL`, and stops at your breakpoints. With no session waiting, the extension opens the terminal (`simI.siteUrl`, the address of Sim i's site in the browser) and the program starts once you sign on. `"terminal": false` runs it in a job of its own instead (it then fails at its first screen), and `"terminal": true` runs a program without a display file in the session too.
- **The terminal**: the Terminal button in the status bar, or "Sim i: Open the Terminal", opens it in the browser; "Sim i: Open Program Profiles" and "Sim i: Receive a Library from an IBM i" open those pages. They need `simI.siteUrl`.
- **Attach** ("Sim i: attach to my 5250 session", `"request": "attach"`) debugs whatever your 5250 sessions run, rather than one program: call programs there and they stop at your breakpoints. `"jobUser": "OTHER"` or `"*ALL"` debugs other users' jobs.
- Breakpoints take a condition, a hit count or a log message (right-click the breakpoint); "Break on Value Change" on a variable stops the program when it changes. The watch list and the debug console take RPG expressions; `eval name = value` in the console changes a variable.

The extension has no code of its own for debugging: Sim i's debug port speaks the Debug Adapter Protocol, and VS Code connects to it directly.
