using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace NvidiaCacheMover.Models;

public enum CacheLinkState
{
    NotCreatedYet,
    LocalOnDefaultDrive,
    LinkedToTarget,
    LinkedElsewhere
}

public sealed class CacheItemModel : INotifyPropertyChanged
{
    private bool _isSelected = true;
    private CacheLinkState _state;
    private string _statusText = "Checking...";
    private string _badgeLabel = "CHECKING";
    private string _currentPhysicalPath = "";
    private long _sizeBytes;
    private string _sizeText = "0 MB";

    public required string Id { get; init; }
    public required string DisplayName { get; init; }
    public required string Description { get; init; }
    public required string DefaultSystemPath { get; init; }
    public required string TargetSubfolderName { get; init; }
    public required string IconData { get; init; }
    public string[] ExpectedChildFolders { get; init; } = [];
    public string[] ExcludedSiblingFoldersWhenSharedRoot { get; init; } = [];
    public bool SetsOpenGlEnvVar { get; init; }

    public bool IsSelected
    {
        get => _isSelected;
        set { if (_isSelected != value) { _isSelected = value; OnPropertyChanged(); } }
    }

    public CacheLinkState State
    {
        get => _state;
        set
        {
            if (_state != value)
            {
                _state = value;
                BadgeLabel = value switch
                {
                    CacheLinkState.LinkedToTarget => "LINKED",
                    CacheLinkState.LinkedElsewhere => "LINKED",
                    CacheLinkState.NotCreatedYet => "STANDBY",
                    _ => "LOCAL C:"
                };
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsLinked));
            }
        }
    }

    public bool IsLinked => _state is CacheLinkState.LinkedToTarget or CacheLinkState.LinkedElsewhere;

    public string BadgeLabel
    {
        get => _badgeLabel;
        private set { if (_badgeLabel != value) { _badgeLabel = value; OnPropertyChanged(); } }
    }

    public string StatusText
    {
        get => _statusText;
        set { if (_statusText != value) { _statusText = value; OnPropertyChanged(); } }
    }

    public string CurrentPhysicalPath
    {
        get => _currentPhysicalPath;
        set { if (_currentPhysicalPath != value) { _currentPhysicalPath = value; OnPropertyChanged(); } }
    }

    public long SizeBytes
    {
        get => _sizeBytes;
        set
        {
            if (_sizeBytes != value)
            {
                _sizeBytes = value;
                SizeText = FormatBytes(value);
                OnPropertyChanged();
            }
        }
    }

    public string SizeText
    {
        get => _sizeText;
        private set { if (_sizeText != value) { _sizeText = value; OnPropertyChanged(); } }
    }

    public static string FormatBytes(long bytes)
    {
        if (bytes <= 0) return "0 MB";
        double mb = bytes / (1024.0 * 1024.0);
        if (mb >= 1024.0)
            return $"{mb / 1024.0:F2} GB";
        if (mb >= 1.0)
            return $"{mb:F1} MB";
        return $"{bytes / 1024.0:F0} KB";
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class DriveCardModel : INotifyPropertyChanged
{
    private bool _isSelectedDrive;

    public required string DriveRoot { get; init; }
    public required string DriveLetter { get; init; }
    public required string VolumeName { get; init; }
    public required string FreeText { get; init; }
    public required double UsedRatio { get; init; }
    public required bool IsSystemDrive { get; init; }
    public string SuggestedPath => System.IO.Path.Combine(DriveRoot, "NVIDIA_Cache");

    public bool IsSelectedDrive
    {
        get => _isSelectedDrive;
        set { if (_isSelectedDrive != value) { _isSelectedDrive = value; OnPropertyChanged(); } }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
