using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;

namespace JackiVpn.Desktop.ViewModels;

public sealed record WorkspaceTab(string Title, object Page);

public sealed partial class WorkspaceViewModel : ObservableObject
{
    public WorkspaceViewModel(params WorkspaceTab[] tabs)
    {
        Tabs = tabs;
        _selectedTab = tabs[0];
    }

    public IReadOnlyList<WorkspaceTab> Tabs { get; }

    [ObservableProperty]
    private WorkspaceTab _selectedTab;
}
