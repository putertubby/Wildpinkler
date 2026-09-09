using System;
using System.Collections.Generic;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.UI.Text;

namespace Wildpinkler.App.Controls;

public sealed partial class AssistantMarkdown : UserControl
{
    private static readonly FontFamily CodeFont = new("Consolas");
    private static readonly string[] BulletGlyphs = ["\u2022", "\u25E6", "\u25AA", "\u25AA"];
    private static readonly TimeSpan RenderDebounce = TimeSpan.FromMilliseconds(50);

    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text),
        typeof(string),
        typeof(AssistantMarkdown),
        new PropertyMetadata(string.Empty, OnTextChanged));

    private readonly DispatcherQueueTimer _renderTimer;
    private string _pendingText = string.Empty;
    private string _renderedText = string.Empty;

    public AssistantMarkdown()
    {
        InitializeComponent();

        _renderTimer = DispatcherQueue.CreateTimer();
        _renderTimer.Interval = RenderDebounce;
        _renderTimer.IsRepeating = false;
        _renderTimer.Tick += (_, _) => Render(_pendingText);
        Unloaded += (_, _) => _renderTimer.Stop();

        // Brushes are resolved in code, so a theme switch needs an explicit rebuild.
        ActualThemeChanged += (_, _) => Render(Text);
    }

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    private static void OnTextChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args) =>
        ((AssistantMarkdown)sender).ScheduleRender((string?)args.NewValue ?? string.Empty);

    /// <summary>Streaming rewrites Text many times a second; rebuilding the whole visual tree that often
    /// is what causes the flicker, so appends are coalesced. Anything that is not an append is a
    /// different entry (the list recycles containers) and must render now: a debounced swap would change
    /// the row's height after it was measured and jerk the scroll position.</summary>
    private void ScheduleRender(string markdown)
    {
        _pendingText = markdown;
        _renderTimer.Stop();

        if (markdown == _renderedText)
            return;

        if (_renderedText.Length > 0 && markdown.StartsWith(_renderedText, StringComparison.Ordinal))
            _renderTimer.Start();
        else
            Render(markdown);
    }

    private void Render(string markdown)
    {
        _renderedText = markdown;
        ContentRoot.Children.Clear();

        var flow = new List<MarkdownBlock>();
        var quotes = new List<MarkdownBlock>();

        foreach (var block in AssistantMarkdownParser.Parse(markdown))
        {
            if (block.Kind == MarkdownBlockKind.Quote)
            {
                FlushFlow(flow);
                quotes.Add(block);
                continue;
            }

            FlushQuotes(quotes);

            switch (block.Kind)
            {
                case MarkdownBlockKind.CodeBlock:
                    FlushFlow(flow);
                    ContentRoot.Children.Add(BuildCodeBlock(block));
                    break;
                case MarkdownBlockKind.Table:
                    FlushFlow(flow);
                    ContentRoot.Children.Add(BuildTable(block));
                    break;
                case MarkdownBlockKind.Rule:
                    FlushFlow(flow);
                    ContentRoot.Children.Add(BuildRule());
                    break;
                default:
                    flow.Add(block);
                    break;
            }
        }

        FlushQuotes(quotes);
        FlushFlow(flow);
    }

    private void FlushFlow(List<MarkdownBlock> flow)
    {
        if (flow.Count == 0)
            return;

        ContentRoot.Children.Add(BuildRichText(flow, quoted: false));
        flow.Clear();
    }

    private void FlushQuotes(List<MarkdownBlock> quotes)
    {
        if (quotes.Count == 0)
            return;

        ContentRoot.Children.Add(new Border
        {
            BorderThickness = new Thickness(3, 0, 0, 0),
            BorderBrush = ThemeBrush("AccentFillColorDefaultBrush"),
            Padding = new Thickness(10, 2, 0, 2),
            Child = BuildRichText(quotes, quoted: true),
        });

        quotes.Clear();
    }

    private RichTextBlock BuildRichText(List<MarkdownBlock> blocks, bool quoted)
    {
        var rich = new RichTextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true,
            Foreground = ThemeBrush(quoted ? "TextFillColorSecondaryBrush" : "TextFillColorPrimaryBrush"),
        };

        foreach (var block in blocks)
        {
            var paragraph = new Paragraph();

            switch (block.Kind)
            {
                case MarkdownBlockKind.Heading:
                    paragraph.FontSize = block.Level switch { 1 => 20, 2 => 17, _ => 15 };
                    paragraph.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
                    paragraph.Margin = new Thickness(0, rich.Blocks.Count == 0 ? 0 : 10, 0, 2);
                    break;
                case MarkdownBlockKind.BulletItem:
                case MarkdownBlockKind.OrderedItem:
                    paragraph.Margin = new Thickness((block.Indent * 16) + 16, 1, 0, 1);
                    paragraph.TextIndent = -16;
                    paragraph.Inlines.Add(new Run
                    {
                        Text = block.Kind == MarkdownBlockKind.OrderedItem
                            ? block.Marker + "\u00A0"
                            : BulletGlyphs[block.Indent] + "\u00A0\u00A0",
                    });
                    break;
                default:
                    paragraph.Margin = new Thickness(0, 2, 0, 2);
                    break;
            }

            AppendInlines(paragraph.Inlines, block.Inlines);
            rich.Blocks.Add(paragraph);
        }

        return rich;
    }

    private Border BuildRule() => new()
    {
        Height = 1,
        Margin = new Thickness(0, 6, 0, 6),
        Background = ThemeBrush("CardStrokeColorDefaultBrush"),
    };

    private Border BuildCodeBlock(MarkdownBlock block)
    {
        var layout = new Grid();
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(new TextBlock
        {
            Text = block.Language ?? "code",
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = ThemeBrush("TextFillColorSecondaryBrush"),
        });

        var copy = new Button
        {
            Content = new FontIcon { Glyph = "\uE8C8", FontSize = 12 },
            Background = null,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(6, 2, 6, 2),
            Tag = block.Literal,
        };
        AutomationProperties.SetName(copy, "Copy code");
        ToolTipService.SetToolTip(copy, "Copy code");
        copy.Click += CopyCode_Click;
        Grid.SetColumn(copy, 1);
        header.Children.Add(copy);

        var code = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollMode = ScrollMode.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollMode = ScrollMode.Disabled,
            Content = new TextBlock
            {
                Text = block.Literal,
                FontFamily = CodeFont,
                FontSize = 13,
                TextWrapping = TextWrapping.NoWrap,
                IsTextSelectionEnabled = true,
            },
        };
        Grid.SetRow(code, 1);

        layout.Children.Add(header);
        layout.Children.Add(code);

        return new Border
        {
            Background = ThemeBrush("CardBackgroundFillColorSecondaryBrush"),
            BorderBrush = ThemeBrush("CardStrokeColorDefaultBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(10, 4, 4, 8),
            Child = layout,
        };
    }

    private ScrollViewer BuildTable(MarkdownBlock block)
    {
        var stroke = ThemeBrush("CardStrokeColorDefaultBrush");
        var columns = 0;
        foreach (var row in block.Rows)
            columns = Math.Max(columns, row.Cells.Count);

        var grid = new Grid();
        for (var column = 0; column < columns; column++)
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        for (var index = 0; index < block.Rows.Count; index++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var row = block.Rows[index];

            for (var column = 0; column < columns; column++)
            {
                var text = new TextBlock { TextWrapping = TextWrapping.Wrap };
                if (row.IsHeader)
                    text.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
                if (column < row.Cells.Count)
                    AppendInlines(text.Inlines, row.Cells[column]);

                var cell = new Border
                {
                    BorderBrush = stroke,
                    BorderThickness = new Thickness(0, 0, 1, 1),
                    Padding = new Thickness(8, 4, 8, 4),
                    Child = text,
                };
                Grid.SetRow(cell, index);
                Grid.SetColumn(cell, column);
                grid.Children.Add(cell);
            }
        }

        return new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollMode = ScrollMode.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollMode = ScrollMode.Disabled,
            HorizontalAlignment = HorizontalAlignment.Left,
            Content = new Border
            {
                BorderBrush = stroke,
                BorderThickness = new Thickness(1, 1, 0, 0),
                CornerRadius = new CornerRadius(4),
                Child = grid,
            },
        };
    }

    private void AppendInlines(InlineCollection target, IReadOnlyList<MarkdownInline> inlines)
    {
        foreach (var inline in inlines)
        {
            if (inline.Style.HasFlag(MarkdownInlineStyle.LineBreak))
            {
                target.Add(new LineBreak());
                continue;
            }

            var run = new Run { Text = inline.Text };

            if (inline.Style.HasFlag(MarkdownInlineStyle.Bold))
                run.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
            if (inline.Style.HasFlag(MarkdownInlineStyle.Italic))
                run.FontStyle = FontStyle.Italic;
            if (inline.Style.HasFlag(MarkdownInlineStyle.Strikethrough))
                run.TextDecorations = TextDecorations.Strikethrough;
            if (inline.Style.HasFlag(MarkdownInlineStyle.Code))
            {
                run.FontFamily = CodeFont;
                run.Foreground = ThemeBrush("TextFillColorSecondaryBrush");
            }

            target.Add(run);
        }
    }

    private static Brush? ThemeBrush(string key) =>
        Application.Current.Resources.TryGetValue(key, out var value) ? value as Brush : null;

    private void CopyCode_Click(object sender, RoutedEventArgs args)
    {
        if (sender is not Button { Tag: string code })
            return;

        var package = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
        package.SetText(code);
        Clipboard.SetContent(package);
    }
}

