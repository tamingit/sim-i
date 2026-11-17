# Sim i

A development and test emulator for IBM i code. It runs RPG, CL, DDS and embedded SQL against PostgreSQL, serves 5250 sessions and IBM's host servers, takes copies of files from a real IBM i, and lets you edit and debug from a browser or VS Code. It is for developing and testing away from the real system; it does not replace one: compiled objects don't run on it, and what it doesn't support is listed in [docs/rpg-support.md](docs/rpg-support.md).

It speaks TN5250E over Telnet, and five of IBM's host servers: the port mapper, sign-on, remote command, Network Print and the file server. All but the port mapper can also listen for TLS. New here? Start with [docs/getting-started.md](docs/getting-started.md).

It was split out of [GreenGlow Connect](https://github.com/tamingit/greenglow), where it ran the server's and browser's tests and the public demo.

**Status:** it serves the clients it was written for. The next goal is enough fidelity that IBM i Access Client Solutions (ACS) can connect to it and behave as it does with a real IBM i.

## What it does

**5250 display (Telnet, port 2323)**

- **Negotiation:** TN5250E, one option at a time as an IBM i does it: NEW-ENVIRON, then the terminal type, then end of record and binary. It asks for a value only once the client has agreed to the option. A client that refuses NEW-ENVIRON still gets a session.
- **Device names:** it honours the device name a client asks for (`DEVNAME`). Without one, it makes one up. A name already in use gets the Startup Response Record's error code, as on a real system.
- **Screens:**
  - **Sign-on:** checks the user and password against the [users](#users), with an IBM i's messages for an unknown user, a wrong password or a disabled profile. The password field is case-sensitive.
  - **Change Password:** the expired user goes on to this screen, which needs the current password and the new one twice. F3 or F12 goes back to sign-on.
  - **Main menu:**
    - `4`: a list of placeholder items, three pages long, paged with Page Up and Page Down.
    - `5`: [source members](#rpg): list, edit, check and compile them.
    - `132`: a 27 × 132 screen.
    - **Command line:** `CALL`, `CRTBNDRPG`, `CRTSQLRPGI`, `CRTDSPF`, `RUNSQLSTM`, `RUNSQL`, `DLTPGM`, `DLTF`, `WRKMBRPDM`, `STRSEU`, the [CL and build commands](#cl), and any command made with `CRTCMD`. Each runs in the session's job, which keeps its library list, overrides and service programs until sign-off. Any other command says it isn't found.
    - `90` or `signoff`: ends the session.
    - F3: back to the sign-on screen.
- **Logs:** it logs what clients do, but never the contents of a password field.

**Host servers**

Password levels 0 and 1 use DES; 2 and 3 use SHA-1. Sim i runs at level 2.

- **Port mapper (port 449):** tells a client which port a service is on, as JTOpen and ACS ask before connecting to a host server. It knows `as-signon`, `as-rmtcmd`, `as-netprt` and `as-file` when they run, and answers that there's no such service for anything else, including the TLS names (`as-signon-s`). It logs each name asked, so the log shows which services a client wants.
- **Sign-on (port 8476):**
  - **Sign-on:** checks the user and password, and answers with the sign-on dates and the server's CCSID (37). It claims to be IBM i 7.5.
  - **Change password:** checks the old password, and the new one: it can't be empty, too long, or the same as the old one.
  - **Not supported:** profile tokens, Kerberos and multi-factor authentication.
- **Remote command (port 8475):** runs CL commands and calls programs, as ACS does after signing on. The [RPG commands](#rpg) (`CALL` of a program made from source, `CRTBNDRPG`, `RUNSQL` and so on) really run, and a program made from source is called with its parameters as bytes laid out as on an IBM i. A failed one answers return code `0400` (command) or `0500` (program) with no messages; the log says why. Anything else runs nothing: every other command succeeds with no messages, and so does every other program call. A few programs get real answers:
  - `QSYCKUFU` (Check User Function Usage): every function is allowed for every user (the API's `2`; `1` means not allowed). ACS checks its functions this way, such as `QIBM_XE1_5250` for the 5250 emulator.
  - `QGYOLSPL`, `QGYGTLE` and `QGYCLST` (the open list of spooled files): ACS's Printer Output view lists spooled files this way, in format OSPL0300 or OSPL0400, and filters by user. It lists the placeholder files (owned by the main user) and what RPG programs printed (owned by the user who ran them).
  - `QWCRTVCA` (Retrieve Current Attributes): the server job's name.
  - **Any other program:** its output parameters come back filled with zeros. For an IBM API, zeros in the error code parameter mean success.

  The log names each command and program, with the program's parameter sizes and, for `QSYCKUFU`, the function checked. It never logs a command's parameters or other programs' data, which can hold passwords.
- **Network Print (port 8474):** it lists placeholder spooled files and sends their contents. After those come the spooled files RPG programs printed, as *SCS files (see [RPG](#rpg)).
- **File server (port 8473):** the [integrated file system](#ifs) by path, as JTOpen's `IFSFileInputStream` and ACS read it. It exchanges attributes (datastream level 24, path names in UTF-16), lists a folder's entries (a pattern such as `folder/*`, with `*` and `?`, whatever their case), opens a file for reading with its dates and size, reads it, and closes it. A refusal has the IBM i's return code: 2 no such file, 3 no such folder, 4 a folder opened as a file, 6 no such handle, 13 a path that leads out of the IFS. Every layout was checked against a real IBM i. *Not supported yet* (return code 23): opening for writing, and creating files. The log names each request and path, never a file's contents.

<a id="rpg"></a>**RPG, SQL and source**

Sim i runs RPG IV, free-form and fixed-form, against PostgreSQL. It has libraries, and each library is a schema of the database `--database` (or `SIM_I_DATABASE`) names, a connection string such as `Host=127.0.0.1;Database=simi;Username=simi;Password=simi`: its source members, the programs and display files made from them, and the tables SQL works on. `QGPL` is always there; it's the current library, and its schema also holds the users and the spooled files, so it can't be deleted.

- **Libraries:** `WRKLIB` on the 5250 command line (or System Options, option 2) lists them with what each holds (source members, objects and tables): F6 makes one, 4 deletes one after asking. `CRTLIB LIB(NAME)` makes one and `DLTLIB LIB(NAME)` deletes it and its file (on the command line or through the remote command server), and so does `CREATE SCHEMA NAME` in an SQL script. A name is up to 10 letters, digits, `#`, `$`, `@` and `_`. There can be as many as you like.
- **Names:** a program, file or member named without a library is looked for in every library, `QGPL` first and then the rest by name (there's no library list to set). Name the library, as `CALL MYLIB/PROG`, to be sure. SQL does the same: `LIB.TABLE` and `LIB/TABLE` are that library's table, and a table with no library is looked for in each library, and made in `QGPL`. `QTEMP` is the database session's temporary schema, shared by every job and gone when Sim i stops.

- **Getting source in:**
  - **Folder:** `--source-dir` (or `SIM_I_SOURCE_DIR`) reads `.rpgle`, `.sqlrpgle`, `.rpgleinc`, `.dspf`, `.prtf`, `.sql`, `.clle` and `.cmd` files at start-up. Each folder directly in it is a library, made when it's missing, and a folder in that is the source file: `MYLIB/QRPGLESRC/prog.rpgle` is member `PROG` of `MYLIB/QRPGLESRC`. A file directly in a library's folder goes in the source file its type usually has (`QRPGLESRC`, `QDDSSRC` or `QSQLSRC`), and a file directly in the source folder goes to `QGPL`. A member is named for its file. Members that are new or changed are saved, checked and created: copy members first, then SQL (scripts that create tables before scripts that fill them; a script that only drops isn't run), then display and printer files, then RPG programs (a `NOMAIN` module becomes a service program), then CL programs. Commands aren't created: `CRTCMD` names their program. Run again, nothing changes unless a file did.
  - **5250:** option 5 on the main menu is System Options, and its option 3 (or `WRKMBRPDM`) lists members: 2 edits, 4 deletes, 5 shows the diagnostics, 14 compiles; F6 makes a new one. `STRSEU SRCFILE(QRPGLESRC) SRCMBR(NAME)` opens the editor directly. Source lines are 72 columns; line commands are `I` (insert after) and `D` (delete). F3 saves, F12 cancels.
- **Checking:** saving a member checks it as an IBM i's compiler would, and records by line what's wrong, what Sim i can't run, and what it translated or ignored. RPG is checked for syntax, then for names, types, calls and flow (an undefined name, a number given to a character field, a condition that isn't an indicator, the wrong number of parameters, `LEAVE` outside a loop and the like). An error makes nothing, as on an IBM i: `CRTBNDRPG` fails with `RNS9310`, `CRTSQLRPGI` with `SQL9001`, and a program calling a procedure no service program exports isn't bound (`CPF5D04`). A program is still created with lines it can't run; reaching one ends the program with a message naming the line. [docs/rpg-support.md](docs/rpg-support.md) lists what's checked and what isn't supported. `--source-dir DIR --check` does only the checking, prints the findings and exits with code 3 when something is in error or isn't supported.
- **Running:** `CALL PGM(NAME) PARM(...)` on the 5250 command line (the program's screens and `DSPLY` messages appear in the session) or through the remote command server. A program starts fresh on every call, as if it always ended with `*INLR` on.

*RPG supported:* free-form and fixed-form RPG, alone or mixed, with the RPG cycle (a primary file read record by record, control breaks, total-time calculations, O specs printing heading, detail, total and exception lines). Native record I/O on database files (`CHAIN`, `SETLL`, `READE` and the rest) over physical and logical files made from DDS with `CRTPF` and `CRTLF`, and over SQL tables, views and indexes. Data structures are bytes laid out as on an IBM i, so `OVERLAY`, `MOVE` and pointers (`BASED`, `%ADDR`, `%ALLOC`) work. `EXTPROC` prototypes call the integrated file system's C APIs (`open`, `read`, `write`, `stat`, `opendir` ...) on the IFS folder, and `system`, `sleep`, `getenv`. Data areas, `/COPY` of stream files, `SRCSTMF`, `SORTA`, `EVAL-CORR`, `*INZSR`, `*PSSR`, `ON-EXIT`, `SND-MSG`, `DIM(*AUTO)`, compile-time arrays and about 100 built-in functions. Also the PSDS, `infds`, `usropn` files, service programs through `bnddir`, and `QCMDEXC`. A mainline repeats until `*INLR` is on or a `RETURN`, as on an IBM i. What's checked, what runs and what isn't supported is in [docs/rpg-support.md](docs/rpg-support.md).

<a id="web"></a>*Web source editor:* with `--web-port 8080`, a page at http://localhost:8080 lists every member with a dot for its state, and edits and uploads source. Sign on with a user profile of Sim i. Save & compile (Ctrl+S) checks and compiles the member and lists what was found by line: errors (what an IBM i's compiler rejects, or a compile that failed) in red, and what Sim i can't run (and SQL on tables that aren't there) as warnings. Uploading files, or a folder laid out as `--source-dir` reads one (`LIBRARY/FILE/member.ext`), saves and compiles each in the order they depend on each other, and lists each file as error, warning or OK. Changes need the page's own header, so a page on another site can't make them.

<a id="debug"></a>*Debugging RPG from VS Code:* with `--debug-port 4711` Sim i is a debug server (it speaks the Debug Adapter Protocol), and the extension in [`vscode/sim-i`](vscode/sim-i) makes VS Code its debugger, working on the source files of your repository (a folder laid out `LIBRARY/FILE/member.ext`, as `--source-dir` reads one):

- **A program's files:** "Sim i: Files of This Program" (the database icon above an RPG source file) lists the files the program uses: its file declarations, the tables its embedded SQL names and the files its data structures are described by (`EXTNAME`), each with what it is on Sim i and how many records it has there. "Find them on the IBM i" looks for them in the libraries of a real IBM i (`simI.remoteHost`, `remoteUser`, `remoteLibraries`), with each one's records, size and estimated size here, and adds the physical files that the program's logical files are over. The ones not here yet are ticked; each can have a filter ("only records where"), and Copy brings them across, as `SAVRSTOBJ` does, and compiles the program again.
- **Saving compiles:** each source file saved in VS Code is sent to Sim i (through `--web-port`), which saves the member, checks it and compiles it; what it finds is listed under Problems, by line, errors and warnings as the web editor has them. Commands send one file or a whole folder.
- **Launch** calls a program (the one whose source is open, or `"program": "MYLIB/MYPGM"` with `"parms"`) in a job of its own; `DSPLY` goes to the debug console. **Attach** debugs a user's jobs instead: run the program in that user's 5250 session, and the session stops at a breakpoint, its screen waiting, until you continue. That's how a program with a display file is debugged.
- **Breakpoints** on lines, with a condition (an RPG expression: `custno = 1042 and %trim(name) <> ''`), a hit count, or a message to log instead of stopping. **Data breakpoints** ("Break on Value Change" on a variable) stop the program after the statement that changes the value, as IBM i's `WATCH` does. With "Errors that end the program" on, a program stops where an RPG error is about to end it, with its variables still there.
- **Step** into, over and out of procedures and called programs; the call stack shows each program and procedure with its line.
- **Variables:** a procedure's parameters and locals, the program's globals and file fields, and the indicators; data structures open into subfields and arrays into elements, and a value can be changed. The watch list, hovering over a name, and the debug console take any RPG expression (`total + arr(i)`, `%subst(name:1:3)`, `cust.no`); in the console `eval name = value` changes a variable, as on an IBM i.

A debugger signs on with a user profile (the extension asks for the password once and keeps it in VS Code's secret storage); the debug port itself isn't encrypted. Only RPG is debugged: a CL program runs through, and its RPG is stopped in. A program runs the source it was last compiled from, so a breakpoint in a file that's been changed and not saved is off by the lines added. Statements brought in by `/COPY` have their own line numbers, so a breakpoint on a line can stop in a copied statement of the same number. Code-server, or any editor with a Debug Adapter Protocol client, connects the same way.

<a id="ifs"></a>*Integrated file system:* the IFS's root is a folder on the host, `--ifs-dir` (default: `ifs` in the database folder, or a temporary one). Names are found whatever their case, as in the IFS's root file system, and no path leads out of the folder, through `..` or a symbolic link. `/QSYS.LIB/LIB.LIB/FILE.FILE/MBR.MBR` paths are members. It's used by `/COPY` of stream files, `SRCSTMF` on `CRTBNDRPG`, `CRTSQLRPGI`, `CRTRPGMOD`, `CRTBNDCL` and `RUNSQLSTM`, the IFS commands (`MKDIR`, `RMDIR`, `RMVLNK`/`DEL`, `CHGCURDIR`/`CD`, `CPY`, `CPYTOSTMF`, `CPYFRMSTMF`) and RPG's IFS APIs.

<a id="savrst"></a>*Receiving a library from a real IBM i:* `SAVRSTLIB LIB(MYLIB) RMTLOCNAME(real.example.com)` fetches a library from a real system, and `SAVRSTOBJ OBJ(CUSTPF ORD*) LIB(MYLIB) RMTLOCNAME(real.example.com)` some of its objects (names, generic names or `*ALL`; `OBJTYPE(*FILE *DTAARA *MSGF)` and `OMITOBJ(ABC* XYZ)` narrow them; `RSTLIB(OTHER)` puts them in another library). On an IBM i these commands send a library *to* the remote system over ObjectConnect; here they bring it *from* it, over its FTP server (remote commands and binary transfers), as Sim i can't read a save. What comes across is the data and what describes it:

- **Physical files and SQL tables:** their fields, record format, key and records. A file made from DDS becomes a file made from DDS here (generated from its description, kept as the object's source), an SQL table an SQL table, with its long names and identity column. Packed, zoned and binary numbers, EBCDIC by each field's CCSID, dates in the field's format, varying-length fields and null values arrive as they are; deleted records leave their relative record numbers free. A file with several members comes with its first (the one named as the file, when there is one). LOB, DATALINK and ROWID columns are left out. `DATA(*NO)` receives the files without their records, and `WHERE('ORDDAT >= 20260101')` only the records that meet an SQL condition, written as the remote system takes it (the file is `A`, so `WHERE('RRN(A) <= 1000')` is each file's first thousand records).
- **Logical files, SQL indexes and views:** made again over the files received: a logical file from its key, fields and select/omit rules (or, when it has renamed or derived fields, from its DDS source member when the remote object still names one), an index from its key, a view from its definition in the catalog. One whose physical file or tables aren't here isn't made, and is listed.
- **Source physical files:** every member with its source type, checked as any saved member is.
- **Data areas and message files:** their values and message descriptions.
- **Not received:** programs, service programs, display and printer files and every other compiled object: compile them here from their source. Each is counted in the listing.

With the web source editor on (`--web-port`), its **Receive from an IBM i** page (`/receive`) does the same from a browser. Connect and list shows every object of the remote library with its records, its size there and an estimate of the room it takes here, beside the database's size and the room free on its server; choosing objects adds up what they need and says whether it fits before anything is received. Receive then runs in the background, one at a time, showing objects and records done, how much the database grew against the estimate, and each object as it finishes; it can be ended, which leaves the file it was on empty. The estimate is each file's size on the IBM i, less its deleted records, plus a fifth (PostgreSQL's rows, the relative record number and the key's index), and has been within a fifth for files of any size; logical files and views take no room, an index is taken to be as big as it is there. The free room is what `df` says on the database server, which PostgreSQL only runs for a superuser (compose's user is one); otherwise the page says it's unknown.

The command's message says how many objects and records came, and the spooled file `QPSRODSP` lists each object with what happened to it; an object that fails doesn't stop the rest (the command then ends with CPF3773). An object that's already here is replaced. The remote user is `RMTUSER(NAME) RMTPWD('password')` on the command, or better `SIM_I_REMOTE_USER` and `SIM_I_REMOTE_PASSWORD` in the settings, which keeps the password off the command line; that user needs to read the objects, and `DSPFD`, `DSPFFD`, `DSPOBJD` and `RUNSQL`. Sim i works in that session's QTEMP only and changes nothing else on the remote system. The session is encrypted when the remote FTP server offers TLS (`SECCNN(*SSL)` insists on it, `*NONE` doesn't try; the server's certificate isn't checked); when it doesn't, the password and the data cross the network in the clear, as with any FTP. `PORT(21)` names another port. A listing that one damaged file stops is asked again by first letter and then file by file, so one bad file costs time, not the library.

*SQL:* `exec sql` runs on PostgreSQL. Db2 for i syntax is translated where it differs: library qualifiers (`LIB/TABLE` and `LIB.TABLE`) become the library's schema, and QTEMP the session's temporary one; `CONCAT`, `CURRENT DATE` and the other special registers (local time, as Db2's), `WITH UR` and the other isolation clauses, `FOR READ ONLY`, `RRN()` (each table has a hidden `qsim_rrn` column for it), host variables and indicators, `GET DIAGNOSTICS ... ROW_COUNT`, `CREATE OR REPLACE TABLE`, identity columns (`IDENTITY_VAL_LOCAL()` from the table's own), row change timestamps (`GENERATED ALWAYS FOR EACH ROW ON UPDATE AS ROW CHANGE TIMESTAMP`, kept by a trigger to the microsecond), `WITH DEFAULT`, date arithmetic with labeled durations (`CURRENT DATE - 30 DAYS`, `DUEDATE + (n) MONTHS`, microseconds too), function-style casts (`INT(x)`, `DEC(x, 7, 2)`, `CHAR(x)`), `VALUE`, `VALUES 1, 2, 3` as three rows, `SET SCHEMA` in a script, and `QSYS2.USER_INFO_BASIC`, `SYSTABLES` and `SYSCOLUMNS`. `FETCH FIRST`, `OFFSET`, `MERGE` and the rest of standard SQL PostgreSQL runs as they are. A quoted name in capitals is the same as unquoted, as in Db2. Character comparisons with a literal ignore its trailing blanks, as Db2's do. Db2 scalar functions PostgreSQL lacks (`UCASE`, `DIGITS`, `YEAR`, `DAYS`, `POSSTR` and so on) are in the schema `qsim`. `SQLCODE` and `SQLSTATE` are set from PostgreSQL's SQLSTATE as Db2 sets them for the common cases. Cursors fetch forward only, and read their rows when they're opened. *Not supported, and reported when saving:* the rest of the system catalog and services (`QSYS2.`), `CALL`, `WHERE CURRENT OF`, multiple-row `FETCH`, and stored procedures and functions. There is no commitment control yet: each statement commits, and `COMMIT` and `SET OPTION` are ignored with a note.

*Display files (DDS):* record formats with named fields and constants, input and output fields, option indicators, `CAnn` and `CFnn` keys with response indicators, `DSPATR`, `COLOR`, `ERRMSG`, `EDTCDE`, `DATE(*YY)`, `OVERLAY`, and subfiles (`SFL`, `SFLCTL`, `SFLDSP`, `SFLDSPCTL`, `SFLCLR`, `SFLEND`, `SFLPAG`), including records of more than one line, which page by themselves. `EXFMT`, `WRITE`, `READ`, `READC`, `CHAIN` and `UPDATE` work on them. `DSPSIZ(27 132 *DS4)` files show 27 × 132 on a display that can (a 3477 or 3180 terminal type); opening one on a 24 × 80 display fails, as on an IBM i. Windows (`WINDOW`, `WDWBORDER`, `WDWTITLE`) go over what's on the screen: the file's own records, or, for a file with an `ASSUME` record, what the job last showed. `RTNCSRLOC` returns the record and field the cursor was on; `ALARM` beeps; `PUTOVR` rewrites the record without clearing the screen; an `INVITE` record written and then `READ` by file name waits as long as `CRTDSPF ... WAITRCD(n)` says, then ends with status 1331, so a screen can refresh itself. Help isn't supported. Data with display attributes in it (`x'20'` to `x'3F'`) colors parts of a field, as on a real display.

*Printing:* `dcl-f name printer(width);` and `write name line;` collect lines, and when the program ends they become one spooled file named for the printer file, which the Network Print server lists and serves. A printer file made from DDS with `CRTPRTF` (`PAGESIZE`, `OVRFLW`) is externally described: `dcl-f name printer;` with no length, and `write RECORD;` prints the record's fields at their positions, with `SPACEB`, `SPACEA`, `SKIPB`, `SKIPA`, `PAGNBR`, `DATE`, `TIME` and `EDTCDE`, sets the `oflind` indicator at the overflow line, and starts new pages. `close` (or the program's end) makes the spooled file, named as `OVRPRTF ... SPLFNAME` says.

<a id="cl"></a>*CL:* `CRTBNDCL` makes a program from CLLE source, which runs `PGM`, `DCL` (`*CHAR`, `*DEC`, `*INT`, `*LGL`), `CHGVAR`, `IF`/`ELSE`, `DO`, `DOWHILE`, `DOUNTIL`, `DOFOR`, `SELECT`/`WHEN`/`OTHERWISE`, `LEAVE`, `ITERATE`, `SUBR`/`CALLSUBR`, `MONMSG` (on a command or the program, `CPF0000`-style generic ids), `CALL` with variables by reference, `SNDPGMMSG` (`*ESCAPE` ends the program; `*COMP` shows on the command line; `*STATUS` on the message line) and `RETURN`; expressions with `*EQ` and the like, `*AND`, `*OR`, `*NOT`, `*CAT`, `*BCAT`, `*TCAT`, arithmetic and `%SST`. Any other command runs as if typed, with `&VARIABLES` put in. `CRTCMD CMD(..) PGM(..)` makes a command from CMD source (`PARM` with `TYPE`, `LEN`, `DFT`, `RSTD`, `VALUES`), checked and run as typed: `LIB/CMD value`. The build commands: `CRTSQLRPGI ... OBJTYPE(*MODULE)`, `CRTRPGMOD`, `CRTSRVPGM`, `CRTPGM`, `CRTBNDDIR`, `ADDBNDDIRE`, `CRTPRTF`; and `ADDLIBLE`, `RMVLIBLE`, `OVRPRTF`, `DLTOVR`, `CHKOBJ`. `SNDSMTPEMM` sends nothing: it answers success for an address with an `@`. `GRTOBJAUT` and the like do nothing. `RUNSQLSTM ... ERRLVL(30)` or above doesn't fail for statements on tables that aren't there.

The examples are in `examples/`: `source` (a hello program and a customer list), `customers` (a customer maintenance program with a 50-customer table, a list with search, lookup, create and print), `users` (user maintenance, see [Users](#users)) and `crm` (a CRM of 12 SQLRPGLE programs, a service program, windows, 27 × 132 screens, a dashboard that refreshes itself, printed reports, a CL build and a command; see below).

```bash
dotnet run --project src/SimI -- --source-dir examples/customers --print-port 8474
# then sign on at port 2323 and enter: CALL CUSTMNT
```

The CRM (`examples/crm`, library `CRMDEMO`, described in its README) is built by its own CL program, as on an IBM i. Start Sim i with it, make its three users (System Options, option 1, or SQL on `QSIM_USER`: `CRMUSR1`, `CRMUSR2` and `CRMUSR3`), then on the command line:

```bash
dotnet run --project src/SimI -- --source-dir examples/crm --print-port 8474
# CALL CRMDEMO/CRMBUILD PARM('*YES')    tables, sample data, files, service program, programs, the CRM command
# CRMDEMO/CRM                           the menu; CRMDEMO/CRM *DASH for the dashboard (a 27 x 132 session)
```

<a id="users"></a>**Users**

Every server checks sign-ons against the same users: the 5250 sign-on screen, the sign-on server, Network Print and the remote command server. They're kept in the database, in the table `QGPL.QSIM_USER` (`USERNAME`, `PASSWORD`, `STATUS` `*ENABLED` or `*DISABLED`, `PWDEXP` `*YES` or `*NO`, `TEXT`, `DFTLIB`), so there can be as many as you like. A password changed through any server holds for all of them. These three are there to begin with:

| User | Password | Signing on with the right password |
|---|---|---|
| `PRTUSER` | `Secret12` | Signs on. |
| `EXPUSER` | `Expired1` | The password has expired. The 5250 screen goes on to Change Password; the sign-on server answers `0003000D` until the password is changed through it. |
| `LOCKUSER` | `Locked12` | The profile is disabled, so it can't sign on with any password. The 5250 screen says "User profile LOCKUSER cannot sign on."; the host servers answer `00020002`. |

- **Adding more:** option 5 on the main menu is **System Options**: 1 works with users, 2 with libraries, 3 with source members. Users runs Sim i's own user maintenance program, `USRMNT` (also `WRKUSRPRF`, or `CALL USRMNT`), which is RPG with a display file like any other and is put in `QGPL` at start-up unless it's there (so a copy you change is kept; delete it to get the original back). F6 creates a user, 2 changes one (a blank password keeps the old one) and 4 deletes one. A change holds at the next sign-on. Any SQL on `QSIM_USER` does the same. Passwords are kept as typed, so every server can check them: use placeholders.
- **Default library:** each user has one (`DFTLIB`, `QGPL` to begin with, and it has to be a library that exists, as `QSIM_LIBRARY` lists them; if it's deleted, `QGPL` is used). It's where a program, display file or source member named without a library is looked for first, where the user's new source goes (the library on the new-member screen, and `STRSEU`), and where an SQL `CREATE TABLE` with no library makes its table. A table name with no library in other SQL still goes through the libraries in order, `QGPL` first.
- **The three above:** their names and passwords come from `.env` and are put in the table at each start, as written there, so a change to one of them in the table lasts until the next start. Every other user is left alone.
- **Setting them:** copy `.env.example` to `.env` and change the names and passwords. Sim i reads `.env` from the directory it starts in, or the file `--env-file` names. Variables set in the environment win over the file. For Docker, pass the file with `--env-file .env`, or leave that out to use the defaults.
- **Values:** write them without quotes, as Docker keeps quotes as part of the value. A user name is up to 10 characters. A password can be up to 27 characters on the 5250 sign-on screen, and up to 128 for the host servers.
- **Logs:** Sim i logs who signed on and why a sign-on was refused, but never a password.

**TLS**

As on an IBM i, TLS is a second port for the same server: Telnet on 992, sign-on on 9476, remote command on 9475, Network Print on 9474. A TLS session shares everything with a session on the plain port: device names, and the users. The port mapper stays unencrypted on 449, and tells clients the TLS ports when they ask for `as-signon-s`, `as-rmtcmd-s` or `as-netprt-s`.

- **Certificate:** `--tls-cert` names a PKCS #12 file without a password. If the file doesn't exist, Sim i makes a self-signed certificate there, good for 397 days, and writes its public part beside it as PEM (the same name, ending `.crt`) for clients to trust. Without `--tls-cert`, it makes a certificate that lasts only until it stops.
- **Names:** a certificate Sim i makes is for `localhost`, `127.0.0.1`, `::1`, the machine's name and the `--bind` address. `--tls-name` adds more, such as the name or address clients connect to.
- **Not supported:** client certificates.
- **Logs:** each TLS connection logs its TLS version, or why the handshake failed. A client that doesn't trust the certificate shows up as `certificate unknown`.

## Running it

`docker compose up --build` runs Sim i with every server, the web editor on port 8080, and the PostgreSQL it keeps everything in (see `compose.yaml`).

New here? [docs/getting-started.md](docs/getting-started.md) walks through loading your source, copying data from a real IBM i, and debugging in VS Code.


Docker:

```bash
docker run --rm --env-file .env -e SIM_I_DATABASE="Host=db.example;Database=simi;Username=simi;Password=..." -v sim-i-certs:/certs \
  -p 127.0.0.1:2323:2323 -p 127.0.0.1:992:992 -p 127.0.0.1:8474:8474 -p 127.0.0.1:9474:9474 \
  -p 127.0.0.1:8475:8475 -p 127.0.0.1:9475:9475 -p 127.0.0.1:8476:8476 -p 127.0.0.1:9476:9476 \
  -p 127.0.0.1:449:449 ghcr.io/tamingit/sim-i
```

- **Ports:** publish each port on the same number: the port mapper tells clients the ports inside the container.
- **Database:** the image keeps everything in the PostgreSQL database `SIM_I_DATABASE` names (compose.yaml runs one beside it). To load source, mount a folder with a folder for each library, and name it with `--source-dir` (or `-e SIM_I_SOURCE_DIR=/source`, which also overrides a `.env` that names a folder on the host): `-v "$PWD/examples/customers:/source:ro" ... ghcr.io/tamingit/sim-i --source-dir /source`. The IFS is `/ifs`: mount a named volume there to keep it.
- **Certificate:** the image keeps its certificate in `/certs`. The named volume keeps it from one run to the next, so clients only need to trust it once. Copy out the part to trust with `docker cp <container>:/certs/sim-i.crt .`. Use a named volume rather than a directory: Sim i runs as a non-root user, which can't write to a directory you mount.

With the .NET 10 SDK:

```bash
dotnet run --project src/SimI -- --port 2323 --print-port 8474 --signon-port 8476 --command-port 8475 --file-port 8473 --mapper-port 449
```

Port 449 is below 1024, so outside a container Sim i needs root, or `CAP_NET_BIND_SERVICE`, to listen there.

- **Options:**
  - `--port`: the 5250 port (default 2323).
  - `--print-port`: the Network Print port (default: no Network Print server).
  - `--signon-port`: the sign-on port (default: no sign-on server).
  - `--command-port`: the remote command port (default: no remote command server).
  - `--file-port`: the file server's port (default: no file server).
  - `--mapper-port`: the port mapper's port (default: no port mapper). Clients only ever look for it on 449.
  - `--tls-port`, `--signon-tls-port`, `--command-tls-port`, `--print-tls-port`, `--file-tls-port`: TLS ports for Telnet, sign-on, remote command, Network Print and the file server (default: none). Each but the first needs its server's plain port too.
  - `--tls-cert`: the certificate for the TLS ports (default: one made for this run only).
  - `--tls-name`: another name or address for a certificate Sim i makes. Repeat it, or separate names with commas.
  - `--database`: the PostgreSQL database's connection string (`Host=...;Port=...;Database=...;Username=...;Password=...`). Required, except with `--check`, which makes a database of its own on that server (or the scratch server) and drops it after.
  - `--source-dir`: a folder of source to read at start-up, a folder for each library (see [RPG](#rpg)).
  - `--check`: with `--source-dir`, only check the source, report what's in error or isn't supported and exit (code 3 when something is).
  - `--env-file`: the file of settings, such as the [users](#users) (default: `.env`, when there is one).
  - **Settings:** `.env` also sets the address and every port: `SIM_I_BIND`, `SIM_I_PORT`, `SIM_I_PRINT_PORT`, `SIM_I_SIGNON_PORT`, `SIM_I_COMMAND_PORT`, `SIM_I_FILE_PORT`, `SIM_I_MAPPER_PORT`, the five `SIM_I_*_TLS_PORT`s, `SIM_I_TLS_CERT`, `SIM_I_TLS_NAMES`, `SIM_I_DATABASE`, `SIM_I_SOURCE_DIR`, `SIM_I_IFS_DIR` and `SIM_I_WEB_PORT`. The options above win over them, and a blank port leaves that server off. The image sets its own address and ports, so they don't apply in Docker: publish the ports instead.
  - `--bind`: the address to listen on (default `127.0.0.1`).
  - `--trace`: logs each Telnet negotiation step, and each record's length. It never logs a record's contents, or the value of a NEW-ENVIRON variable that can hold a secret (`IBMSUBSPW`, `IBMRSEED`).
- **Listening address:** by default it listens on loopback only. The image passes `--bind 0.0.0.0` so it can be reached from outside the container. Publish it on `127.0.0.1`, as above, unless you mean other machines to reach it.

## Connecting with IBM i Access Client Solutions

ACS doesn't only use the 5250 port. It signs on to the sign-on server first, for example to check the password or to bypass the 5250 sign-on screen, and it finds that server through the port mapper on port 449. After signing on, it uses the remote command server. So Sim i needs all four: Telnet, sign-on, remote command and the port mapper.

1. Start Sim i where the PC running ACS can reach it, with the trace on. Docker is easiest, because it can listen on 449 without root (arguments after the image name are added to the image's own):
   ```bash
   docker run --rm --env-file .env -p 2323:2323 -p 8474:8474 -p 8475:8475 -p 8476:8476 -p 449:449 ghcr.io/tamingit/sim-i:beta --trace
   ```
   Or, as root or with `CAP_NET_BIND_SERVICE`:
   ```bash
   dotnet run --project src/SimI -- --port 2323 --signon-port 8476 --command-port 8475 --mapper-port 449 --bind 0.0.0.0 --trace
   ```
2. In ACS, make a 5250 session with this machine's address as the system, port 2323, and SSL off. For TLS, use port 992 with SSL on, and turn on SSL in the system's connection settings too. ACS must trust Sim i's certificate first: import `sim-i.crt` into ACS's key store, under Tools, then Key Management. If ACS connects by a name or address the certificate isn't for, add it with `--tls-name` and make a new certificate.
3. Sign on as one of the [users](#users), both when ACS asks for a user and password and on the 5250 sign-on screen.

If it doesn't connect, the log shows how far ACS got:

- **`mapper` lines:** each service ACS asked the port mapper for. A service Sim i doesn't run says so; that's what ACS wanted next.
- **`signon` lines:** whether ACS signed on, or why it was refused.
- **`rmtcmd` lines:** each command ACS ran and program it called. Sim i answers them all with success; if ACS still stops, the last one is the likeliest to need a real answer.
- **`TLS handshake failed`:** with `certificate unknown`, ACS doesn't trust the certificate yet.
- **`as-hostcnn-s: no such service here`:** recent clients ask for the host connection server, used for multi-factor authentication. They carry on without it.
- **No `connected` line at all:** ACS never reached the 5250 server. Check the address, the port and the firewall.
- **It stops partway:** the last `<-` and `->` lines show which step the client didn't answer.

## Tests

```bash
sudo docker run -d --name sim-i-dev-pg -e POSTGRES_USER=simi -e POSTGRES_PASSWORD=simi -e POSTGRES_DB=simi -p 127.0.0.1:54329:5432 postgres:17
dotnet test SimI.slnx
```

- **Database:** each test's machine gets a database of its own, copied from a template, on the scratch server: PostgreSQL on 127.0.0.1:54329 (user and password `simi`), or the connection string in `SIM_I_SCRATCH_DATABASE`. The databases are dropped when the tests end. CI runs a PostgreSQL service on that port.
- **A real IBM i:** two tests receive objects from a real system, and are skipped unless it's named: `SIM_I_TEST_REMOTE_HOST`, `_USER` and `_PASSWORD`, with `SIM_I_TEST_REMOTE_OBJECTS="MYLIB/CUSTPF MYLIB/ORD*"` or `SIM_I_TEST_REMOTE_LIBRARY=MYLIB` (and `SIM_I_TEST_REMOTE_DATA=*NO` for no records). `SIM_I_TEST_REMOTE_DATABASE`, a connection string, keeps what they receive.
- **Client:** the tests drive Sim i with the 5250 client from GreenGlow Connect, copied into `tests/SimI.Tests/Client`.
- **Sign-on server:** the tests send requests laid out as JTOpen 21.0.7 builds them. The password substitutes and protected passwords in them were computed by JTOpen itself, so they don't share Sim i's password code.
- **Limitation:** where Sim i and that client share code (Telnet, EBCDIC), a mistake made on both sides can't be caught. Checking against other clients, such as ACS, is part of the fidelity work.

GitHub Actions runs the tests and builds the image on every push.

## Releases

Publishing a GitHub release from main runs the tests, then pushes the image to GitHub's container registry:

| Release tag | Image tags |
|---|---|
| `vX.Y.Z-beta` or `vX.Y.Z-beta.N` | `ghcr.io/tamingit/sim-i:X.Y.Z-beta.N` and `:beta` |
| `vX.Y.Z` | `ghcr.io/tamingit/sim-i:X.Y.Z` and `:latest` |

```bash
gh release create v0.1.0-beta.1 --target main --prerelease --generate-notes   # beta
gh release create v0.1.0 --target main --generate-notes                       # stable
```

Other tags, and releases whose commit isn't on main, are refused.
