using System.ComponentModel;

namespace LiveSub;

public sealed class HistoryItem : INotifyPropertyChanged
{
    private string _chinese;

    public HistoryItem(string time, string english, string chinese)
    {
        Time = time;
        English = english;
        _chinese = chinese;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Time { get; }
    public string English { get; }
    public bool Final { get; set; }

    public string Chinese
    {
        get => _chinese;
        set
        {
            if (_chinese == value) return;
            _chinese = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Chinese)));
        }
    }
}
