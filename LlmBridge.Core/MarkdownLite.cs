using System.Text.RegularExpressions;

namespace LlmBridge.Core;

/// <summary>Satırın türü: düz paragraf, başlık, madde işaretli veya numaralı liste, ayraç.</summary>
public enum MdLineKind
{
    Paragraph,
    Heading,
    Bullet,
    Numbered,
    Rule,
    Blank,
}

/// <summary>Satır içi parça: metin ve biçimi.</summary>
public sealed record MdSpan(string Text, bool Bold = false, bool Italic = false, bool Code = false);

/// <summary>
/// Bir satır. <see cref="Level"/>: başlıkta 1-3, listede girinti düzeyi (0 = en dış).
/// <see cref="Marker"/>: numaralı listede "1." gibi numara.
/// </summary>
public sealed record MdLine(MdLineKind Kind, IReadOnlyList<MdSpan> Spans, int Level = 0, string Marker = "");

/// <summary>
/// Modellerin yanıtlarında kullandığı Markdown'un küçük bir alt kümesini ayrıştırır:
/// başlık (#), kalın (**metin**, __metin__), italik (*metin*), satır içi kod (`kod`),
/// madde işaretli (*, -, +) ve numaralı listeler, ayraç (---). Arayüzden bağımsızdır;
/// akış sırasında kapanmamış işaretler (ör. yarım "**") düz metin olarak kalır.
/// </summary>
public static partial class MarkdownLite
{
    [GeneratedRegex(@"^(#{1,6})\s+(.*)$")]
    private static partial Regex HeadingRegex();

    [GeneratedRegex(@"^(\s*)[*+-]\s+(.*)$")]
    private static partial Regex BulletRegex();

    [GeneratedRegex(@"^(\s*)(\d{1,3}[.)])\s+(.*)$")]
    private static partial Regex NumberedRegex();

    [GeneratedRegex(@"^\s*([-*_])(\s*\1){2,}\s*$")]
    private static partial Regex RuleRegex();

    // Sıra önemli: önce kod (içindeki yıldızlar biçim sayılmaz), sonra kalın, sonra italik.
    [GeneratedRegex(@"`([^`\n]+)`|\*\*(?=\S)(.+?)(?<=\S)\*\*|__(?=\S)(.+?)(?<=\S)__|(?<![*\w])\*(?=[^\s*])(.+?)(?<=[^\s*])\*(?![*\w])")]
    private static partial Regex InlineRegex();

    public static IReadOnlyList<MdLine> Parse(string? markdown)
    {
        var lines = new List<MdLine>();
        if (string.IsNullOrEmpty(markdown))
        {
            return lines;
        }
        var inCodeBlock = false;
        foreach (var raw in markdown.Replace("\r\n", "\n").Split('\n'))
        {
            if (raw.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                inCodeBlock = !inCodeBlock;
                continue;
            }
            if (inCodeBlock)
            {
                lines.Add(new MdLine(MdLineKind.Paragraph, [new MdSpan(raw, Code: true)]));
                continue;
            }
            if (string.IsNullOrWhiteSpace(raw))
            {
                lines.Add(new MdLine(MdLineKind.Blank, []));
                continue;
            }
            if (RuleRegex().IsMatch(raw))
            {
                lines.Add(new MdLine(MdLineKind.Rule, []));
                continue;
            }
            var heading = HeadingRegex().Match(raw);
            if (heading.Success)
            {
                lines.Add(new MdLine(MdLineKind.Heading, ParseInline(heading.Groups[2].Value.TrimEnd('#', ' ')),
                    Math.Min(3, heading.Groups[1].Length)));
                continue;
            }
            var bullet = BulletRegex().Match(raw);
            if (bullet.Success)
            {
                lines.Add(new MdLine(MdLineKind.Bullet, ParseInline(bullet.Groups[2].Value), IndentLevel(bullet.Groups[1].Value)));
                continue;
            }
            var numbered = NumberedRegex().Match(raw);
            if (numbered.Success)
            {
                lines.Add(new MdLine(MdLineKind.Numbered, ParseInline(numbered.Groups[3].Value),
                    IndentLevel(numbered.Groups[1].Value), numbered.Groups[2].Value));
                continue;
            }
            lines.Add(new MdLine(MdLineKind.Paragraph, ParseInline(raw.Trim())));
        }
        // Baştaki ve sondaki boş satırlar balonda gereksiz boşluk bırakır.
        while (lines.Count > 0 && lines[0].Kind == MdLineKind.Blank) lines.RemoveAt(0);
        while (lines.Count > 0 && lines[^1].Kind == MdLineKind.Blank) lines.RemoveAt(lines.Count - 1);
        return lines;
    }

    public static IReadOnlyList<MdSpan> ParseInline(string text)
    {
        var spans = new List<MdSpan>();
        var last = 0;
        foreach (Match m in InlineRegex().Matches(text))
        {
            if (m.Index > last)
            {
                spans.Add(new MdSpan(text[last..m.Index]));
            }
            if (m.Groups[1].Success)
            {
                spans.Add(new MdSpan(m.Groups[1].Value, Code: true));
            }
            else if (m.Groups[2].Success || m.Groups[3].Success)
            {
                var inner = m.Groups[2].Success ? m.Groups[2].Value : m.Groups[3].Value;
                // **kalın içinde *italik*** gibi iç içe biçim: iç parçalar kalınlığı korur.
                spans.AddRange(ParseInline(inner).Select(s => s with { Bold = true }));
            }
            else
            {
                spans.AddRange(ParseInline(m.Groups[4].Value).Select(s => s with { Italic = true }));
            }
            last = m.Index + m.Length;
        }
        if (last < text.Length)
        {
            spans.Add(new MdSpan(text[last..]));
        }
        return spans;
    }

    /// <summary>Biçim işaretleri kaldırılmış düz metin (ör. kopyalama veya sesli okuma için).</summary>
    public static string ToPlainText(string? markdown) =>
        string.Join("\n", Parse(markdown).Select(l => l.Kind switch
        {
            MdLineKind.Bullet => new string(' ', l.Level * 2) + "• " + Join(l.Spans),
            MdLineKind.Numbered => new string(' ', l.Level * 2) + l.Marker + " " + Join(l.Spans),
            MdLineKind.Rule => "———",
            _ => Join(l.Spans),
        }));

    private static string Join(IEnumerable<MdSpan> spans) => string.Concat(spans.Select(s => s.Text));

    /// <summary>Modeller iç içe listede 2 veya 4 boşluk kullanır: 1-4 boşluk 1. düzey, 5-8 boşluk 2. düzey.</summary>
    private static int IndentLevel(string whitespace)
    {
        var width = whitespace.Replace("\t", "    ").Length;
        return width == 0 ? 0 : Math.Min(3, (width + 3) / 4);
    }
}
