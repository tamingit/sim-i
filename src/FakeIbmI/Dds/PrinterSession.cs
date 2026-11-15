// A printer file open in a running program, described by its DDS (CRTPRTF): each WRITE of a
// record format prints its fields where they go on the page, spacing and skipping as the DDS
// says, and sets the overflow indicator at the overflow line. Closed, the pages become a
// spooled file.

using FakeIbmI.Rpg;

namespace FakeIbmI.Dds;

internal sealed class PrinterSession(DisplayFile file, Func<string, Cell?> variable, Func<int, bool> indicator, Cell? overflow)
{
    private readonly List<List<string>> _pages = [[]];
    private int _line = 1;

    public DisplayFile File { get; } = file;

    private List<string> Page => _pages[^1];

    private void NewPage()
    {
        _pages.Add([]);
        _line = 1;
    }

    /// <summary>Moves to the line before printing: SKIP to a line (a new page when it's above this one), or SPACE down.</summary>
    private void Move(int skip, int space)
    {
        if (skip > 0)
        {
            if (skip < _line || skip == _line && Page.Count >= _line && Page[_line - 1].Length > 0) NewPage();
            _line = skip;
        }
        if (space > 0)
        {
            _line += space;
            if (_line > File.PageLength) NewPage();
        }
    }

    private void Print(int column, string text)
    {
        while (Page.Count < _line) Page.Add("");
        var line = Page[_line - 1].PadRight(column - 1 + text.Length);
        var width = File.PageWidth;
        if (column > width) return;
        if (column - 1 + text.Length > width) text = text[..(width - column + 1)];
        Page[_line - 1] = (line[..(column - 1)] + text + line[(column - 1 + text.Length)..]).TrimEnd();
    }

    /// <summary>WRITE of a record format.</summary>
    public void Write(string name)
    {
        var format = File.Records[name];
        Move(format.SkipBefore, format.SpaceBefore);
        foreach (var field in format.Fields)
        {
            if (field.Hidden || !field.When.Holds(indicator)) continue;
            Move(field.SkipBefore, field.SpaceBefore);
            var text = field.Text ?? field.System switch
            {
                "PAGNBR" => Values.Edit(_pages.Count, 4, 0, field.EditCode ?? 'Z'),
                "DATE" => DateTime.Now.ToString(field.FourDigitYear ? "MM'/'dd'/'yyyy" : "MM'/'dd'/'yy"),
                "TIME" => DateTime.Now.ToString("HH':'mm':'ss"),
                "USER" or "SYSNAME" => field.System == "USER" ? "" : "FAKEIBMI",
                _ => DisplaySession.Format(field, (variable(field.Name!) ?? throw new RpgError(100, $"{field.Name} isn't defined")).Get()),
            };
            Print(field.Column, text);
            Move(field.SkipAfter, field.SpaceAfter);
        }
        Move(format.SkipAfter, format.SpaceAfter);
        if (_line >= File.OverflowLine) overflow?.Set(true);
    }

    /// <summary>The pages printed, each its lines, with a form feed line between pages.</summary>
    public (List<string> Lines, int Pages) Output()
    {
        var lines = new List<string>();
        for (var i = 0; i < _pages.Count; i++)
        {
            if (i > 0) lines.Add("\f");
            lines.AddRange(_pages[i]);
        }
        return (lines, _pages.Count);
    }
}
