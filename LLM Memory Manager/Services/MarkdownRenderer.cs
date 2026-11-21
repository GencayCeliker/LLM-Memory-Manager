using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using LlmBridge.Core;

namespace LLM_Memory_Manager.Services;

/// <summary>
/// Model yanıtındaki Markdown'u biçimli gösterir: kalın/italik/kod, başlıklar, madde ve numaralı
/// listeler, ayraç. Her satır ayrı bir öğedir; liste işareti ayrı sütunda durduğu için alt satıra
/// kayan madde metni girintisini korur. Akış sırasında her parça geldiğinde yeniden kurulur.
/// </summary>
public static class MarkdownRenderer
{
    private const double ListIndent = 18;

    private static readonly Brush CodeForeground = Freeze(new SolidColorBrush(Color.FromRgb(206, 145, 120)));
    private static readonly Brush CodeBackground = Freeze(new SolidColorBrush(Color.FromRgb(30, 30, 30)));
    private static readonly Brush RuleBrush = Freeze(new SolidColorBrush(Color.FromRgb(85, 85, 85)));
    private static readonly FontFamily CodeFont = new("Cascadia Mono, Consolas");

    /// <summary>Markdown metninden, balonun içine konacak öğeyi kurar.</summary>
    public static UIElement Build(string? markdown, double fontSize, Brush foreground)
    {
        var panel = new StackPanel();
        foreach (var line in MarkdownLite.Parse(markdown))
        {
            panel.Children.Add(line.Kind switch
            {
                MdLineKind.Blank => new Border { Height = fontSize * 0.6 },
                MdLineKind.Rule => new Border { Height = 1, Background = RuleBrush, Margin = new Thickness(0, 6, 0, 6) },
                MdLineKind.Heading => Heading(line, fontSize, foreground),
                MdLineKind.Bullet or MdLineKind.Numbered => ListItem(line, fontSize, foreground),
                _ => Paragraph(line.Spans, fontSize, foreground),
            });
        }
        return panel;
    }

    private static TextBlock Paragraph(IEnumerable<MdSpan> spans, double fontSize, Brush foreground)
    {
        var block = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = fontSize, Foreground = foreground };
        foreach (var span in spans)
        {
            block.Inlines.Add(Run(span));
        }
        return block;
    }

    private static TextBlock Heading(MdLine line, double fontSize, Brush foreground)
    {
        var block = Paragraph(line.Spans, fontSize + (4 - line.Level) * 2, foreground);
        block.FontWeight = FontWeights.Bold;
        block.Margin = new Thickness(0, 4, 0, 2);
        return block;
    }

    private static Grid ListItem(MdLine line, double fontSize, Brush foreground)
    {
        var grid = new Grid { Margin = new Thickness(6 + line.Level * ListIndent, 1, 0, 1) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto, MinWidth = ListIndent });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var marker = line.Kind == MdLineKind.Bullet ? (line.Level == 0 ? "•" : "◦") : line.Marker;
        var markerBlock = new TextBlock
        {
            Text = marker,
            FontSize = fontSize,
            Foreground = foreground,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 6, 0),
        };
        var text = Paragraph(line.Spans, fontSize, foreground);
        Grid.SetColumn(text, 1);
        grid.Children.Add(markerBlock);
        grid.Children.Add(text);
        return grid;
    }

    private static Run Run(MdSpan span)
    {
        var run = new Run(span.Text);
        if (span.Bold)
        {
            run.FontWeight = FontWeights.Bold;
        }
        if (span.Italic)
        {
            run.FontStyle = FontStyles.Italic;
        }
        if (span.Code)
        {
            run.FontFamily = CodeFont;
            run.Foreground = CodeForeground;
            run.Background = CodeBackground;
        }
        return run;
    }

    private static Brush Freeze(Brush brush)
    {
        brush.Freeze();
        return brush;
    }
}
