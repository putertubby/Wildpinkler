using System;
using System.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Windows.UI.Text;

namespace Wildpinkler.App.Controls;

public sealed partial class AssistantMarkdown : UserControl
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text),
        typeof(string),
        typeof(AssistantMarkdown),
        new PropertyMetadata(string.Empty, OnTextChanged));

    public AssistantMarkdown()
    {
        InitializeComponent();
    }

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    private static void OnTextChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        ((AssistantMarkdown)sender).Render((string?)args.NewValue ?? string.Empty);
    }

    private void Render(string markdown)
    {
        ContentBlock.Blocks.Clear();
        var sanitized = AssistantMarkdownParser.Sanitize(markdown);
        var lines = sanitized.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var inCode = false;
        var code = new StringBuilder();

        foreach (var line in lines)
        {
            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                if (inCode)
                {
                    AddParagraph(code.ToString(), isCode: true);
                    code.Clear();
                }

                inCode = !inCode;
                continue;
            }

            if (inCode)
            {
                if (code.Length > 0)
                    code.AppendLine();
                code.Append(line);
                continue;
            }

            var trimmed = line.TrimStart();
            if (trimmed.StartsWith("# ", StringComparison.Ordinal))
                AddParagraph(trimmed[2..], isHeading: true);
            else if (trimmed.StartsWith("## ", StringComparison.Ordinal))
                AddParagraph(trimmed[3..], isHeading: true);
            else if (trimmed.StartsWith("### ", StringComparison.Ordinal))
                AddParagraph(trimmed[4..], isHeading: true);
            else if (trimmed.StartsWith("- ", StringComparison.Ordinal) || IsNumberedItem(trimmed))
                AddParagraph(trimmed, isList: true);
            else
                AddParagraph(line);
        }

        if (inCode || code.Length > 0)
            AddParagraph(code.ToString(), isCode: true);
    }

    private void AddParagraph(string text, bool isHeading = false, bool isList = false, bool isCode = false)
    {
        var paragraph = new Paragraph
        {
            Margin = new Thickness(0, 2, 0, isHeading ? 6 : 2),
        };

        if (isHeading)
            paragraph.FontSize = 18;
        if (isList)
            text = "• " + (text.StartsWith("- ", StringComparison.Ordinal) ? text[2..] : text[(text.IndexOf('.', StringComparison.Ordinal) + 1)..].TrimStart());

        AddInlineRuns(paragraph, text, isCode);
        ContentBlock.Blocks.Add(paragraph);
    }

    private static bool IsNumberedItem(string text)
    {
        var dot = text.IndexOf('.', StringComparison.Ordinal);
        return dot > 0 && dot <= 3 && int.TryParse(text[..dot], out _);
    }

    private static void AddInlineRuns(Paragraph paragraph, string text, bool isCode)
    {
        if (isCode)
        {
            paragraph.FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas");
            paragraph.Inlines.Add(new Run { Text = text });
            return;
        }

        var position = 0;
        while (position < text.Length)
        {
            var next = text.IndexOf('`', position);
            if (next < 0)
            {
                AddStyledText(paragraph, text[position..], FontStyle.Normal, false);
                break;
            }

            AddStyledText(paragraph, text[position..next], FontStyle.Normal, false);
            var end = text.IndexOf('`', next + 1);
            if (end < 0)
            {
                AddStyledText(paragraph, text[next..], FontStyle.Normal, false);
                break;
            }

            AddStyledText(paragraph, text[(next + 1)..end], FontStyle.Normal, true);
            position = end + 1;
        }
    }

    private static void AddStyledText(Paragraph paragraph, string text, FontStyle style, bool code)
    {
        if (text.Length == 0)
            return;

        if (code)
        {
            var span = new Span { FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas") };
            span.Inlines.Add(new Run { Text = text });
            paragraph.Inlines.Add(span);
        }
        else
        {
            paragraph.Inlines.Add(new Run { Text = text });
        }
    }
}
