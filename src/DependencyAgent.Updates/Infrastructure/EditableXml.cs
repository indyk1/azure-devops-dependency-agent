using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace DependencyAgent.Updates.Infrastructure;

/// <summary>
/// An MSBuild-style XML file loaded with <see cref="LoadOptions.PreserveWhitespace"/> and line info. Edits are
/// applied as minimal text splices at the original source positions, so everything else (declaration, BOM,
/// line endings, attribute order, self-closing style, comments, indentation) is preserved byte-for-byte.
/// </summary>
internal sealed class EditableXml
{
    private readonly List<Edit> _edits = [];
    private readonly int[] _lineStarts;

    private EditableXml(TextFile file, XDocument document)
    {
        File = file;
        Document = document;
        _lineStarts = ComputeLineStarts(file.Text);
    }

    private readonly record struct Edit(int Start, int Length, string Text);

    public TextFile File { get; }

    public XDocument Document { get; }

    public string RelativePath => File.RelativePath;

    public string FileName => Path.GetFileName(RelativePath);

    public XElement? Root => Document.Root;

    public static EditableXml? TryLoad(string root, string fullPath, out string? error)
    {
        error = null;
        try
        {
            var file = TextFile.Load(root, fullPath);
            using var reader = new StringReader(file.Text);
            using var xml = XmlReader.Create(reader, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit });
            var doc = XDocument.Load(xml, LoadOptions.PreserveWhitespace | LoadOptions.SetLineInfo);
            return new EditableXml(file, doc);
        }
        catch (Exception ex) when (ex is XmlException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            error = $"Could not read {PathUtil.Relative(root, fullPath)}: {ex.Message}";
            return null;
        }
    }

    public IEnumerable<XElement> Descendants(string localName) =>
        Document.Descendants().Where(e => string.Equals(e.Name.LocalName, localName, StringComparison.OrdinalIgnoreCase));

    public bool TrySetAttribute(XAttribute attribute, string value)
    {
        if (attribute is not IXmlLineInfo info || !info.HasLineInfo() || attribute.Name.Namespace != XNamespace.None)
        {
            return false;
        }

        var text = File.Text;
        var name = attribute.Name.LocalName;
        var i = OffsetOf(info);
        if (i < 0 || i + name.Length > text.Length || string.CompareOrdinal(text, i, name, 0, name.Length) != 0)
        {
            return false;
        }

        i += name.Length;
        i = SkipWhitespace(text, i);
        if (i >= text.Length || text[i] != '=')
        {
            return false;
        }

        i = SkipWhitespace(text, i + 1);
        if (i >= text.Length || text[i] is not ('"' or '\''))
        {
            return false;
        }

        var quote = text[i];
        var end = text.IndexOf(quote, i + 1);
        return end >= 0 && AddEdit(i + 1, end - i - 1, Escape(value, quote));
    }

    /// <summary>Replaces the text content of a leaf element, keeping surrounding whitespace.</summary>
    public bool TrySetElementText(XElement element, string value)
    {
        if (element is not IXmlLineInfo info || !info.HasLineInfo() || element.HasElements || element.Name.Namespace != XNamespace.None)
        {
            return false;
        }

        var text = File.Text;
        var name = element.Name.LocalName;
        var i = OffsetOf(info);
        if (i < 0 || i + name.Length > text.Length || string.CompareOrdinal(text, i, name, 0, name.Length) != 0)
        {
            return false;
        }

        var j = i + name.Length;
        var quote = '\0';
        for (; j < text.Length; j++)
        {
            var c = text[j];
            if (quote != '\0')
            {
                if (c == quote)
                {
                    quote = '\0';
                }
            }
            else if (c is '"' or '\'')
            {
                quote = c;
            }
            else if (c == '>')
            {
                break;
            }
        }

        if (j >= text.Length || text[j - 1] == '/')
        {
            return false;
        }

        var start = j + 1;
        var end = text.IndexOf('<', start);
        if (end < 0 || end + 1 >= text.Length || text[end + 1] != '/')
        {
            return false;
        }

        var raw = text.AsSpan(start, end - start);
        var trimmedStart = start;
        var trimmedEnd = start;
        if (!raw.IsWhiteSpace())
        {
            trimmedStart = start + (raw.Length - raw.TrimStart().Length);
            trimmedEnd = end - (raw.Length - raw.TrimEnd().Length);
        }

        return AddEdit(trimmedStart, trimmedEnd - trimmedStart, Escape(value, '\0'));
    }

    /// <summary>Writes the file if the queued edits changed it. Returns true when written.</summary>
    public bool Save()
    {
        if (_edits.Count == 0)
        {
            return false;
        }

        var sb = new StringBuilder(File.Text);
        foreach (var edit in _edits.OrderByDescending(e => e.Start))
        {
            sb.Remove(edit.Start, edit.Length).Insert(edit.Start, edit.Text);
        }

        return File.Save(sb.ToString());
    }

    private bool AddEdit(int start, int length, string replacement)
    {
        foreach (var e in _edits)
        {
            if (e.Start == start && e.Length == length)
            {
                return e.Text == replacement;
            }

            if (start < e.Start + e.Length && e.Start < start + length)
            {
                return false;
            }
        }

        _edits.Add(new Edit(start, length, replacement));
        return true;
    }

    private int OffsetOf(IXmlLineInfo info)
    {
        var line = info.LineNumber - 1;
        return line < 0 || line >= _lineStarts.Length ? -1 : _lineStarts[line] + info.LinePosition - 1;
    }

    private static int SkipWhitespace(string text, int i)
    {
        while (i < text.Length && char.IsWhiteSpace(text[i]))
        {
            i++;
        }

        return i;
    }

    private static string Escape(string value, char quote)
    {
        var sb = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            sb.Append(c switch
            {
                '&' => "&amp;",
                '<' => "&lt;",
                '>' => "&gt;",
                '"' when quote == '"' => "&quot;",
                '\'' when quote == '\'' => "&apos;",
                _ => c.ToString(),
            });
        }

        return sb.ToString();
    }

    private static int[] ComputeLineStarts(string text)
    {
        var starts = new List<int> { 0 };
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n')
            {
                starts.Add(i + 1);
            }
            else if (text[i] == '\r')
            {
                if (i + 1 < text.Length && text[i + 1] == '\n')
                {
                    i++;
                }

                starts.Add(i + 1);
            }
        }

        return [.. starts];
    }
}
