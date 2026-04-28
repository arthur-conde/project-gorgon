using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Palantir.Domain;

namespace Palantir.ViewModels;

public sealed partial class NotificationTesterViewModel : ObservableObject
{
    private readonly PalantirAttentionSource _source;

    public NotificationTesterViewModel(PalantirAttentionSource source)
    {
        _source = source;
        _source.Changed += (_, _) => OnPropertyChanged(nameof(CurrentCount));
    }

    public int CurrentCount => _source.Count;

    [ObservableProperty] private int _setCountInput = 3;

    [RelayCommand] private void Bump() => _source.Bump();
    [RelayCommand] private void Decrement() => _source.Decrement();
    [RelayCommand] private void Clear() => _source.Clear();
    [RelayCommand] private void ApplySetCount() => _source.SetCount(SetCountInput);
}
