using System.Text;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

// Markdig 和 WinUI 都有 Block / Inline，两边都起别名
using MdBlock = Markdig.Syntax.Block;
using XamlInline = Microsoft.UI.Xaml.Documents.Inline;

namespace GameGallery.Views;

/// <summary>
/// 用 Markdig 解析 Markdown，再把 AST 渲染成 WinUI 元素。
///
/// 覆盖 CHANGELOG 会用到的：标题、段落、无序/有序列表（含嵌套）、行内代码、
/// 粗体/斜体/删除线、链接、引用、围栏代码块、分隔线。
/// 表格、图片等没实现，会退化成纯文本。
/// </summary>
internal static class MarkdownRenderer
{
    private static readonly FontFamily MonoFont = new("Consolas");

    private const double CodeFontSize = 12.5;

    public static UIElement Render(string markdown, double fontSize = 14)
    {
        var document = Markdown.Parse(markdown);
        var root = new StackPanel { Spacing = 12 };

        foreach (var block in document)
        {
            AddBlock(root, block, 0, fontSize);
        }

        return root;
    }

    // ------------------------------------------------------------------
    // 块级元素
    // ------------------------------------------------------------------

    private static void AddBlock(Panel parent, MdBlock block, int depth, double fontSize)
    {
        switch (block)
        {
            case HeadingBlock heading:
                parent.Children.Add(CreateHeading(heading, fontSize));
                break;

            case ParagraphBlock paragraph:
                parent.Children.Add(CreateParagraph(paragraph, fontSize));
                break;

            case ListBlock list:
                parent.Children.Add(CreateList(list, depth, fontSize));
                break;

            case QuoteBlock quote:
                parent.Children.Add(CreateQuote(quote, depth, fontSize));
                break;

            case CodeBlock code:
                parent.Children.Add(CreateCodeBlock(code));
                break;

            case ThematicBreakBlock:
                parent.Children.Add(new Border
                {
                    Height = 1,
                    Background = Lookup("DividerStrokeColorDefaultBrush", Color.FromArgb(40, 128, 128, 128)),
                    Margin = new Thickness(0, 2, 0, 2),
                });
                break;

            case ContainerBlock container:
                foreach (var child in container) AddBlock(parent, child, depth, fontSize);
                break;

            case LeafBlock leaf when leaf.Lines.Count > 0:
                parent.Children.Add(CreatePlainParagraph(leaf, fontSize));
                break;
        }
    }

    private static TextBlock CreateHeading(HeadingBlock heading, double fontSize)
    {
        var block = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            FontWeight = FontWeights.SemiBold,
            FontSize = heading.Level switch
            {
                1 => fontSize + 8,
                2 => fontSize + 4,
                3 => fontSize + 1,
                _ => fontSize,
            },
            Margin = new Thickness(0, heading.Level <= 2 ? 10 : 4, 0, 0),
        };

