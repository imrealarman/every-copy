using EveryCopy.Models;
using EveryCopy.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace EveryCopy;

/// <summary>The colour of a destination drive as WinUI brushes (used from XAML bindings), and the small "rename / recolour" pop-up.</summary>
public static class Tints
{
    private static readonly SolidColorBrush[] Solid = Destinations.Palette.Select(s => new SolidColorBrush(Color.FromArgb(255, s.R, s.G, s.B))).ToArray();
    private static readonly SolidColorBrush[] Soft = Destinations.Palette.Select(s => new SolidColorBrush(Color.FromArgb(0x30, s.R, s.G, s.B))).ToArray();

    private static int Clamp(int index) => Math.Clamp(index, 0, Solid.Length - 1);

    public static Brush BrushOf(int index) => Solid[Clamp(index)];
    public static Brush SoftBrushOf(int index) => Soft[Clamp(index)];
    public static Color ColorOf(int index) { var s = Destinations.Palette[Clamp(index)]; return Color.FromArgb(255, s.R, s.G, s.B); }

    /// <summary>
    /// Opens a small pop-up under <paramref name="anchor"/> where the user names the drive ("Sara's USB") and, if they like,
    /// picks another colour. Every transfer to that drive follows the change straight away.
    /// </summary>
    public static void ShowEditor(FrameworkElement anchor, CopyJob job, Action<string> changed)
    {
        string key = job.DestKey;
        var name = new TextBox
        {
            Text = Destinations.NameOf(key),
            PlaceholderText = "A name for this drive, e.g. Sara's USB",
            MaxLength = 40,
            MinWidth = 260,
        };

        var swatches = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        var buttons = new List<Button>();
        void Mark()
        {
            int current = Destinations.ColorOf(key);
            for (int i = 0; i < buttons.Count; i++)
            {
                buttons[i].BorderThickness = new Thickness(i == current ? 3 : 1);
                buttons[i].BorderBrush = (Brush)Application.Current.Resources[i == current ? "TextBrush" : "BorderBrush1"];
            }
        }
        for (int i = 0; i < Destinations.Palette.Length; i++)
        {
            int index = i;
            var button = new Button
            {
                Width = 28, Height = 28, MinWidth = 28, MinHeight = 28, Padding = new Thickness(0),
                CornerRadius = new CornerRadius(14),
                Background = BrushOf(i),
            };
            ToolTipService.SetToolTip(button, Destinations.Palette[i].Name);
            button.Click += (_, _) => { Destinations.SetColor(key, index); Mark(); changed(key); };
            buttons.Add(button);
            swatches.Children.Add(button);
        }
        Mark();

        var flyout = new Flyout();
        var save = new Button { Content = "Save", Style = (Style)Application.Current.Resources["PrimaryButton"] };
        var clear = new Button { Content = "Remove name", Style = (Style)Application.Current.Resources["GhostButton"] };
        void Apply() { Destinations.SetName(key, name.Text); changed(key); flyout.Hide(); }
        save.Click += (_, _) => Apply();
        clear.Click += (_, _) => { name.Text = ""; Apply(); };
        name.KeyDown += (_, e) => { if (e.Key == Windows.System.VirtualKey.Enter) Apply(); };

        var body = new StackPanel { Spacing = 10 };
        body.Children.Add(new TextBlock { Text = $"Drive {job.DestRoot}", Style = (Style)Application.Current.Resources["PanelHeader"] });
        body.Children.Add(name);
        body.Children.Add(new TextBlock { Text = "Colour", Style = (Style)Application.Current.Resources["MutedText"] });
        body.Children.Add(swatches);
        body.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { save, clear } });
        flyout.Content = body;
        flyout.ShowAt(anchor);
        name.Focus(FocusState.Programmatic);
    }
}
