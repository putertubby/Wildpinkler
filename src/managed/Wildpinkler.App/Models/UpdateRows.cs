using System;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Wildpinkler.App.Models;

public sealed class AvailableUpdateRow : ObservableObject
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string VersionText { get; set; } = string.Empty;
    public string UpdatedText { get; set; } = string.Empty;
    public bool IsActionAvailable => false;
}

public sealed class TrackedModRow : ObservableObject
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string DomainText { get; set; } = string.Empty;
    public string AuthorText { get; set; } = string.Empty;
    public string VersionText { get; set; } = string.Empty;
    public string LastModifiedText { get; set; } = string.Empty;
    public string ModPageUrl { get; set; } = string.Empty;
    public bool IsActionAvailable => false;
}
