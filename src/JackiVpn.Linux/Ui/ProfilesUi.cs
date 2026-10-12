using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using JackiVpn.Application.Accounts;
using JackiVpn.Domain.Connection;
using static JackiVpn.Linux.Ui.Ui;

namespace JackiVpn.Linux.Ui;

/// <summary>Profile picker (from the rail), the add/edit dialog and the PIN prompt.</summary>
internal static class ProfilesUi
{
    /// <summary>A round avatar: the profile's picture, or its initials on its colour.</summary>
    public static Control Avatar(LocalAccountService accounts, LocalAccount? account, double size)
    {
        if (account is null)
        {
            return new Border { Width = size, Height = size, CornerRadius = new CornerRadius(size / 2), Background = B(TileBg), Child = Icon(Icons.User, size * 0.45, Text2, 1.8) };
        }

        if (accounts.PicturePath(account) is { } path && File.Exists(path))
        {
            try
            {
                return new Border
                {
                    Width = size,
                    Height = size,
                    CornerRadius = new CornerRadius(size / 2),
                    ClipToBounds = true,
                    BorderBrush = B(Ui.Border),
                    BorderThickness = new Thickness(1),
                    Child = new Image { Source = new Bitmap(path), Stretch = Stretch.UniformToFill },
                };
            }
            catch (Exception ex) when (ex is IOException or ArgumentException or InvalidOperationException)
            {
                // fall back to initials
            }
        }

        var color = Color.FromUInt32(LocalAccountService.AvatarColors[Math.Clamp(account.Color, 0, LocalAccountService.AvatarColors.Count - 1)]);
        return new Border
        {
            Width = size,
            Height = size,
            CornerRadius = new CornerRadius(size / 2),
            Background = Gradient(color, Color.FromArgb(255, (byte)(color.R * 0.75), (byte)(color.G * 0.75), (byte)(color.B * 0.75))),
            Child = new TextBlock { Text = account.Initials, FontSize = size * 0.38, FontWeight = FontWeight.SemiBold, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
        };
    }

    public static void ShowPicker(Shell shell, Control anchor)
    {
        var accounts = shell.Services.Accounts;
        var list = new StackPanel { Spacing = 6, Width = 300 };
        list.Children.Add(new TextBlock { Text = "Profiles", FontSize = 16, FontWeight = FontWeight.SemiBold, Foreground = B(Text), Margin = new Thickness(4, 0, 0, 6) });
        Flyout? flyout = null;
        foreach (var a in accounts.Accounts)
        {
            var account = a;
            var active = account.Id == accounts.Active?.Id;
            var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
            if (account.IsLocked)
            {
                right.Children.Add(Icon(Icons.Shield, 14, Muted, 2));
            }

            if (active)
            {
                right.Children.Add(Badge("Active", Success));
            }

            var edit = new Button { Background = Brushes.Transparent, Padding = new Thickness(6), Content = Icon(Icons.Advanced, 15, Text2, 1.8) };
            ToolTip.SetTip(edit, "Edit");
            edit.Click += (_, e) =>
            {
                e.Handled = true;
                flyout?.Hide();
                _ = EditAsync(shell, account);
            };
            right.Children.Add(edit);
            DockPanel.SetDock(right, Dock.Right);
            var avatar = Avatar(accounts, account, 36);
            avatar.Margin = new Thickness(0, 0, 12, 0);
            DockPanel.SetDock(avatar, Dock.Left);
            var row = new Border
            {
                CornerRadius = new CornerRadius(14),
                Padding = new Thickness(8, 6),
                Background = active ? B(Accent, 0.1) : Brushes.Transparent,
                Cursor = new Cursor(StandardCursorType.Hand),
                Child = new DockPanel { Children = { right, avatar, new TextBlock { Text = account.Name, FontSize = 14, Foreground = B(Text), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis } } },
            };
            row.PointerEntered += (_, _) => row.Background = active ? B(Accent, 0.14) : B(TileBg);
            row.PointerExited += (_, _) => row.Background = active ? B(Accent, 0.1) : Brushes.Transparent;
            row.PointerPressed += (_, _) =>
            {
                flyout?.Hide();
                if (!active)
                {
                    _ = SwitchAsync(shell, account);
                }
            };
            list.Children.Add(row);
        }

        var add = Ghost("Add profile", () =>
        {
            flyout?.Hide();
            _ = EditAsync(shell, null);
        });
        add.HorizontalAlignment = HorizontalAlignment.Stretch;
        add.HorizontalContentAlignment = HorizontalAlignment.Center;
        add.Margin = new Thickness(0, 8, 0, 0);
        list.Children.Add(add);
        list.Children.Add(Hint("Each profile has its own settings (routing, DNS, modes, theme…), stored encrypted. Servers are shared."));
        flyout = new Flyout { Content = new Border { Padding = new Thickness(6), Child = list }, Placement = PlacementMode.RightEdgeAlignedBottom };
        flyout.ShowAt(anchor);
    }

    private static async Task SwitchAsync(Shell shell, LocalAccount account)
    {
        var s = shell.Services;
        string? pin = null;
        if (account.IsLocked)
        {
            pin = await PromptPinAsync(shell, $"PIN for {account.Name}");
            if (pin is null)
            {
                return;
            }
        }

        if (!ConnectionStateRules.IsIdle(s.Orchestrator.State) || s.Orchestrator.State == ConnectionState.Connected)
        {
            if (!await shell.ConfirmAsync("Switch profile", $"Disconnect and switch to {account.Name}? Their own settings apply from the next connection.", "Switch"))
            {
                return;
            }

            await s.Orchestrator.DisconnectAsync();
        }

        if (await s.Accounts.SwitchAsync(account.Id, pin, CancellationToken.None) is { } problem)
        {
            shell.Toast("Profile", problem, warning: true);
            return;
        }

        shell.Toast("Profile", $"Now {account.Name}.");
    }

    /// <summary>Add (account null) or edit a profile: name, colour, picture, PIN; delete.</summary>
    private static async Task EditAsync(Shell shell, LocalAccount? account)
    {
        var accounts = shell.Services.Accounts;
        var dialog = new Window
        {
            Title = account is null ? "Add profile" : "Edit profile",
            SizeToContent = SizeToContent.WidthAndHeight,
            CanResize = false,
            Background = B(Look.P.Window),
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        var color = account?.Color ?? accounts.Accounts.Count % LocalAccountService.AvatarColors.Count;
        var name = new TextBox { Text = account?.Name ?? string.Empty, Width = 320, PlaceholderText = "Name" };
        var swatches = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        void ShowSwatches()
        {
            swatches.Children.Clear();
            for (var i = 0; i < LocalAccountService.AvatarColors.Count; i++)
            {
                var index = i;
                var c = Color.FromUInt32(LocalAccountService.AvatarColors[i]);
                var swatch = new Border
                {
                    Width = 28,
                    Height = 28,
                    CornerRadius = new CornerRadius(14),
                    Background = B(c),
                    BorderBrush = B(Text),
                    BorderThickness = new Thickness(index == color ? 2.5 : 0),
                    Cursor = new Cursor(StandardCursorType.Hand),
                };
                swatch.PointerPressed += (_, _) =>
                {
                    color = index;
                    ShowSwatches();
                };
                swatches.Children.Add(swatch);
            }
        }

        ShowSwatches();
        var copy = new CheckBox { Content = "Start from my current settings", IsChecked = true, IsVisible = account is null };
        var pin = new TextBox { PasswordChar = '•', Width = 160, PlaceholderText = account?.IsLocked == true ? "New PIN (empty = keep)" : "PIN (optional)" };
        var oldPin = new TextBox { PasswordChar = '•', Width = 160, PlaceholderText = "Current PIN", IsVisible = account?.IsLocked == true };
        var removePin = new CheckBox { Content = "Remove the PIN", IsVisible = account?.IsLocked == true };
        var error = new TextBlock { Foreground = B(Danger), FontSize = 12, TextWrapping = TextWrapping.Wrap, MaxWidth = 360, IsVisible = false };
        string? picture = null;
        var pictureButton = Ghost("Choose picture…", () => { });
        pictureButton.IsVisible = account is not null;
        pictureButton.Click += async (_, _) =>
        {
            var files = await dialog.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Profile picture",
                AllowMultiple = false,
                FileTypeFilter = [new FilePickerFileType("Pictures") { Patterns = ["*.png", "*.jpg", "*.jpeg"] }],
            });
            picture = files.Count > 0 ? files[0].TryGetLocalPath() : null;
            pictureButton.Content = picture is null ? "Choose picture…" : Path.GetFileName(picture);
        };

        void Fail(string text)
        {
            error.Text = text;
            error.IsVisible = true;
        }

        var save = Primary(account is null ? "Add" : "Save", async () =>
        {
            var newPin = string.IsNullOrEmpty(pin.Text) ? null : pin.Text;
            if (account is null)
            {
                var (_, problem) = await accounts.CreateAsync(name.Text ?? string.Empty, color, copy.IsChecked == true, newPin, CancellationToken.None);
                if (problem is not null)
                {
                    Fail(problem);
                    return;
                }
            }
            else
            {
                if (await accounts.RenameAsync(account.Id, name.Text ?? string.Empty, color, CancellationToken.None) is { } problem)
                {
                    Fail(problem);
                    return;
                }

                if (picture is not null && await accounts.SetPictureAsync(account.Id, picture, CancellationToken.None) is { } pictureProblem)
                {
                    Fail(pictureProblem);
                    return;
                }

                if ((newPin is not null || removePin.IsChecked == true)
                    && await accounts.SetPinAsync(account.Id, string.IsNullOrEmpty(oldPin.Text) ? null : oldPin.Text, removePin.IsChecked == true ? null : newPin, CancellationToken.None) is { } pinProblem)
                {
                    Fail(pinProblem);
                    return;
                }
            }

            dialog.Close();
        });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { Ghost("Cancel", () => dialog.Close()), save } };
        if (account is not null && account.Id != accounts.Active?.Id)
        {
            buttons.Children.Insert(0, Ghost("Delete", async () =>
            {
                if (await shell.ConfirmAsync("Delete profile", $"Delete {account.Name} and their settings?", "Delete"))
                {
                    await accounts.DeleteAsync(account.Id, CancellationToken.None);
                    dialog.Close();
                }
            }));
        }

