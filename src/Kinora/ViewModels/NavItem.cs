using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Kinora.ViewModels;

public enum NavKind
{
    Header,
    AllMovies,
    Unwatched,
    RecentlyAdded,
    NeedsMetadata,
    Root,
    Genre,
}

/// <summary>An entry of the sidebar: a view of the library, or a section header.</summary>
public partial class NavItem(NavKind kind, string title, Geometry? icon = null) : ObservableObject
{
    public NavKind Kind { get; } = kind;
    public string Title { get; } = title;
    public Geometry? Icon { get; } = icon;
    public long RootId { get; init; }
    public string? Genre { get; init; }

    /// <summary>Right click on a library folder.</summary>
    public System.Windows.Input.ICommand? MarkWatchedCommand { get; init; }
    public System.Windows.Input.ICommand? MarkUnwatchedCommand { get; init; }

    public bool IsHeader => Kind == NavKind.Header;
    public bool IsSelectable => !IsHeader;
    public bool IsRoot => Kind == NavKind.Root;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CountText))]
    public partial int Count { get; set; }

    [ObservableProperty]
    public partial bool IsUnavailable { get; set; }

    public string CountText => IsHeader ? "" : Count.ToString("N0");

    public bool SameAs(NavItem? other) =>
        other is not null && other.Kind == Kind && other.RootId == RootId && other.Genre == Genre;
}

public sealed record SortOption(string Label, Core.Settings.SortOrder Value)
{
    public override string ToString() => Label;
}
