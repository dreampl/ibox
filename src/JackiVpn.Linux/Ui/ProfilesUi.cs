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

        if (Domain.Visuals.Avatars.Find(account.Avatar) is { } art)
        {
            return new AvatarArtView { Art = art, Width = size, Height = size };
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

    /// <summary>A cartoon avatar (<see cref="Domain.Visuals.Avatars"/>) drawn from its vectors, clipped to a circle.</summary>
    public sealed class AvatarArtView : Control
    {
        private static readonly Dictionary<string, (Geometry Shape, IBrush? Fill, IPen? Pen)[]> Shapes = new(StringComparer.Ordinal);

        public Domain.Visuals.AvatarArt? Art { get; set; }

        public override void Render(DrawingContext context)
        {
            if (Art is not { } art || Bounds.Width <= 0)
            {
                return;
            }

            if (!Shapes.TryGetValue(art.Id, out var shapes))
            {
                shapes = art.Layers.Select(l => (
                    Geometry.Parse(l.Data),
                    l.Fill == 0 ? null : (IBrush?)new SolidColorBrush(Color.FromUInt32(l.Fill)),
                    l.Stroke == 0 ? null : (IPen?)new Pen(new SolidColorBrush(Color.FromUInt32(l.Stroke)), l.StrokeWidth, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round)))
                    .ToArray();
                Shapes[art.Id] = shapes;
            }

            var s = Math.Min(Bounds.Width, Bounds.Height) / 100;
            using (context.PushTransform(Matrix.CreateScale(s, s)))
            using (context.PushGeometryClip(new EllipseGeometry(new Rect(0, 0, 100, 100))))
            {
                context.DrawEllipse(new SolidColorBrush(Color.FromUInt32(art.Background)), null, new Point(50, 50), 50, 50);
                foreach (var (shape, fill, pen) in shapes)
                {
                    context.DrawGeometry(fill, pen, shape);
                }
            }
        }
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

        // Moving to another device (Windows, Linux or Android): one encrypted .ibox file.
        var export = Ghost("Export…", () =>
        {
            flyout?.Hide();
            _ = ExportAsync(shell);
        });
        var import = Ghost("Import…", () =>
        {
            flyout?.Hide();
            _ = ImportAsync(shell);
        });
        ToolTip.SetTip(export, "Save this profile, its settings, subscriptions and servers to a password-protected file for another device");
        ToolTip.SetTip(import, "Open an .ibox file from Windows, Linux or Android");
        var transfer = new Grid { ColumnDefinitions = new ColumnDefinitions("*,8,*"), Margin = new Thickness(0, 4, 0, 0) };
        export.HorizontalAlignment = import.HorizontalAlignment = HorizontalAlignment.Stretch;
        export.HorizontalContentAlignment = import.HorizontalContentAlignment = HorizontalAlignment.Center;
        Grid.SetColumn(import, 2);
        transfer.Children.Add(export);
        transfer.Children.Add(import);
        list.Children.Add(transfer);
        var hint = Hint("Each profile has its own settings (routing, DNS, modes, theme…), stored encrypted. Servers are shared. An exported account opens on Windows, Linux and Android.");
        hint.TextWrapping = TextWrapping.Wrap;
        list.Children.Add(hint);
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
        // The face of the profile: ten cartoon avatars to pick from (or a picture of your own below).
        var avatar = account?.Avatar ?? Domain.Visuals.Avatars.NextFree(accounts.Accounts.Select(a => a.Avatar));
        var swatches = new WrapPanel { MaxWidth = 340 };
        void ShowSwatches()
        {
            swatches.Children.Clear();
            foreach (var art in Domain.Visuals.Avatars.All)
            {
                var id = art.Id;
                var chosen = id == avatar;
                var face = new Border
                {
                    Width = 58,
                    Height = 58,
                    Margin = new Thickness(0, 0, 8, 8),
                    CornerRadius = new CornerRadius(29),
                    Padding = new Thickness(3),
                    BorderBrush = B(chosen ? Accent : Ui.Border),
                    BorderThickness = new Thickness(chosen ? 2.5 : 1),
                    Cursor = new Cursor(StandardCursorType.Hand),
                    Child = new AvatarArtView { Art = art, Width = 48, Height = 48 },
                };
                ToolTip.SetTip(face, art.Name);
                Avalonia.Automation.AutomationProperties.SetName(face, "Avatar " + art.Name);
                face.PointerPressed += (_, _) =>
                {
                    avatar = id;
                    ShowSwatches();
                };
                swatches.Children.Add(face);
            }
        }

        ShowSwatches();
        var copy = new CheckBox { Content = "Start from my current settings", IsChecked = true, IsVisible = account is null };
        var pin = new TextBox { PasswordChar = '•', Width = 160, PlaceholderText = account?.IsLocked == true ? "New PIN (empty = keep)" : "PIN (optional)" };
        var oldPin = new TextBox { PasswordChar = '•', Width = 160, PlaceholderText = "Current PIN", IsVisible = account?.IsLocked == true };
        var removePin = new CheckBox { Content = "Remove the PIN", IsVisible = account?.IsLocked == true };
        var error = new TextBlock { Foreground = B(Danger), FontSize = 12, TextWrapping = TextWrapping.Wrap, MaxWidth = 360, IsVisible = false };
        string? picture = null;
        var pictureButton = Ghost("Or use a picture of your own…", () => { });
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
            pictureButton.Content = picture is null ? "Or use a picture of your own…" : Path.GetFileName(picture);
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
                var (_, problem) = await accounts.CreateAsync(name.Text ?? string.Empty, color, copy.IsChecked == true, newPin, CancellationToken.None, avatar);
                if (problem is not null)
                {
                    Fail(problem);
                    return;
                }
            }
            else
            {
                if (await accounts.RenameAsync(account.Id, name.Text ?? string.Empty, color, CancellationToken.None, picture is null && avatar != account.Avatar ? avatar : null) is { } problem)
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
                Caption("Avatar"),
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

    private static async Task ExportAsync(Shell shell)
    {
        var transfer = shell.Services.AccountTransfer;
        if (await PromptPasswordAsync(shell, "Export account", "Choose a password for the file. You need it to open the file on the other device.", confirm: true) is not { } password)
        {
            return;
        }

        var file = await shell.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export account",
            SuggestedFileName = transfer.SuggestedFileName(),
            DefaultExtension = "ibox",
            FileTypeChoices = [new FilePickerFileType("IBOX account") { Patterns = ["*.ibox"] }],
        });
        if (file is null)
        {
            return;
        }

        try
        {
            var bytes = await transfer.ExportAsync(password, shell.Lifetime.Token);
            await using var stream = await file.OpenWriteAsync();
            await stream.WriteAsync(bytes);
            shell.Toast("Account exported", "Copy the file to the other device and choose Import there.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            shell.Toast("Export failed", ex.Message, warning: true);
        }
    }

    private static async Task ImportAsync(Shell shell)
    {
        var files = await shell.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Import account",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("IBOX account") { Patterns = ["*.ibox"] }, FilePickerFileTypes.All],
        });
        if (files.Count == 0)
        {
            return;
        }

        byte[] bytes;
        await using (var stream = await files[0].OpenReadAsync())
        {
            if (stream.Length > Application.Accounts.AccountTransfer.MaxFileBytes)
            {
                shell.Toast("Import failed", "This is not an IBOX account file.", warning: true);
                return;
            }

            using var ms = new MemoryStream();
            await stream.CopyToAsync(ms);
            bytes = ms.ToArray();
        }

        if (await PromptPasswordAsync(shell, "Import account", "The password the file was exported with.", confirm: false) is not { } password)
        {
            return;
        }

        // A new profile becomes active: like switching, disconnect first.
        if (shell.Services.Orchestrator.State != Domain.Connection.ConnectionState.Disconnected)
        {
            await shell.Services.Orchestrator.DisconnectAsync();
        }

        var (result, problem) = await shell.Services.AccountTransfer.ImportAsync(bytes, password, shell.Lifetime.Token);
        if (result is null)
        {
            shell.Toast("Import failed", problem ?? "Unknown error.", warning: true);
            return;
        }

        shell.Toast("Account imported", $"\"{result.Name}\" from {result.From}: {result.Subscriptions} subscriptions (updating now), {result.Servers} servers." + (result.Notes.Count > 0 ? " " + string.Join(" ", result.Notes) : string.Empty));
    }

    /// <summary>A password for an account file (typed twice when choosing one); null when cancelled.</summary>
    private static async Task<string?> PromptPasswordAsync(Shell shell, string title, string message, bool confirm)
    {
        var first = new TextBox { PasswordChar = '•', Width = 300 };
        var second = new TextBox { PasswordChar = '•', Width = 300, IsVisible = confirm, PlaceholderText = "Again" };
        var error = T(string.Empty, 12, color: Danger);
        error.TextWrapping = TextWrapping.Wrap;
        error.MaxWidth = 300;
        var hint = Hint(message);
        hint.TextWrapping = TextWrapping.Wrap;
        hint.MaxWidth = 300;
        var dialog = new Window { Title = title, SizeToContent = SizeToContent.WidthAndHeight, CanResize = false, Background = B(Look.P.Window), WindowStartupLocation = WindowStartupLocation.CenterOwner };
        string? result = null;
        void Ok()
        {
            var problem = confirm
                ? Application.Accounts.AccountTransfer.PasswordProblem(first.Text) ?? (first.Text != second.Text ? "The two passwords differ." : null)
                : string.IsNullOrEmpty(first.Text) ? "Enter the password." : null;
            if (problem is not null)
            {
                error.Text = problem;
                return;
            }

            result = first.Text;
            dialog.Close();
        }

        dialog.Content = new StackPanel
        {
            Margin = new Thickness(22),
            Spacing = 10,
            Children = { T(title, 15, FontWeight.SemiBold), hint, first, second, error, new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { Ghost("Cancel", () => dialog.Close()), Primary("OK", Ok) } } },
        };
        dialog.Opened += (_, _) => first.Focus();
        await dialog.ShowDialog(shell);
        return result;
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
