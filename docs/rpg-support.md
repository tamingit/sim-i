# RPG: what's checked and what isn't supported

The fake checks RPG source in two ways, as an IBM i's compiler would. What it finds is shown on the member's diagnostics screen, printed by `--check`, and logged when source is imported.

- **Errors**: source an IBM i's compiler rejects. Nothing is made from the member: `CRTBNDRPG` fails with `RNS9310`, `CRTSQLRPGI` with `SQL9001`, and binding with `CPF5D04`.
- **Not supported**: real RPG that the fake can't run. The program is still made, and reaching one of these lines ends it with a message naming the line.

The lists below are checked against ILE RPG on IBM i 7.5. The words the fake knows to be RPG are in `src/FakeIbmI/Rpg/RpgLanguage.cs`. A word in none of those lists is an error, not "not supported".

## Errors

**Syntax**
- A statement with no `;` at the end of the member. A missing `;` in the middle runs two statements together, which is reported where it goes wrong (`N isn't expected here`).
- A block with no end (`ENDIF is missing for the block that starts here`), an end with no block, and a missing `END-DS`, `END-PR`, `END-PI` or `END-PROC`.
- A name that isn't an operation code, such as `ENDSEL` (it's `ENDSL`), `ITERATE` (it's `ITER`), or any other unknown word used as a statement.
- A fixed-form-only operation code in free form (`MOVE`, `ADD`, `Z-ADD`, `CALL`, `GOTO`, `SETON` and the rest), with what to use instead.
- A built-in function that doesn't exist (`%FOO`), and a definition, file, `CTL-OPT` or compiler-directive keyword that doesn't exist.
- A declaration with no type (`dcl-s n;`), or with a type that needs a length and has none (`char`).
- An unclosed literal, a bad date, time, timestamp or hex literal, and characters that aren't RPG (`!`, `&`, `"`, ...).
- `/ELSE`, `/ELSEIF` or `/ENDIF` with no `/IF`, `/IF` with no `/ENDIF`, `/COPY` or `/INCLUDE` of a member that isn't there.
- `ON-ERROR` with anything other than status codes, `*PROGRAM`, `*FILE` or `*ALL`.
- `EXEC SQL` in RPGLE source. It has to be SQLRPGLE, for `CRTSQLRPGI`.
- In fixed form: an operation code that isn't one, a definition type or device that isn't one, detail calculations after total calculations, `MVR` not after `DIV`, an `END` with no block, a `GOTO` with no `TAG`.

**Names**
- A name that isn't defined (RNF7030 on an IBM i): variables, constants, subfields (`ds.field`), procedures, subroutines (`EXSR`), the names `LIKE`, `LIKEDS`, `SFILE`, `INDDS`, `INFDS` and `OFLIND` give, and SQL host variables (SQL0312).
- A name defined twice in one scope, including an unqualified data structure's subfields.
- A record format in `EXFMT`, `WRITE`, `READ`, `READC`, `CHAIN` or `UPDATE` that isn't one of the program's files', and an `SFILE` record that isn't a subfile record.
- A display file (`WORKSTN`), an externally described printer file (`PRINTER` with no length) or a database file (`DISK`) that doesn't exist. `EXTNAME` of a table that doesn't exist; `LIKEREC` of a record format the program's files don't have.
- Indexing something that isn't an array, a qualified name on something that isn't a data structure, and assigning to a constant, a procedure or a `CONST` parameter.

**Types**
- An assignment or `INZ` of the wrong type: characters to a number, a number to characters, characters to a date, and so on. The message says which built-in function converts.
- A comparison of different types (characters with a number, a date with characters).
- A condition (`IF`, `ELSEIF`, `DOW`, `DOU`, `WHEN`) that isn't an indicator, such as `if count;`. `AND`, `OR` and `NOT` on things that aren't indicators.
- Arithmetic on the wrong types, and date minus date (it's `%DIFF`).
- A built-in function given the wrong number of parameters, or the wrong type (`%TRIM(5)`).
- `FOR` with an index, start, limit or `BY` that isn't numeric.

**Calls and flow**
- A call with too many or too few parameters (`OPTIONS(*NOPASS)` parameters may be left off the end).
- A literal or expression passed to a parameter passed by reference (no `CONST` or `VALUE`), or a variable of a different type.
- A `CONST` or `VALUE` parameter given a value of the wrong type.
- A procedure that returns nothing used in an expression, or a procedure called in an expression without `()`.
- A prototype that doesn't match its procedure interface (number of parameters, their types, whether it returns a value).
- `RETURN` with a value in the main procedure or in a procedure that returns nothing; `RETURN` with no value in one that returns something, or with a value of the wrong type.
- `LEAVE` or `ITER` outside a loop, and `LEAVESR` outside a subroutine.
- Binding: a program that calls a procedure through a prototype which neither the program nor any service program has (CPF5D04 on `CRTBNDRPG`, `CRTSQLRPGI` or `CRTPGM`).

**What isn't checked**

These would fail on an IBM i but the fake doesn't catch them yet:
- Lengths: assigning a longer field to a shorter one is fine in RPG, but passing a `char(10)` by reference to a `char(20)` parameter isn't, and the fake only checks the kind of type (characters, number, date).
- A name shared by a record format and a procedure or variable.
- SQL syntax errors in embedded SQL. The SQL is translated for PostgreSQL, and what it won't take is reported as "not supported".
- Binding is looser than an IBM i's. A procedure is found in any service program, not only through `BNDDIR`.
- Warnings the compiler gives (unused names, a procedure with no `RETURN`, and so on).

## What runs

Beyond expressions, declarations and the usual free-form operations:

- **Fixed-form RPG**, alone or mixed with free form. Specs are translated to the free form they mean, so they're checked the same way:
  - H specs, F specs (devices `DISK`, `PRINTER`, `WORKSTN`; types I, O, U and C; P and S designations; keyed files; keywords).
  - D specs (`S`, `C`, `DS`, `PR`, `PI`; from/to positions; long names; external, program-status and data-area structures).
  - P specs.
  - C specs, with conditioning indicators and AN/OR lines, resulting indicators, and the operations that only fixed form has: `Z-ADD`, `ADD`, `SUB`, `MULT`, `DIV` with `MVR` (truncating as fixed form does), `MOVE`, `MOVEL`, `MOVEA`, `IFxx`/`DOWxx`/`DOUxx`/`WHENxx` with `ANDxx`/`ORxx`, `DO`, `CASxx`, `CABxx`, `GOTO`/`TAG`, `KLIST`/`KFLD`, `PLIST`/`PARM` (`*ENTRY` too), `CALL`, `DEFINE`, `LOOKUP`, `SCAN`, `CHECK`, `XLATE`, `SUBST`, `CAT`, `COMP`, `SETON`/`SETOFF`, `TIME`, `ADDDUR`/`SUBDUR`/`EXTRCT`, `TESTN`, `TESTB`, `BITON`, `BITOFF`, `OCCUR` and the file operations.
  - I specs for externally described files: record identifying indicators, field renames (which record I/O and checking use), control levels (L1–L9), field indicators.
  - Extended factor 2 over several lines (and literals continued with `+` or `-`), `PARM` result fields defined with a length after `CALL`, a label on `ENDSR` as a `GOTO`/`CABxx` target, and SQL in `C/EXEC SQL`, `C+` and `C/END-EXEC` lines.
  - Fixed and free form mixed in one member (a fixed `DO` ended by a free `enddo;`, a free `if` ended by a fixed `ENDIF`), a key list named in a free-form `CHAIN` or `SETLL`, `PAGE` to `PAGE7` and `UDATE`, `UDAY`, `UMONTH` and `UYEAR` without a definition, a field defined again by a calculation (result length, `*LIKE DEFINE`, also like `*DATE` or `*INxx`), and `IN`/`OUT`/`UNLOCK` of `*DTAARA`.
  - Program-status and file-information subfields named by keyword (`*PROC`, `*STATUS`, `*ROUTINE`, `*PARMS`, `*FILE`, `*RECORD`, `*OPCODE` ...), in fixed or free form.
  - O specs: heading, detail, total and exception lines on program-described printers (fields at end positions, edit codes and words, blank after, constants, space and skip, `PAGE`, `UDATE`), and record formats written, added or deleted.
- **The RPG cycle:** a program with a mainline repeats it until LR is on, or until a `RETURN`, as on IBM i. A primary file, then secondary files, is read record by record. Control breaks set L1–L9, total-time calculations and total output come before the new record's fields, and `1P` and overflow indicators work.
- **Native record I/O** on database files:
  - The operations: `CHAIN`, `SETLL`, `SETGT`, `READ`, `READE`, `READP`, `READPE`, `WRITE`, `UPDATE` (with `%FIELDS`), `DELETE`, `UNLOCK`, `FEOD`, `OPEN` and `CLOSE`.
  - Search arguments: key lists, `%KDS` and `*LOVAL`/`*HIVAL`/`*START`/`*END`.
  - Result data structures; `%EOF`, `%FOUND` and `%EQUAL` for a file.
  - The file keywords `RENAME`, `PREFIX`, `USAGE`, `EXTFILE`/`EXTDESC`, and `LIKEREC`.
  - A file can be a physical file (`CRTPF` from DDS: fields, key, `UNIQUE`, reference fields with `REF` and `REFFLD`, whose length and decimals can be replaced or changed by `+n`/`-n`; field names with `#`, `@` and `$`; a file of more than PostgreSQL's 1600 columns, such as a field reference file, is made without a table, so its fields can be referred to but it holds no records), a logical file (`CRTLF`: `PFILE`, its own fields and key, `PFILE` over several lines, `RENAME`, derived fields with `SST` and `CONCAT` (input only), more than one record format (each over its own physical file; an operation naming a format works on it, one naming the file on its first format), select/omit with `COMP` (or `CMP`), `VALUES`, `RANGE`), an SQL table or view (keyed by its primary key), or an SQL index.
- **Embedded SQL** beyond single statements: cursors that scroll (`FETCH NEXT`, `PRIOR`, `FIRST`, `LAST`, `BEFORE`, `AFTER`, `CURRENT`, `RELATIVE n`, `ABSOLUTE n`), multiple-row `FETCH ... FOR n ROWS` into a data structure array (`SQLERRD(3)` the rows, `SQLERRD(5)` +100 at the end), dynamic SQL (`PREPARE`, `EXECUTE`, `EXECUTE IMMEDIATE`), and `CALL` of a program (host variables by reference; SQLCODE -204 when it isn't there) or of `QSYS2.QCMDEXC`.
- **SQL tables keep their system names:** a column's `FOR COLUMN` name is the field RPG, DDS and record I/O know it by (SQL uses either), and `RCDFMT` is the table's record format name.
- **Display and printer files** take reference fields (`REF`, `REFFLD`, R in column 29) from the database file, as physical files do.
- **SQL scripts as IBM i generates them** (`RUNSQLSTM`, and SQL members in a source folder, whose unqualified names are in the member's library): `FOR COLUMN`, `CCSID`, a bare `DEFAULT` (the type's default), and `RENAME TABLE ... FOR SYSTEM NAME` (the system name is a view of the table).
- **Source as the compiler reads it:** columns 1 to 5 are ignored, a line with only its spec letter is empty, a directive can follow a spec letter in column 6, names can start with `@`, `#` or `$`, and date and time formats can name their separator (`*USA/`, `*ISO-`, `*YMD.`, `*HMS&`).
- **Data structures are bytes**, with IBM i's layouts (packed, zoned, binary, integer, float, varying, dates as ISO text). Subfields overlay each other (`OVERLAY` with a position or `*NEXT`, `POS` with a number or a named constant), structures move as characters, and a bad zoned or packed value is a decimal-data error.
- **Pointers:** `POINTER`, `BASED`, `%ADDR`, `%ALLOC`, `%REALLOC`, `DEALLOC`, `%STR` (and as a target), arithmetic and `*NULL`.
- **`EXTPROC`** prototypes call procedures of the program or of a service program by their external name, or the fake's own natives:
  - The integrated file system's APIs: `open`, `read`, `write`, `close`, `lseek`, `unlink`, `access`, `mkdir`, `rmdir`, `rename`, `stat`, `opendir`/`readdir`/`closedir`, with IBM i's flag and errno values.
  - `__errno`, `strerror`, `sleep`, `usleep`, `system` and `getenv`.
  - `OPTIONS(*STRING)`, `*OMIT` and `%OMITTED` work with them.
- **The integrated file system:** a host folder, used by stream-file `/COPY` and `/INCLUDE` (relative paths from the including file), by `SRCSTMF` on the compile commands and `RUNSQLSTM`, and by `MKDIR`, `RMDIR`, `RMVLNK`, `CHGCURDIR`, `CPY`, `CPYTOSTMF` and `CPYFRMSTMF`. `/QSYS.LIB/...MBR` paths name members.
- **Data areas:** `IN`, `OUT`, `UNLOCK`, `DTAARA` (`*AUTO`, `*LDA`), and `CRTDTAARA`, `CHGDTAARA`, `DSPDTAARA` and `DLTDTAARA`.
- **More operations and declarations:**
  - Operations: `SORTA` (descending, or by a subfield, `ds(*).key`), `SELECT` with an operand (`WHEN-IS`, `WHEN-IN`), the `IN` operator with `%LIST` and `%RANGE`, `EVAL-CORR`, `TEST`, `ON-EXIT`, `SND-MSG` with `ON-EXCP` and `%MSG`.
  - `*INZSR` runs at start-up, and `*PSSR` handles errors.
  - Declarations: `DIM(*VAR)` and `DIM(*AUTO)`, `OCCURS` with `%OCCUR`, compile-time arrays and tables (`CTDATA`, `PERRCD`, `**CTDATA`, `%TLOOKUP`), `LIKE` with a length adjustment, `PREFIX`, `LIKEREC`, `EXTNAME(file : *KEY)`.
  - `CTL-OPT DATFMT` and `TIMFMT`, float literals and `**=`. `UCS2` and `GRAPH` are held as characters.

## Not supported

**Source and operations**
- `/SET`, `/RESTORE` and `/CHARCOUNT`. `/IF` conditions other than `DEFINED(name)` and `NOT DEFINED(name)`.
- Program-described input (I specs with positions), and program-described `DISK` files.
- Matching records (M1–M9) between primary and secondary files.
- Commitment control (`COMMIT`, `ROLBK`): each statement and record operation commits as it's done.
- `XML-INTO`, `XML-SAX`, `DATA-INTO`, `DATA-GEN`, `FORCE`, `POST`, `ACQ`, `REL`, `NEXT`, `DCL-ENUM`, `CALLB` and fixed form's `TESTZ` and `MxxZO`.

**Built-in functions:** `%DATA`, `%GEN`, `%HANDLER`, `%PARSER`, `%THIS`, `%NULLIND`, `%PADDR`, and `%FIELDS` other than in `UPDATE`.

**Types and definitions**
- `OBJECT`, procedure pointers (`POINTER(*PROC)`), numbers of more than 28 digits.
- Definition keywords: `ALIAS`, `ALT`, `ALTSEQ`, `CLASS`, `EXT`, `EXTFLD`, `EXTFMT`, `FROMFILE`, `LIKEFILE`, `NULLIND`, `OVERLOAD`, `PACKEVEN`, `SAMEPOS`, `SQLTYPE`, `TOFILE`. `EXTPROC(*JAVA ...)`.
- Taken but with no effect: `STATIC`, `TEMPLATE`, `EXPORT` and `IMPORT` on variables, `CCSID`, `ASCEND`, `DESCEND`, `OPDESC`, `RTNPARM`, `NOOPT`, `ALIGN`, `DATFMT` and `TIMFMT` on a variable, `REQPROTO`, and `OPTIONS` other than `*NOPASS`, `*OMIT` and `*STRING`.

**Files**
- `SEQ` and `SPECIAL` files; the file keywords `QUALIFIED`, `HANDLER`, `DATFMT`, `DEVID`, `EXTIND`, `FORMLEN`, `FORMOFL`, `KEYLOC`, `LIKEFILE`, `PASS`, `PGMNAME`, `PLIST`, `PRTCTL`, `RAFDATA`, `RECNO`, `SAVEDS`, `SAVEIND`, `SLN`, `TEMPLATE`, `TIMFMT`; `EXTFILE` with a variable.
- Join logical files (`JFILE`). A descending key field is read in ascending order. Records aren't locked between jobs.
- Taken but with no effect: `ALIAS`, `INFSR`, `MAXDEV`, `IGNORE`, `INCLUDE`, `COMMIT`, `BLOCK`, `STATIC`, `EXTMBR`.

**Control options:** every `CTL-OPT` keyword is taken; `MAIN`, `NOMAIN`, `BNDDIR`, `DATFMT` and `TIMFMT` have an effect.

**Embedded SQL**
- Of the SQLCA, only `SQLCODE` (`SQLCOD`), `SQLSTATE` (`SQLSTT`), `SQLERRD` and `SQLER3`.
- `CALL` of an SQL procedure (`CREATE PROCEDURE` isn't supported), and `CALL` arguments that are expressions. `FETCH ... USING DESCRIPTOR`.
- SQL that can't be translated for the database is reported statement by statement, as are SQL scripts.

## Display and printer files

DDS for display files (`CRTDSPF`) and printer files (`CRTPRTF`) is read from its columns. What the fake can't show is reported as not supported; the file is still made.

**Supported**
- Records and fields: constants, named fields with usage O, I, B, H and P (program-to-system), line and position (or +n), data types A, S, Y and the like; dates, times and timestamps as their text; reference fields (`REF`, `REFFLD`, R in column 29) from database files or other display files.
- Conditions: up to three option indicators a line, and more over lines with A (and) or O (or) in column 7, or a line of indicators alone; `*DS3`/`*DS4` display-size conditions.
- Display: `DSPATR` (and `DSPATR(&field)` from a P field), `COLOR`, `EDTCDE` (with `*` fill or a floating currency symbol), `EDTWRD`, `DFT`, `DATE`, `TIME`, `USER`, `SYSNAME`, `PAGNBR`, `MSGID` and `MSGCON`, `OVERLAY`, `PUTOVR`, `ALARM`, `PROTECT`, `CSRLOC`, `RTNCSRLOC`, `INVITE`, `ASSUME`.
- Keys: `CA`/`CF` keys with their indicators, `ROLLUP`/`ROLLDOWN` (`PAGEDOWN`/`PAGEUP`), `CHANGE` on records and fields.
- Checking what's typed, before the program gets it: `VALUES`, `RANGE`, `COMP`/`CMP`, `CHECK(ME MF VN RB RZ LC)`, with `CHKMSGID` for the message.
- Errors and messages: `ERRMSG`, `ERRMSGID`, `SFLMSG`, `SFLMSGID`, from message files (`CRTMSGF`, `ADDMSGD`, and message-file source as CL, `MSGF` members); message subfiles (`SFLMSGRCD`, `SFLPGMQ`, `SFLMSGKEY`) showing what `QMHSNDPM` sends.
- Subfiles: `SFL`, `SFLCTL`, `SFLSIZ`, `SFLPAG`, `SFLDSP`, `SFLDSPCTL`, `SFLCLR`, `SFLDLT`, `SFLEND`, `SFLINZ`, `SFLNXTCHG`, `SFLRCDNBR`, `SFLDROP`/`SFLFOLD` (the key switches between one line and all lines a record), paging by itself.
- Windows: `WINDOW` at a line and position, placed by the program (`&line &position`) or by the cursor (`*DFT`), shared with `WINDOW(record)`, `WDWBORDER`, `WDWTITLE`.
- Printer files: `SPACEB`/`SPACEA`/`SKIPB`/`SKIPA`, `FORMLEN`, `FORMOFL`, and the same field keywords.
- Taken with no effect: help (H specs, `HLPARA` and the other `HLP` keywords; the Help key shows nothing), `ENTFLDATR`, `PUTRETAIN`, `MSGALARM`, `MOUBTN`, `VLDCMDKEY`, `DUP`, `BARCODE` (printed as text), and the like.

**Not supported**
- Usage M (message) fields, data types F, J, E, O and G, `DSPSIZ` other than 24 x 80 and 27 x 132, edit codes with other than `*` or a currency symbol.

## CL

CL programs (`CLLE`, `CLP`) are run command by command; the program's own commands are run here, the rest by the machine with the program's variables put in.

**Supported**
- Program structure: `PGM`/`ENDPGM`, `DCL`, `IF`/`ELSE`, `DO`/`ENDDO`, `SELECT`/`WHEN`/`OTHERWISE`, `DOWHILE`, `DOUNTIL`, `DOFOR`, `LEAVE`, `ITERATE`, `SUBR`/`CALLSUBR`/`RTNSUBR`, `MONMSG` (on a command, or for the program), `CHGVAR`, `CALL`, `CALLPRC` (a service program's procedure, through `DCLPRCOPT BNDDIR` or any that exports it, with `RTNVAL`), `RETURN`, and `GOTO` to a label anywhere in the program (back, forward, into or out of a group).
- Source as written on IBM i: `/*` after a non-blank isn't a comment (`LIB/*ALL`), and prompting (`?COMMAND`, `??KEYWORD`) runs the command as it is.
- Files: `DCLF` (with `OPNID`) of a display file, shown and read with `SNDF`, `RCVF` and `SNDRCVF` (with `&IN01`–`&IN99`), or of a database file, read with `RCVF` in its key's order to `CPF0864`; `CLOSE`.
- Data areas and the job: `RTVDTAARA` (with a substring), `CHGDTAARA`, `RTVJOBA`, `RTVSYSVAL`, `RTVMBRD`, `CVTDAT`.
- Messages: `SNDPGMMSG` (text, or a message file's message with `MSGDTA` in `&1`; `*ESCAPE` ends the program; `TOPGMQ(*SAME)` goes to the message subfile), `SNDUSRMSG` (the reply, or its default with nobody to answer), `RCVMSG` (the escape message `MONMSG` caught, or the last one sent), `RMVMSG`, message files (`CRTMSGF`, `ADDMSGD`, `CHGMSGD`, `RMVMSGD`, `DLTMSGF`).
- Receiving from a real IBM i: `SAVRSTLIB` and `SAVRSTOBJ` (`LIB`, `OBJ`, `OBJTYPE`, `OMITOBJ`, `RSTLIB`, `RMTLOCNAME`, and the fake's own `RMTUSER`, `RMTPWD`, `PORT`, `SECCNN` and `DATA`) fetch files with their records, logical files, indexes, views, source members, data areas and message files: see the README.
- Database files: `OVRDBF`/`DLTOVR` (the file a program's open gets), `OPNQRYF` (`QRYSLT`, `KEYFLD`, `MAPFLD`, `FORMAT`, `JFLD`, `JDFTVAL`, `GRPFLD`, `GRPSLT`, `OPNID`, with `%SST`, `%DIGITS`, `%SUM` and the like, `%RANGE`, `%VALUES`, `%WLDCRD`, `*CT`) read by the next program that opens the file, `CPYFRMQRYF`, `CLOF`, `CPYF` (`MBROPT`, `CRTFILE`, `FMTOPT`, `INCREL`), `CLRPFM`, `CRTDUPOBJ`, `CPYTOIMPF` and `CPYFRMIMPF` (delimited stream files).
- `SBMJOB` runs its command in a job of its own while the program goes on.
- Taken with nothing to do: authorities, members (`ADDPFM`, `RMVM`), `ALCOBJ`/`DLCOBJ`, `CHGJOB`, `DLYJOB`, commitment control, journals, subsystems, job and output queues, spooled-file housekeeping, data queues, `DSPFD`, `SIGNOFF` and the like.

**Not supported**
- `STRQMQRY` and `RUNQRY`, `RTVMSG`, files with more than one member, and `OPNQRYF` updates through the query (it's for reading).
- QTEMP is one for all jobs (the fake has one database connection).
