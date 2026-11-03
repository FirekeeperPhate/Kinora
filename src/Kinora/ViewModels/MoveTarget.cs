using System.Windows.Input;

namespace Kinora.ViewModels;

/// <summary>A folder to open: one of those without a video, in "Needs attention".</summary>
public sealed record FolderLink(string Name, string Path, ICommand Command);

/// <summary>One entry of the "Move to" menu: a library folder and what clicking it does.</summary>
public sealed record MoveTarget(string Name, string Path, ICommand Command)
{
    public string Header => Name;
}
