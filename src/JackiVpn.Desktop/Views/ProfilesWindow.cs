using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using JackiVpn.Application.Accounts;
using JackiVpn.Desktop.Composition;
using JackiVpn.Domain.Connection;

namespace JackiVpn.Desktop.Views;

/// <summary>A round profile avatar: the picture, or the initials on the profile's colour.</summary>
public sealed class AvatarView : FrameworkElement
{
    public static readonly DependencyProperty InitialsProperty = DependencyProperty.Register(
        nameof(Initials), typeof(string), typeof(AvatarView), new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ColorIndexProperty = DependencyProperty.Register(
        nameof(ColorIndex), typeof(int), typeof(AvatarView), new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty PicturePathProperty = DependencyProperty.Register(
        nameof(PicturePath), typeof(string), typeof(AvatarView), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public string Initials { get => (string)GetValue(InitialsProperty); set => SetValue(InitialsProperty, value); }

    public int ColorIndex { get => (int)GetValue(ColorIndexProperty); set => SetValue(ColorIndexProperty, value); }

    public string? PicturePath { get => (string?)GetValue(PicturePathProperty); set => SetValue(PicturePathProperty, value); }

    public static AvatarView For(LocalAccountService accounts, LocalAccount? account, double size) => new()
    {
        Width = size,
        Height = size,
        Initials = account?.Initials ?? string.Empty,
        ColorIndex = account?.Color ?? 0,
        PicturePath = account is null ? null : accounts.PicturePath(account),
    };

    protected override void OnRender(DrawingContext dc)
    {
        ArgumentNullException.ThrowIfNull(dc);
        var size = Math.Min(ActualWidth, ActualHeight);
        if (size < 4)
        {
            return;
        }

        var center = new Point(ActualWidth / 2, ActualHeight / 2);
        var circle = new EllipseGeometry(center, size / 2, size / 2);
        if (PicturePath is { } path && File.Exists(path))
        {
            try
            {
                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.UriSource = new Uri(path);
                image.DecodePixelWidth = (int)Math.Ceiling(size * 2);
                image.EndInit();
                dc.PushClip(circle);
                dc.DrawImage(image, new Rect(center.X - (size / 2), center.Y - (size / 2), size, size));
                dc.Pop();
                return;
            }
            catch (Exception ex) when (ex is IOException or NotSupportedException or UriFormatException)
            {
                // initials below
            }
        }

        var argb = LocalAccountService.AvatarColors[Math.Clamp(ColorIndex, 0, LocalAccountService.AvatarColors.Count - 1)];
        var c = Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);
        var dark = Color.FromArgb(255, (byte)(c.R * 0.75), (byte)(c.G * 0.75), (byte)(c.B * 0.75));
        dc.DrawGeometry(new LinearGradientBrush(c, dark, 45), null, circle);
        var text = new FormattedText(string.IsNullOrEmpty(Initials) ? "?" : Initials, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI Variable Text, Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal),
            size * 0.38, Brushes.White, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        dc.DrawText(text, new Point(center.X - (text.Width / 2), center.Y - (text.Height / 2)));
    }
}

/// <summary>Profiles: switch (PIN when locked), add, rename, colour, picture, PIN, delete.</summary>
internal sealed class ProfilesWindow
{
    private readonly AppServices _s;
    private readonly StackPanel _list = new();
    private Window? _window;

    public ProfilesWindow(AppServices services) => _s = services;

    public void Show()
    {
        _window = DialogService.CreateWindow("Profiles", 460);
        var root = new StackPanel { Margin = new Thickness(24, 20, 24, 20) };
        root.Children.Add(new TextBlock { Text = "Profiles", FontSize = 17, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4) });
        root.Children.Add(DialogService.Styled(new TextBlock { Text = "Each profile has its own settings (routing, DNS, modes, theme…), stored encrypted. Servers and subscriptions are shared.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 14) }, "Text.Hint"));
        root.Children.Add(_list);
        var add = DialogService.Styled(new Button { Content = "Add profile", HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(0, 12, 0, 0), Padding = new Thickness(0, 9, 0, 9) }, "Button.Outline");
        add.Click += (_, _) => _ = EditAsync(null);
        root.Children.Add(add);

        // Moving to another device (Windows, Linux or Android): one encrypted .ibox file.
        var transfer = new Grid { Margin = new Thickness(0, 8, 0, 0) };
        transfer.ColumnDefinitions.Add(new ColumnDefinition());
        transfer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
        transfer.ColumnDefinitions.Add(new ColumnDefinition());
        var export = DialogService.Styled(new Button { Content = "Export account…", Padding = new Thickness(0, 9, 0, 9), ToolTip = "Save this profile, its settings, subscriptions and servers to a password-protected file for another device" }, "Button.Outline");
        export.Click += (_, _) => _ = ExportAsync();
        var import = DialogService.Styled(new Button { Content = "Import account…", Padding = new Thickness(0, 9, 0, 9), ToolTip = "Open an .ibox file from Windows, Linux or Android" }, "Button.Outline");
        import.Click += (_, _) => _ = ImportAsync();
        Grid.SetColumn(import, 2);
        transfer.Children.Add(export);
        transfer.Children.Add(import);
        root.Children.Add(transfer);
        root.Children.Add(DialogService.Styled(new TextBlock { Text = "The file works on Windows, Linux and Android; IBOX adapts it to the device it is opened on.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) }, "Text.Hint"));
        var close = new Button { Content = "Close", IsCancel = true, MinWidth = 90, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        close.Click += (_, _) => _window.Close();
        root.Children.Add(close);
        _window.Content = new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Render();
        _window.ShowDialog();
    }

    private async Task ExportAsync()
    {
        if (AskPassword("Export account", "Choose a password for the file. You need it to open the file on the other device.", confirm: true) is not { } password)
        {
            return;
        }

        var dlg = new Microsoft.Win32.SaveFileDialog { Filter = "IBOX account (*.ibox)|*.ibox", FileName = _s.AccountTransfer.SuggestedFileName(), OverwritePrompt = true };
        if (dlg.ShowDialog(_window) != true)
        {
            return;
        }

        try
        {
            await File.WriteAllBytesAsync(dlg.FileName, await _s.AccountTransfer.ExportAsync(password, _s.Lifetime));
            DialogService.Show("Account exported", "Saved to " + dlg.FileName + ". Copy it to the other device and choose Import account there.", null, "OK", null, false, 480, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            DialogService.Show("Export failed", ex.Message, null, "OK", null, false, 480, null);
        }
    }

    private async Task ImportAsync()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "IBOX account (*.ibox)|*.ibox|All files (*.*)|*.*", CheckFileExists = true };
        if (dlg.ShowDialog(_window) != true)
        {
            return;
        }

        if (new FileInfo(dlg.FileName).Length > Application.Accounts.AccountTransfer.MaxFileBytes
            || AskPassword("Import account", "The password the file was exported with.", confirm: false) is not { } password)
        {
            return;
        }

        // A new profile becomes active: like switching, disconnect first.
        if (_s.Orchestrator.State != Domain.Connection.ConnectionState.Disconnected)
        {
            await _s.Orchestrator.DisconnectAsync();
        }

        var (result, problem) = await _s.AccountTransfer.ImportAsync(await File.ReadAllBytesAsync(dlg.FileName), password, _s.Lifetime);
        if (result is null)
        {
            DialogService.Show("Import failed", problem ?? "Unknown error.", null, "OK", null, false, 480, null);
            return;
        }

        Render();
        var notes = result.Notes.Count > 0 ? "\n\n" + string.Join("\n", result.Notes) : string.Empty;
        DialogService.Show("Account imported", $"\"{result.Name}\" from {result.From} is now the active profile: {result.Subscriptions} subscriptions (updating now) and {result.Servers} servers.{notes}", null, "OK", null, false, 480, null);
    }

    /// <summary>A password for an account file (typed twice when choosing one); null when cancelled.</summary>
    private string? AskPassword(string title, string message, bool confirm)
    {
        var first = new PasswordBox();
        var second = new PasswordBox();
        var error = DialogService.Styled(new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) }, "Text.Hint");
        error.SetResourceReference(TextBlock.ForegroundProperty, "B.Danger");
        var panel = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
        panel.Children.Add(Label("Password"));
        panel.Children.Add(first);
        if (confirm)
        {
            panel.Children.Add(Label("Again"));
            panel.Children.Add(second);
        }

        panel.Children.Add(error);
        while (DialogService.Show(title, message, panel, "OK", "Cancel", false, 440, first))
        {
            var problem = confirm ? Application.Accounts.AccountTransfer.PasswordProblem(first.Password) ?? (first.Password != second.Password ? "The two passwords differ." : null)
                : first.Password.Length == 0 ? "Enter the password." : null;
            if (problem is null)
            {
                return first.Password;
            }

            error.Text = problem;
            (panel.Parent as Panel)?.Children.Remove(panel);
        }

        return null;
    }

    private void Render()
    {
        _list.Children.Clear();
        foreach (var a in _s.Accounts.Accounts)
        {
            var account = a;
            var active = account.Id == _s.Accounts.Active?.Id;
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
            var avatar = AvatarView.For(_s.Accounts, account, 38);
            avatar.Margin = new Thickness(0, 0, 12, 0);
            DockPanel.SetDock(avatar, Dock.Left);
            row.Children.Add(avatar);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(buttons, Dock.Right);
            if (!active)
            {
                var use = DialogService.Styled(new Button { Content = "Switch", Margin = new Thickness(0, 0, 6, 0), Padding = new Thickness(12, 4, 12, 4) }, "Button.Primary");
                use.Click += (_, _) => _ = SwitchAsync(account);
                buttons.Children.Add(use);
            }

            var edit = new Button { Content = "Edit", Padding = new Thickness(12, 4, 12, 4) };
            edit.Click += (_, _) => _ = EditAsync(account);
            buttons.Children.Add(edit);
            row.Children.Add(buttons);
            var name = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            name.Children.Add(new TextBlock { Text = account.Name, FontSize = 14, FontWeight = FontWeights.SemiBold });
            name.Children.Add(DialogService.Styled(new TextBlock { Text = (active ? "Active" : "Saved") + (account.IsLocked ? " · PIN" : string.Empty) }, "Text.Hint"));
            row.Children.Add(name);
            _list.Children.Add(row);
        }
    }

    private async Task SwitchAsync(LocalAccount account)
    {
        string? pin = null;
        if (account.IsLocked)
        {
            pin = await new DialogService().PromptAsync("PIN", $"PIN for {account.Name}:");
            if (string.IsNullOrEmpty(pin))
            {
                return;
            }
        }

        if (_s.Orchestrator.State != ConnectionState.Disconnected)
        {
            if (!new DialogService().Confirm("Switch profile", $"Disconnect and switch to {account.Name}? Their settings apply from the next connection."))
            {
                return;
            }

            await _s.Orchestrator.DisconnectAsync();
        }

        if (await _s.Accounts.SwitchAsync(account.Id, pin, CancellationToken.None) is { } problem)
        {
            new DialogService().Info("Profile", problem);
            return;
        }

        Render();
    }

    private async Task EditAsync(LocalAccount? account)
    {
        var window = DialogService.CreateWindow(account is null ? "Add profile" : "Edit profile", 440);
        var root = new StackPanel { Margin = new Thickness(24, 20, 24, 20) };
        root.Children.Add(new TextBlock { Text = account is null ? "Add profile" : "Edit profile", FontSize = 17, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 12) });
        var name = new TextBox { Text = account?.Name ?? string.Empty };
        root.Children.Add(Label("Name"));
        root.Children.Add(name);
        var color = account?.Color ?? _s.Accounts.Accounts.Count % LocalAccountService.AvatarColors.Count;
        var swatches = new StackPanel { Orientation = Orientation.Horizontal };
        void ShowSwatches()
        {
            swatches.Children.Clear();
            for (var i = 0; i < LocalAccountService.AvatarColors.Count; i++)
            {
                var index = i;
                var argb = LocalAccountService.AvatarColors[i];
                var swatch = new Border
                {
                    Width = 26,
                    Height = 26,
                    CornerRadius = new CornerRadius(13),
                    Margin = new Thickness(0, 0, 8, 0),
                    Cursor = Cursors.Hand,
                    Background = new SolidColorBrush(Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb)),
                    BorderThickness = new Thickness(index == color ? 2.5 : 0),
                };
                swatch.SetResourceReference(Border.BorderBrushProperty, "B.Text");
                swatch.MouseLeftButtonUp += (_, _) =>
                {
                    color = index;
                    ShowSwatches();
                };
                swatches.Children.Add(swatch);
            }
        }

        ShowSwatches();
        root.Children.Add(Label("Colour"));
        root.Children.Add(swatches);
        string? picture = null;
        var pictureButton = new Button { Content = "Choose picture…", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 12, 0, 0), Visibility = account is null ? Visibility.Collapsed : Visibility.Visible };
        pictureButton.Click += (_, _) =>
        {
            picture = new DialogService().OpenFile("Pictures|*.png;*.jpg;*.jpeg");
            pictureButton.Content = picture is null ? "Choose picture…" : Path.GetFileName(picture);
        };
        root.Children.Add(pictureButton);
        var copy = DialogService.Styled(new CheckBox { IsChecked = true, Margin = new Thickness(0, 12, 0, 0), Content = new TextBlock { Text = "Start from my current settings" }, Visibility = account is null ? Visibility.Visible : Visibility.Collapsed }, "Toggle");
        root.Children.Add(copy);
        root.Children.Add(DialogService.Styled(new TextBlock { Text = "A PIN encrypts this profile's settings with a key only the PIN opens; it is asked when switching to it.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 14, 0, 6) }, "Text.Hint"));
        var oldPin = new PasswordBox { Visibility = account?.IsLocked == true ? Visibility.Visible : Visibility.Collapsed, Margin = new Thickness(0, 0, 0, 6), ToolTip = "Current PIN" };
        var newPin = new PasswordBox { ToolTip = account?.IsLocked == true ? "New PIN (empty = keep)" : "PIN (optional, 4–12 digits)" };
        var removePin = DialogService.Styled(new CheckBox { Margin = new Thickness(0, 8, 0, 0), Content = new TextBlock { Text = "Remove the PIN" }, Visibility = account?.IsLocked == true ? Visibility.Visible : Visibility.Collapsed }, "Toggle");
        if (account?.IsLocked == true)
        {
            root.Children.Add(Label("Current PIN"));
        }

        root.Children.Add(oldPin);
        root.Children.Add(Label(account?.IsLocked == true ? "New PIN" : "PIN (optional)"));
        root.Children.Add(newPin);
        root.Children.Add(removePin);
        var error = DialogService.Styled(new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0) }, "Text.Hint");
        error.SetResourceReference(TextBlock.ForegroundProperty, "B.Danger");
        root.Children.Add(error);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        if (account is not null && account.Id != _s.Accounts.Active?.Id)
        {
            var delete = DialogService.Styled(new Button { Content = "Delete", MinWidth = 90, Margin = new Thickness(0, 0, 8, 0) }, "Button.Danger");
            delete.Click += async (_, _) =>
            {
                if (new DialogService().Confirm("Delete profile", $"Delete {account.Name} and their settings?", danger: true))
                {
                    await _s.Accounts.DeleteAsync(account.Id, CancellationToken.None);
                    window.Close();
                }
            };
            buttons.Children.Add(delete);
        }

        buttons.Children.Add(new Button { Content = "Cancel", IsCancel = true, MinWidth = 90, Margin = new Thickness(0, 0, 8, 0) });
        var save = DialogService.Styled(new Button { Content = account is null ? "Add" : "Save", MinWidth = 90 }, "Button.Primary");
        save.Click += async (_, _) =>
        {
            var pin = string.IsNullOrEmpty(newPin.Password) ? null : newPin.Password;
            string? problem;
            if (account is null)
            {
                (_, problem) = await _s.Accounts.CreateAsync(name.Text, color, copy.IsChecked == true, pin, CancellationToken.None);
            }
            else
            {
                problem = await _s.Accounts.RenameAsync(account.Id, name.Text, color, CancellationToken.None);
                if (problem is null && picture is not null)
                {
                    problem = await _s.Accounts.SetPictureAsync(account.Id, picture, CancellationToken.None);
                }

                if (problem is null && (pin is not null || removePin.IsChecked == true))
                {
                    problem = await _s.Accounts.SetPinAsync(account.Id, string.IsNullOrEmpty(oldPin.Password) ? null : oldPin.Password, removePin.IsChecked == true ? null : pin, CancellationToken.None);
                }
            }

            if (problem is not null)
            {
                error.Text = problem;
                return;
            }

            window.DialogResult = true;
        };
        buttons.Children.Add(save);
        root.Children.Add(buttons);
        window.Content = new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        window.Loaded += (_, _) => Keyboard.Focus(name);
        window.ShowDialog();
        Render();
        await Task.CompletedTask;
    }

    private static TextBlock Label(string text) => DialogService.Styled(new TextBlock { Text = text, Margin = new Thickness(0, 12, 0, 6) }, "Text.Label");
}
