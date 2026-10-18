using System.Windows.Input;

namespace Kinora.ViewModels;

/// <summary>One entry of the "Move to" menu: a library folder and what clicking it does.</summary>
public sealed record MoveTarget(string Name, string Path, ICommand Command)
{
    public string Header => Name;
}
