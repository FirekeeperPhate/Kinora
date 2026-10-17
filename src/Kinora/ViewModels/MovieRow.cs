namespace Kinora.ViewModels;

/// <summary>
/// One row of the poster grid. The grid is a virtualized list of rows, rebuilt when the window
/// width changes: only the rows on screen exist as controls, even with thousands of movies.
/// </summary>
public sealed class MovieRow(IReadOnlyList<CardViewModel> items)
{
    public IReadOnlyList<CardViewModel> Items { get; } = items;
}
