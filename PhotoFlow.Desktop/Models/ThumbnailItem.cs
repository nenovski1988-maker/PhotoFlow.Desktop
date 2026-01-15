using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media.Imaging;

namespace PhotoFlow.Desktop.Models;

public sealed class ThumbnailItem : INotifyPropertyChanged
{
    private BitmapImage? _source;

    public string Barcode { get; set; } = "";
    public string FrameRawPath { get; set; } = "";
    public string Label { get; set; } = "";
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

    public BitmapImage? Source
    {
        get => _source;
        set
        {
            if (!ReferenceEquals(_source, value))
            {
                _source = value;
                OnPropertyChanged();
            }
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
