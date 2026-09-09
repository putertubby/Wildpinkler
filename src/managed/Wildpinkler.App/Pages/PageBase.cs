using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Threading;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace Wildpinkler.App.Pages;

/// <summary>
/// Shared page behaviour: change notification, and a cancellation token whose lifetime matches the
/// time the page is on screen. Pages are cached and reused, so work started by one visit must not
/// still be writing to controls after the user has navigated away.
/// </summary>
[SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable", Justification = "The navigation token source is scoped to a single visit and disposed in OnNavigatedFrom; WinUI never disposes a Page.")]
public abstract class PageBase : Page, INotifyPropertyChanged
{
    private CancellationTokenSource? _navigation;

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Cancelled when the page is navigated away from.</summary>
    protected CancellationToken PageToken => _navigation?.Token ?? CancellationToken.None;

    protected bool SetProperty<T>(ref T storage, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(storage, value))
            return false;

        storage = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _navigation?.Dispose();
        _navigation = new CancellationTokenSource();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        _navigation?.Cancel();
        _navigation?.Dispose();
        _navigation = null;
        base.OnNavigatedFrom(e);
    }
}
