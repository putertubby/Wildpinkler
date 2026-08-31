using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Wildpinkler.App.Controls;

public sealed partial class MergedViewSection : UserControl
{
    public MergedViewSection()
    {
        InitializeComponent();
    }

    public static readonly DependencyProperty ViewNameProperty = DependencyProperty.Register(
        nameof(ViewName), typeof(string), typeof(MergedViewSection), new PropertyMetadata(string.Empty));
    public static readonly DependencyProperty MountPathProperty = DependencyProperty.Register(
        nameof(MountPath), typeof(string), typeof(MergedViewSection), new PropertyMetadata(string.Empty));
    public static readonly DependencyProperty BranchCountTextProperty = DependencyProperty.Register(
        nameof(BranchCountText), typeof(string), typeof(MergedViewSection), new PropertyMetadata(string.Empty));
    public static readonly DependencyProperty AccessLabelProperty = DependencyProperty.Register(
        nameof(AccessLabel), typeof(string), typeof(MergedViewSection), new PropertyMetadata(string.Empty));
    public static readonly DependencyProperty SourceLabelProperty = DependencyProperty.Register(
        nameof(SourceLabel), typeof(string), typeof(MergedViewSection), new PropertyMetadata(string.Empty));
    public static readonly DependencyProperty IsExpandedProperty = DependencyProperty.Register(
        nameof(IsExpanded), typeof(bool), typeof(MergedViewSection), new PropertyMetadata(false));
    public static readonly DependencyProperty BodyProperty = DependencyProperty.Register(
        nameof(Body), typeof(UIElement), typeof(MergedViewSection), new PropertyMetadata(null));

    public string ViewName { get => (string)GetValue(ViewNameProperty); set => SetValue(ViewNameProperty, value); }
    public string MountPath { get => (string)GetValue(MountPathProperty); set => SetValue(MountPathProperty, value); }
    public string BranchCountText { get => (string)GetValue(BranchCountTextProperty); set => SetValue(BranchCountTextProperty, value); }
    public string AccessLabel { get => (string)GetValue(AccessLabelProperty); set => SetValue(AccessLabelProperty, value); }
    public string SourceLabel { get => (string)GetValue(SourceLabelProperty); set => SetValue(SourceLabelProperty, value); }
    public bool IsExpanded { get => (bool)GetValue(IsExpandedProperty); set => SetValue(IsExpandedProperty, value); }
    public UIElement? Body { get => (UIElement?)GetValue(BodyProperty); set => SetValue(BodyProperty, value); }

}