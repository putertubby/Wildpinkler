using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Wildpinkler.App.Agent;

namespace Wildpinkler.App.Controls;

/// <summary>Picks a row template by entry kind so a tool card and a message are not the same shape.</summary>
public sealed class ChatEntryTemplateSelector : DataTemplateSelector
{
    public DataTemplate? User { get; set; }

    public DataTemplate? Assistant { get; set; }

    public DataTemplate? Tool { get; set; }

    public DataTemplate? Approval { get; set; }

    public DataTemplate? Notice { get; set; }

    protected override DataTemplate? SelectTemplateCore(object item) => item is ChatEntry entry
        ? entry.Kind switch
        {
            ChatEntryKind.User => User,
            ChatEntryKind.Tool => Tool,
            ChatEntryKind.Approval => Approval,
            ChatEntryKind.Notice => Notice,
            _ => Assistant,
        }
        : Assistant;

    protected override DataTemplate? SelectTemplateCore(object item, DependencyObject container) =>
        SelectTemplateCore(item);
}