        AppendInlines(heading.Inline, block.Inlines, fontSize);
        return block;
    }

    private static RichTextBlock CreateParagraph(ParagraphBlock paragraph, double fontSize)
        => CreateRichText(paragraph.Inline, fontSize);

    private static RichTextBlock CreatePlainParagraph(LeafBlock leaf, double fontSize)
    {
        var builder = new StringBuilder();
        for (var i = 0; i < leaf.Lines.Count; i++)
        {
            if (i > 0) builder.Append('\n');
            builder.Append(leaf.Lines.Lines[i].Slice.ToString());
        }

        var block = new RichTextBlock { TextWrapping = TextWrapping.Wrap, FontSize = fontSize };
        var paragraph = new Paragraph();
        paragraph.Inlines.Add(new Run { Text = builder.ToString() });
        block.Blocks.Add(paragraph);
        return block;
    }

    private static RichTextBlock CreateRichText(ContainerInline? inline, double fontSize)
    {
        var block = new RichTextBlock { TextWrapping = TextWrapping.Wrap, FontSize = fontSize };
        var paragraph = new Paragraph();
        AppendInlines(inline, paragraph.Inlines, fontSize);
        block.Blocks.Add(paragraph);
        return block;
    }

    private static UIElement CreateList(ListBlock list, int depth, double fontSize)
    {
        var panel = new StackPanel
        {
            Spacing = 6,
            Margin = new Thickness(depth == 0 ? 0 : 16, 0, 0, 0),
        };

        var index = 1;

        foreach (var item in list.OfType<ListItemBlock>())
        {
            var row = new Grid { ColumnSpacing = 8 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var marker = new TextBlock
            {
                Text = list.IsOrdered ? $"{index}." : "•",
                FontSize = fontSize,
                MinWidth = 18,
                Foreground = Lookup("TextFillColorSecondaryBrush", Color.FromArgb(160, 128, 128, 128)),
                VerticalAlignment = VerticalAlignment.Top,
            };
            Grid.SetColumn(marker, 0);
            row.Children.Add(marker);

            var content = new StackPanel { Spacing = 6 };
            foreach (var child in item) AddBlock(content, child, depth + 1, fontSize);
            Grid.SetColumn(content, 1);
            row.Children.Add(content);

            panel.Children.Add(row);
            index++;
        }

        return panel;
    }

    private static UIElement CreateQuote(QuoteBlock quote, int depth, double fontSize)
    {
        var content = new StackPanel { Spacing = 8 };
        foreach (var child in quote) AddBlock(content, child, depth + 1, fontSize);

        return new Border
        {
            BorderThickness = new Thickness(3, 0, 0, 0),
            BorderBrush = Lookup("AccentFillColorDefaultBrush", Color.FromArgb(255, 0, 95, 184)),
            Padding = new Thickness(10, 1, 0, 1),
            Child = content,
        };
    }

    private static UIElement CreateCodeBlock(CodeBlock code)
    {
        var builder = new StringBuilder();
        for (var i = 0; i < code.Lines.Count; i++)
        {
            if (i > 0) builder.Append('\n');
            builder.Append(code.Lines.Lines[i].Slice.ToString());
        }

        return new Border
        {
            Background = Lookup("SubtleFillColorSecondaryBrush", Color.FromArgb(24, 128, 128, 128)),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(12, 10, 12, 10),
            Child = new TextBlock
            {
                Text = builder.ToString(),
                FontFamily = MonoFont,
                FontSize = CodeFontSize,
                TextWrapping = TextWrapping.NoWrap,
                IsTextSelectionEnabled = true,
            },
        };
    }

    // ------------------------------------------------------------------
    // 行内元素
    // ------------------------------------------------------------------

    private static void AppendInlines(ContainerInline? container, InlineCollection target, double fontSize)
    {
        var inline = container?.FirstChild;

        while (inline is not null)
        {
            switch (inline)
            {
                case LiteralInline literal:
                    target.Add(new Run { Text = literal.Content.ToString() });
                    break;

                case EmphasisInline emphasis:
                {
                    if (emphasis.DelimiterChar == '~')
                    {
                        // 限定命名空间：直接用 using Windows.UI.Text 会和 Microsoft.UI.Text.FontWeights 撞名
                        var strike = new Span { TextDecorations = Windows.UI.Text.TextDecorations.Strikethrough };
                        AppendInlines(emphasis, strike.Inlines, fontSize);
                        target.Add(strike);
                    }
                    else if (emphasis.DelimiterCount >= 2)
                    {
                        var bold = new Bold();
                        AppendInlines(emphasis, bold.Inlines, fontSize);
                        target.Add(bold);
                    }
                    else
                    {
                        var italic = new Italic();
                        AppendInlines(emphasis, italic.Inlines, fontSize);
                        target.Add(italic);
                    }

                    break;
                }

                case CodeInline code:
                    target.Add(CreateInlineCode(code.Content));
                    break;

                case LinkInline link:
                    target.Add(CreateLink(link, fontSize));
                    break;

                case AutolinkInline autolink:
                    target.Add(CreateHyperlink(autolink.Url, autolink.Url, fontSize));
                    break;

                case LineBreakInline lineBreak:
                    // 软换行在 Markdown 里等价于空格（CHANGELOG 里大量依赖这一点）
                    if (lineBreak.IsHard) target.Add(new LineBreak());
                    else target.Add(new Run { Text = " " });
                    break;

                case ContainerInline nested:
                    AppendInlines(nested, target, fontSize);
                    break;
            }

            inline = inline.NextSibling;
        }
    }

    private static InlineUIContainer CreateInlineCode(string text)
    {
        var chip = new Border
        {
            Background = Lookup("SubtleFillColorSecondaryBrush", Color.FromArgb(24, 128, 128, 128)),
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(4, 1, 4, 2),
        };

        chip.Child = new TextBlock
        {
            Text = text,
            FontFamily = MonoFont,
            FontSize = CodeFontSize,
        };

        return new InlineUIContainer { Child = chip };
    }

    private static XamlInline CreateLink(LinkInline link, double fontSize)
    {
        var url = link.Url ?? string.Empty;

        // 图片退化成它的地址，至少不会丢信息
        if (link.IsImage)
        {
            return CreateHyperlink(url, url, fontSize);
        }

        var hyperlink = CreateHyperlink(url, string.Empty, fontSize);
        AppendInlines(link, hyperlink.Inlines, fontSize);

        if (hyperlink.Inlines.Count == 0)
        {
            hyperlink.Inlines.Add(new Run { Text = url });
        }

        return hyperlink;
    }

    private static Hyperlink CreateHyperlink(string url, string text, double fontSize)
    {
        var hyperlink = new Hyperlink();

        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            hyperlink.NavigateUri = uri;
        }

        if (!string.IsNullOrEmpty(text))
        {
            hyperlink.Inlines.Add(new Run { Text = text });
        }

        return hyperlink;
    }

    // ------------------------------------------------------------------

    /// <summary>取主题资源；取不到时退回一个中性色，保证不会因为缺资源而崩。</summary>
    private static Brush Lookup(string key, Color fallback)
        => Application.Current.Resources.TryGetValue(key, out var value) && value is Brush brush
            ? brush
            : new SolidColorBrush(fallback);
}
