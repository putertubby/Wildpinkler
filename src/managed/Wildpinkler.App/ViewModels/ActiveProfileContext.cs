using System.ComponentModel;

namespace Wildpinkler.App.ViewModels;

public sealed class ActiveProfileContext : INotifyPropertyChanged
{
    private string _profileName = "No profile selected";
    private string _gameName = "No game selected";

    public event PropertyChangedEventHandler? PropertyChanged;

    public string ProfileName
    {
        get => _profileName;
        set
        {
            if (_profileName == value)
                return;
            _profileName = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ProfileName)));
        }
    }

    public string GameName
    {
        get => _gameName;
        set
        {
            if (_gameName == value)
                return;
            _gameName = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(GameName)));
        }
    }
}
