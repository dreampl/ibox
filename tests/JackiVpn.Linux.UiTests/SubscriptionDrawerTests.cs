using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using JackiVpn.Linux.Ui;
using Xunit;

namespace JackiVpn.Linux.UiTests;

public sealed class SubscriptionDrawerTests
{
    [Fact]
    public async Task Subscription_forms_can_reopen_after_deletion_and_page_rebuild()
    {
        var previousRoot = Environment.GetEnvironmentVariable("JACKIVPN_DATA_ROOT");
        Environment.SetEnvironmentVariable("JACKIVPN_DATA_ROOT", Path.Combine(Path.GetTempPath(), "ibox-ui-test-" + Guid.NewGuid().ToString("N")));
        AppBuilder.Configure<GuiApp>().UseHeadless(new AvaloniaHeadlessPlatformOptions()).SetupWithoutStarting();
        var shell = new Shell();
        try
        {
            var form = new StackPanel { Children = { new TextBox { Text = "subscription input" } } };
            for (var i = 0; i < 20; i++)
            {
                shell.ShowDrawer("Add subscription", form); shell.CloseDrawer();
                Assert.Null(form.Parent);
            }
            shell.Subscriptions.Show();
            await shell.Services.Subscriptions.AddAsync(new Domain.Subscriptions.Subscription { Id = "remove-me", Name = "Test subscription", Host = "example.invalid", MaskedUrl = "https://example.invalid/sub/…" }, "https://example.invalid/sub/test", CancellationToken.None);
            await shell.Services.SubscriptionService.DeleteAsync("remove-me", CancellationToken.None);
            shell.Subscriptions.Reset();
            shell.Subscriptions.Show();
            shell.ShowDrawer("Add subscription", form); shell.CloseDrawer();
            Assert.Null(form.Parent);
        }
        finally
        {
            shell.Lifetime.Cancel();
            await shell.Services.DisposeAsync();
            Environment.SetEnvironmentVariable("JACKIVPN_DATA_ROOT", previousRoot);
        }
    }
}
