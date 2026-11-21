using LlmBridge.Core;

namespace LlmBridge.Core.Tests;

public class MarkdownLiteTests
{
    // Gemma 4'ün gerçek bir yanıtından (LLM Memory Manager, 21.11.2026).
    private const string RealReply = """
        **Neler Yapmıştır? (Özet)**

        1. **Kurtuluş Savaşı:** Türk milletinin bağımsızlık mücadelesini başarıyla yönetmiştir.
        2. **Cumhuriyetin İlanı:** 29 Ekim 1923'te Türkiye Cumhuriyeti'ni ilan etmiştir.
        3. **Modernleşme Reformları:** Toplumu çağdaş bir medeniyete taşımak adına reformlar yapmıştır:
            * **Laiklik:** Devlet yönetiminde dini etkileri azaltarak laik bir yapı kurmuştur.
            * **Hukuk Sistemi:** Çağdaş hukuk sistemlerini benimsemiştir (örneğin, Medeni Kanun).
        """;

    [Fact]
    public void Real_reply_becomes_headed_numbered_and_nested_bullets_without_asterisks()
    {
        var lines = MarkdownLite.Parse(RealReply);

        Assert.Equal(MdLineKind.Paragraph, lines[0].Kind);
        Assert.True(lines[0].Spans.Single().Bold);
        Assert.Equal("Neler Yapmıştır? (Özet)", lines[0].Spans[0].Text);
        Assert.Equal(MdLineKind.Blank, lines[1].Kind);

        var item = lines[2];
        Assert.Equal(MdLineKind.Numbered, item.Kind);
        Assert.Equal("1.", item.Marker);
        Assert.Equal(("Kurtuluş Savaşı:", true), (item.Spans[0].Text, item.Spans[0].Bold));
        Assert.False(item.Spans[1].Bold);

        var nested = lines[5];
        Assert.Equal((MdLineKind.Bullet, 1), (nested.Kind, nested.Level));
        Assert.Equal("Laiklik:", nested.Spans[0].Text);

        Assert.DoesNotContain("**", MarkdownLite.ToPlainText(RealReply));
        Assert.Contains("  • Laiklik: Devlet", MarkdownLite.ToPlainText(RealReply));
    }

    [Theory]
    [InlineData("# Başlık", MdLineKind.Heading, 1)]
    [InlineData("### Alt başlık ###", MdLineKind.Heading, 3)]
    [InlineData("- madde", MdLineKind.Bullet, 0)]
    [InlineData("  + iç madde", MdLineKind.Bullet, 1)]
    [InlineData("        * en iç", MdLineKind.Bullet, 2)]
    [InlineData("12) on iki", MdLineKind.Numbered, 0)]
    [InlineData("---", MdLineKind.Rule, 0)]
    [InlineData("* * *", MdLineKind.Rule, 0)]
    [InlineData("#etiket boşluksuz", MdLineKind.Paragraph, 0)]
    public void Line_kinds(string line, MdLineKind kind, int level)
    {
        var parsed = MarkdownLite.Parse(line).Single();
        Assert.Equal((kind, level), (parsed.Kind, parsed.Level));
    }

    [Fact]
    public void Inline_bold_italic_and_code()
    {
        var spans = MarkdownLite.ParseInline("Bu **kalın**, bu *italik*, bu `x * y` ve __bu da kalın__.");
        Assert.Equal(
            ["Bu ", "kalın", ", bu ", "italik", ", bu ", "x * y", " ve ", "bu da kalın", "."],
            spans.Select(s => s.Text));
        Assert.True(spans[1].Bold && spans[3].Italic && spans[5].Code && spans[7].Bold);
        Assert.False(spans[5].Bold || spans[5].Italic);                 // koddaki yıldız biçim değil
    }

    [Theory]
    [InlineData("Yanıt yazılıyor **yarım kal")]                         // akışta henüz kapanmamış
    [InlineData("5 * 3 = 15 ve 2*4 = 8")]                               // çarpma işareti italik değil
    [InlineData("dosya_adi_ornek.txt")]                                 // alt çizgi kelime içinde
    public void Unclosed_or_arithmetic_markers_stay_as_text(string text)
    {
        var spans = MarkdownLite.ParseInline(text);
        Assert.Equal(text, string.Concat(spans.Select(s => s.Text)));
        Assert.All(spans, s => Assert.False(s.Bold || s.Italic));
    }

    [Fact]
    public void Code_block_keeps_its_lines_and_hides_fences()
    {
        var lines = MarkdownLite.Parse("Örnek:\n```csharp\nvar x = 2 * 3; // **değil**\n```\nBitti");
        Assert.Equal(3, lines.Count);
        Assert.True(lines[1].Spans.Single().Code);
        Assert.Equal("var x = 2 * 3; // **değil**", lines[1].Spans[0].Text);
    }

    [Fact]
    public void Leading_and_trailing_blank_lines_are_dropped()
    {
        Assert.Equal(2, MarkdownLite.Parse("\n\nbir\n\n").Count + 1);
        Assert.Empty(MarkdownLite.Parse(null));
    }
}
