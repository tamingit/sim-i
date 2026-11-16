// What ILE RPG (IBM i 7.5) has, whether or not Sim i runs it. A word in one of these lists that
// Sim i can't run is "not supported"; a word in none of them isn't RPG, and an IBM i's compiler
// rejects it, so Sim i does too.

namespace SimI.Rpg;

internal static class RpgLanguage
{
    /// <summary>The operation codes free-form RPG has.</summary>
    public static readonly HashSet<string> FreeFormOperations =
    [
        "ACQ", "BEGSR", "CALLP", "CHAIN", "CLEAR", "CLOSE", "COMMIT", "DATA-GEN", "DATA-INTO", "DEALLOC", "DELETE", "DOU", "DOW",
        "DSPLY", "DUMP", "ELSE", "ELSEIF", "ENDDO", "ENDFOR", "ENDIF", "ENDMON", "ENDSL", "ENDSR", "EVAL", "EVALR", "EVAL-CORR",
        "EXCEPT", "EXFMT", "EXSR", "FEOD", "FOR", "FOR-EACH", "FORCE", "IF", "IN", "ITER", "LEAVE", "LEAVESR", "MONITOR", "NEXT",
        "ON-ERROR", "ON-EXCP", "ON-EXIT", "OPEN", "OTHER", "OUT", "POST", "READ", "READC", "READE", "READP", "READPE", "REL",
        "RESET", "RETURN", "ROLBK", "SELECT", "SETGT", "SETLL", "SND-MSG", "SORTA", "TEST", "UNLOCK", "UPDATE", "WHEN", "WHEN-IN",
        "WHEN-IS", "WRITE", "XML-INTO", "XML-SAX",
    ];

    /// <summary>Operation codes only fixed-form RPG has: in free-form code they're errors, with what to use instead.</summary>
    public static readonly Dictionary<string, string> FixedFormOnly = new()
    {
        ["ADD"] = "use +", ["SUB"] = "use -", ["MULT"] = "use *", ["DIV"] = "use / or %DIV", ["Z-ADD"] = "use =", ["Z-SUB"] = "use = -",
        ["MOVE"] = "use = or EVALR", ["MOVEL"] = "use =", ["MOVEA"] = "use = or %SUBARR", ["CALL"] = "use CALLP with an EXTPGM prototype",
        ["CALLB"] = "use CALLP with a prototype", ["GOTO"] = "use LEAVE, ITER or a subroutine", ["TAG"] = "use LEAVE, ITER or a subroutine",
        ["CAT"] = "use +", ["SUBST"] = "use %SUBST", ["SCAN"] = "use %SCAN", ["CHECK"] = "use %CHECK", ["CHECKR"] = "use %CHECKR",
        ["XLATE"] = "use %XLATE", ["LOOKUP"] = "use %LOOKUP", ["XFOOT"] = "use %XFOOT", ["SQRT"] = "use %SQRT", ["MVR"] = "use %REM",
        ["ADDDUR"] = "use + with %DAYS and the like", ["SUBDUR"] = "use - or %DIFF", ["EXTRCT"] = "use %SUBDT", ["TIME"] = "use %DATE, %TIME or %TIMESTAMP",
        ["SETON"] = "use *INxx = *ON", ["SETOFF"] = "use *INxx = *OFF", ["DO"] = "use FOR", ["END"] = "use ENDIF, ENDDO, ENDFOR or ENDSL",
        ["COMP"] = "use a comparison", ["PARM"] = "use DCL-PI", ["PLIST"] = "use DCL-PI", ["KLIST"] = "use %KDS or a list of keys",
        ["KFLD"] = "use %KDS or a list of keys", ["DEFINE"] = "use DCL-S with LIKE", ["ALLOC"] = "use %ALLOC", ["REALLOC"] = "use %REALLOC",
        ["OCCUR"] = "use %OCCUR", ["SHTDN"] = "use %SHTDN", ["BITON"] = "use %BITOR", ["BITOFF"] = "use %BITAND", ["TESTB"] = "use %BITAND",
        ["TESTN"] = "use MONITOR and %DEC", ["TESTZ"] = "", ["CASEQ"] = "use SELECT", ["ENDCS"] = "use SELECT",
        ["MHHZO"] = "", ["MHLZO"] = "", ["MLHZO"] = "", ["MLLZO"] = "",
    };

