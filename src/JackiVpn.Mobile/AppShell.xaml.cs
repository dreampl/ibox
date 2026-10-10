namespace JackiVpn.Mobile;

public partial class AppShell : Shell
{
    public AppShell()
    {
        InitializeComponent();
        Microsoft.Maui.Controls.Routing.RegisterRoute("apps", typeof(Pages.AppsPage));
        Microsoft.Maui.Controls.Routing.RegisterRoute("ecosystem", typeof(Pages.EcosystemPage));
        Microsoft.Maui.Controls.Routing.RegisterRoute("usage", typeof(Pages.UsagePage));
        Microsoft.Maui.Controls.Routing.RegisterRoute("island", typeof(Pages.IslandPage));
    }
}
