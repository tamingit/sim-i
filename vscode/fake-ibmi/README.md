# Fake IBM i for VS Code

Edit RPG in your repository, have every save compiled on a fake IBM i, and debug it there.

## Set up

1. Run the fake with its web and debug ports: `--web-port 8080 --debug-port 4711` (the Docker image and `compose.yaml` do).
2. Install this extension: copy this folder to `~/.vscode/extensions/fake-ibmi.fake-ibmi-0.1.0` and restart VS Code, or build a package with `npx @vscode/vsce package` and install the `.vsix` (Extensions view, "Install from VSIX").
3. Open the folder that holds your source as `LIBRARY/FILE/member.ext` (`MYLIB/QRPGLESRC/CUSTMNT.sqlrpgle`). If that folder isn't the workspace folder, name it in `fakeIbmi.sourceRoot`.
4. In Settings, set `fakeIbmi.host` (and the ports, when they aren't 8080 and 4711) and `fakeIbmi.user`. The password is asked for the first time it's needed and kept in VS Code's secret storage; "Fake IBM i: Sign On" changes the user or the password.

## Use

- **Save** a source file: it's sent to the fake, checked and compiled. The status bar says how it went and Problems lists what was found. "Fake IBM i: Send a Folder and Compile It" sends a whole library or source file folder, in the order its members depend on each other. `fakeIbmi.uploadOnSave` turns saving's part off.
- **Files of this program** (the database icon above an RPG source file, or the command of that name) lists the files the program uses and what each is on the fake. "Find them on the IBM i" looks them up on a real IBM i (`fakeIbmi.remoteHost`, `fakeIbmi.remoteUser`, `fakeIbmi.remoteLibraries`; its password is asked for once), with sizes and an estimate of the room each takes on the fake. Tick the ones to copy, give a big file a filter (`ORDDAT >= 20260101`, or `RRN(A) <= 5000` for its first 5,000 records), and copy them.
- **F5** on an RPG source file calls its program under the debugger. For parameters, or a program other than the open one, add a launch configuration:

  ```json
  { "type": "fake-ibmi", "request": "launch", "name": "Call CUSTRPT", "program": "MYLIB/CUSTRPT", "parms": ["A", 100] }
  ```

- **A program with screens** runs in a 5250 session. Start "Fake IBM i: attach to my 5250 session" (`"request": "attach"`), then call the program in a session signed on as the same user: it stops at your breakpoints. `"jobUser": "OTHER"` or `"*ALL"` debugs other users' jobs.
- Breakpoints take a condition, a hit count or a log message (right-click the breakpoint); "Break on Value Change" on a variable stops the program when it changes. The watch list and the debug console take RPG expressions; `eval name = value` in the console changes a variable.

The extension has no code of its own for debugging: the fake's debug port speaks the Debug Adapter Protocol, and VS Code connects to it directly.
