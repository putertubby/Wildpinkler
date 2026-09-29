using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Wildpinkler.App.Services;
using Wildpinkler.App.Services.Games;
using Wildpinkler.App.Services.Profiles;

namespace Wildpinkler.App.Controls;

/// <summary>
/// Per-executable role dialog shown when a mod folder is added or re-enabled.
/// The user decides, for every executable found in the folder, whether it is the
/// game launcher, a tool (enabled by default), or should be skipped.
/// </summary>
public sealed partial class ModRolePickerDialog : ContentDialog
{
    private static readonly ModRole[] AllRoles =
    {
        ModRole.Launcher,
        ModRole.Tool,
        ModRole.Skip,
    };

    private readonly List<RoleRow> _rows = new();

    public ModRolePickerDialog(string modName, IReadOnlyList<ModRoleChoice> roles)
    {
        InitializeComponent();
        Style = (Style)Application.Current.Resources["DefaultContentDialogStyle"];
        Title = $"Choose roles for {modName}";

        var iconService = AppHost.Get<ToolIconService>();
        foreach (var choice in roles)
        {
            ImageSource? icon = null;
            try
            {
                icon = iconService.TryGetIcon(choice.ExecutablePath);
            }
            catch (Exception exception)
            {
                AppDiagnostics.Write(nameof(ModRolePickerDialog), exception);
            }

            _rows.Add(new RoleRow(choice, icon));
        }

        RoleList.ItemsSource = _rows;
    }

    /// <summary>
    /// The per-executable choices as edited in the dialog. Rows are in the same
    /// order as the choices passed to the constructor.
    /// </summary>
    public IReadOnlyList<ModRoleChoice> ChosenRoles
    {
        get
        {
            foreach (var row in _rows)
                row.SyncToChoice();

            return _rows.Select(row => row.Choice).ToList();
        }
    }

    /// <summary>
    /// Wraps a <see cref="ModRoleChoice"/> so the row can bind TwoWay to editable
    /// properties. Must stay a plain <see cref="INotifyPropertyChanged"/> (not
    /// CommunityToolkit.Mvvm.ObservableObject) - WinRT AOT source generation
    /// fails inside a ContentDialog (MVVMTK0045, same as ProfileModPickerDialog).
    /// </summary>
    private sealed class RoleRow : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;

        public RoleRow(ModRoleChoice choice, ImageSource? icon)
        {
            Choice = choice;
            Icon = icon;
        }

        public ModRoleChoice Choice { get; }

        public ImageSource? Icon { get; }

        public ModRole[] RoleOptions => AllRoles;

        public string Name => Choice.SuggestedName;

        public string Caption
        {
            get
            {
                var caption = Choice.Reason.Length > 0 ? $"{Choice.RelativePath} - {Choice.Reason}" : Choice.RelativePath;
                return caption;
            }
        }

        public bool IsToolRole => Role == ModRole.Tool;

        public ModRole Role
        {
            get => Choice.Role;
            set
            {
                if (Choice.Role == value)
                    return;

                Choice.Role = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Role)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsToolRole)));
            }
        }

        public bool IsEnabled
        {
            get => Choice.IsEnabled;
            set
            {
                if (Choice.IsEnabled == value)
                    return;

                Choice.IsEnabled = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsEnabled)));
            }
        }

        public void SyncToChoice()
        {
            Choice.Role = Role;
            Choice.IsEnabled = IsEnabled;
        }
    }
}