    /// <summary>The built-in functions.</summary>
    public static readonly HashSet<string> Bifs =
    [
        "%ABS", "%ADDR", "%ALLOC", "%BITAND", "%BITNOT", "%BITOR", "%BITXOR", "%CHAR", "%CHARCOUNT", "%CHECK", "%CHECKR", "%CONCAT",
        "%CONCATARR", "%DATA", "%DATE", "%DAYS", "%DEC", "%DECH", "%DECPOS", "%DIFF", "%DIV", "%EDITC", "%EDITFLT", "%EDITW", "%ELEM",
        "%EOF", "%EQUAL", "%ERROR", "%FIELDS", "%FLOAT", "%FOUND", "%GEN", "%GRAPH", "%HANDLER", "%HIVAL", "%HOURS", "%INT", "%INTH",
        "%KDS", "%LEFT", "%LEN", "%LIST", "%LOOKUP", "%LOOKUPGE", "%LOOKUPGT", "%LOOKUPLE", "%LOOKUPLT", "%LOVAL", "%LOWER", "%MAX",
        "%MAXARR", "%MIN", "%MINARR", "%MINUTES", "%MONTHS", "%MSECONDS", "%MSG", "%NULLIND", "%OCCUR", "%OMITTED", "%OPEN", "%PADDR",
        "%PARMNUM", "%PARMS", "%PARSER", "%PASSED", "%PROC", "%RANGE", "%REALLOC", "%REM", "%REPLACE", "%RIGHT", "%SCAN", "%SCANR",
        "%SCANRPL", "%SECONDS", "%SHTDN", "%SIZE", "%SPLIT", "%SQRT", "%STATUS", "%STR", "%SUBARR", "%SUBDT", "%SUBST", "%TARGET",
        "%THIS", "%TIME", "%TIMESTAMP", "%TLOOKUP", "%TLOOKUPGE", "%TLOOKUPGT", "%TLOOKUPLE", "%TLOOKUPLT", "%TRIM", "%TRIML", "%TRIMR",
        "%UCS2", "%UNS", "%UNSH", "%UPPER", "%XFOOT", "%XLATE", "%YEARS",
    ];

    /// <summary>The data types of DCL-S, DCL-DS subfields, DCL-PR and DCL-PI.</summary>
    public static readonly HashSet<string> Types =
    [
        "CHAR", "VARCHAR", "GRAPH", "VARGRAPH", "UCS2", "VARUCS2", "IND", "PACKED", "ZONED", "BINDEC", "INT", "UNS", "FLOAT", "DATE",
        "TIME", "TIMESTAMP", "POINTER", "OBJECT",
    ];

    /// <summary>The other keywords of a definition (DCL-S, DCL-C, DCL-DS, DCL-PR, DCL-PI and their subfields and parameters).</summary>
    public static readonly HashSet<string> DefinitionKeywords =
    [
        "ALIAS", "ALIGN", "ALT", "ALTSEQ", "ASCEND", "BASED", "CCSID", "CLASS", "CONST", "CTDATA", "DATFMT", "DESCEND", "DIM",
        "DTAARA", "EXPORT", "EXT", "EXTFLD", "EXTFMT", "EXTNAME", "EXTPGM", "EXTPROC", "FROMFILE", "IMPORT", "INZ", "LEN", "LIKE",
        "LIKEDS", "LIKEFILE", "LIKEREC", "NOOPT", "NULLIND", "OCCURS", "OPDESC", "OPTIONS", "OVERLAY", "OVERLOAD", "PACKEVEN", "PERRCD", "POS",
        "PREFIX", "PSDS", "QUALIFIED", "REQPROTO", "RTNPARM", "SAMEPOS", "SQLTYPE", "STATIC", "TEMPLATE", "TIMFMT", "TOFILE",
        "VALUE", "VARYING", "DCL-PARM", "DCL-SUBF",
    ];

    /// <summary>The keywords of DCL-F.</summary>
    public static readonly HashSet<string> FileKeywords =
    [
        "ALIAS", "BLOCK", "COMMIT", "DATFMT", "DEVID", "DISK", "EXTDESC", "EXTFILE", "EXTIND", "EXTMBR", "FORMLEN", "FORMOFL",
        "HANDLER", "IGNORE", "INCLUDE", "INDDS", "INFDS", "INFSR", "KEYED", "KEYLOC", "LIKEFILE", "MAXDEV", "OFLIND", "PASS",
        "PGMNAME", "PLIST", "PREFIX", "PRINTER", "PRTCTL", "QUALIFIED", "RAFDATA", "RECNO", "RENAME", "SAVEDS", "SAVEIND", "SEQ",
        "SFILE", "SLN", "SPECIAL", "STATIC", "TEMPLATE", "TIMFMT", "USAGE", "USROPN", "WORKSTN",
    ];

    /// <summary>The keywords of CTL-OPT.</summary>
    public static readonly HashSet<string> ControlKeywords =
    [
        "ACTGRP", "ALLOC", "ALTSEQ", "ALWNULL", "AUT", "BNDDIR", "CCSID", "CCSIDCVT", "CHARCOUNT", "CHARCOUNTTYPES", "COPYNEST",
        "COPYRIGHT", "CURSYM", "CVTOPT", "DATEDIT", "DATFMT", "DCLOPT", "DEBUG", "DECEDIT", "DECPREC", "DFTACTGRP", "DFTNAME",
        "ENBPFRCOL", "EXPROPTS", "EXTBININT", "FIXNBR", "FLTDIV", "FORMSALIGN", "FTRANS", "GENLVL", "INDENT", "INTPREC", "LANGID",
        "MAIN", "NOMAIN", "OPENOPT", "OPTIMIZE", "OPTION", "PGMINFO", "PRFDTA", "REQPREXP", "SRTSEQ", "STGMDL", "TEXT", "THREAD",
        "TIMFMT", "TRUNCNBR", "USRPRF", "VALIDATE",
    ];

    /// <summary>The compiler directives.</summary>
    public static readonly HashSet<string> Directives =
    [
        "/COPY", "/INCLUDE", "/DEFINE", "/UNDEFINE", "/IF", "/ELSEIF", "/ELSE", "/ENDIF", "/EOF", "/FREE", "/END-FREE", "/TITLE",
        "/EJECT", "/SPACE", "/SET", "/RESTORE", "/CHARCOUNT", "/EXEC", "/END-EXEC",
    ];
}