        dialog.Content = new StackPanel
        {
            Margin = new Thickness(22),
            Spacing = 12,
            Children =
            {
                T(account is null ? "Add profile" : "Edit profile", 17, FontWeight.SemiBold),
                name,
                Caption("Colour"),
                swatches,
                pictureButton,
                copy,
                Caption("A PIN encrypts this profile's settings with a key only the PIN opens; it is asked when switching to it."),
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { oldPin, pin } },
                removePin,
                error,
                buttons,
            },
        };
        await dialog.ShowDialog(shell);
    }

    private static async Task<string?> PromptPinAsync(Shell shell, string title)
    {
        var box = new TextBox { PasswordChar = '•', Width = 220 };
        var dialog = new Window { Title = title, SizeToContent = SizeToContent.WidthAndHeight, CanResize = false, Background = B(Look.P.Window), WindowStartupLocation = WindowStartupLocation.CenterOwner };
        string? result = null;
        void Ok()
        {
            result = box.Text;
            dialog.Close();
        }

        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                Ok();
            }
        };
        dialog.Content = new StackPanel
        {
            Margin = new Thickness(22),
            Spacing = 12,
            Children = { T(title, 15, FontWeight.SemiBold), box, new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { Ghost("Cancel", () => dialog.Close()), Primary("Open", Ok) } } },
        };
        dialog.Opened += (_, _) => box.Focus();
        await dialog.ShowDialog(shell);
        return string.IsNullOrEmpty(result) ? null : result;
    }
}
