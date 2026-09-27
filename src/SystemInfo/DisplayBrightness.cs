using System.ComponentModel;

namespace iTask.SystemInfo;

/// <summary>Brightness of one display (0–100), however that display happens to be controlled.</summary>
public abstract class DisplayBrightness : INotifyPropertyChanged
{
    public bool HasBrightness { get; protected set; }
    /// <summary>0–100.</summary>
    public int Brightness { get; protected set; }

    public string Description => HasBrightness ? $"Brightness {Brightness}%" : "Brightness not adjustable";

    public event PropertyChangedEventHandler? PropertyChanged;

    public abstract void SetBrightness(int percent);

    /// <summary>Re-reads the current level (it may have changed from the display's own buttons).</summary>
    public abstract void Refresh();

    protected void Publish() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
}
