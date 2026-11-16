// What the debugger sees of a running program: its name, the line it's at, its variables, and
// the value of an expression in one of its calls. A frame is the mainline's or a procedure's
// call; the debugger holds it without knowing what's in it.

namespace SimI.Rpg;

internal sealed partial class Interpreter
{
    internal string DebugName => _name;
    internal string DebugLibrary => _library;
    internal int DebugLine => _line;
    internal IJobHost DebugHost => _host;

    /// <summary>The procedure a frame is a call of; null for the mainline.</summary>
    internal string? DebugProcedure(object frame) => ReferenceEquals(frame, _main) || ((Frame)frame).Procedure == _program.Main ? null : ((Frame)frame).Procedure.Name;

    /// <summary>A procedure call's parameters and local variables, by name.</summary>
    internal IEnumerable<KeyValuePair<string, Cell>> DebugLocals(object frame) =>
        DebugProcedure(frame) is null ? [] : ((Frame)frame).Variables.OrderBy(v => v.Key, StringComparer.Ordinal);

    /// <summary>The program's global variables and the fields of its files, by name.</summary>
    internal IEnumerable<KeyValuePair<string, Cell>> DebugGlobals() => _globals.OrderBy(v => v.Key, StringComparer.Ordinal);

    /// <summary>The indicators: *INLR and *IN01 to *IN99.</summary>
    internal IEnumerable<KeyValuePair<string, Cell>> DebugIndicators() =>
        Enumerable.Range(1, 99).Select(i => KeyValuePair.Create($"*IN{i:00}", _indicators[i])).Prepend(KeyValuePair.Create("*INLR", _lastRecord));

    /// <summary>An RPG expression's value in a frame: a variable, a subfield, an array element, a built-in function, a comparison.</summary>
    internal object DebugEvaluate(string expression, object frame) => Evaluate(RpgParser.ParseExpression(expression), (Frame)frame);

    /// <summary>The variable an expression names in a frame, to change it.</summary>
    internal Cell DebugLocate(string expression, object frame) => Locate(RpgParser.ParseExpression(expression), (Frame)frame);
}
