using EveryCopy.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;

namespace EveryCopy;

/// <summary>A small live area chart of a transfer's speed over the last minute, drawn in the accent colour.</summary>
public sealed class SpeedGraph : UserControl
{
    private const int Slots = 120; // matches CopyJob's history length, so the line fills the width after a minute

    public static readonly DependencyProperty SamplesProperty = DependencyProperty.Register(
        nameof(Samples), typeof(double[]), typeof(SpeedGraph), new PropertyMetadata(null, (d, _) => ((SpeedGraph)d).Redraw()));

    private readonly Canvas canvas = new();
    private readonly Polygon area = new();
    private readonly Polyline line = new() { StrokeThickness = 2, StrokeLineJoin = PenLineJoin.Round };
    private readonly TextBlock peak = new() { FontSize = 10, Margin = new Thickness(8, 4, 0, 0), VerticalAlignment = VerticalAlignment.Top, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly TextBlock now = new() { FontSize = 10, Margin = new Thickness(0, 4, 8, 0), VerticalAlignment = VerticalAlignment.Top, HorizontalAlignment = HorizontalAlignment.Right };

    public SpeedGraph()
    {
        Height = 70;
        HorizontalContentAlignment = HorizontalAlignment.Stretch;

        var accent = (SolidColorBrush)Application.Current.Resources["AccentBrush"];
        line.Stroke = accent;
        area.Fill = new SolidColorBrush(Windows.UI.Color.FromArgb(0x40, accent.Color.R, accent.Color.G, accent.Color.B));
        peak.Foreground = (Brush)Application.Current.Resources["TextMutedBrush"];
        now.Foreground = (Brush)Application.Current.Resources["TextBrush"];

        canvas.Children.Add(area);
        canvas.Children.Add(line);
        var frame = new Grid
        {
            Background = (Brush)Application.Current.Resources["CanvasBrush"],
            BorderBrush = (Brush)Application.Current.Resources["BorderBrush1"],
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
        };
        frame.Children.Add(canvas);
        frame.Children.Add(peak);
        frame.Children.Add(now);
        Content = frame;
        SizeChanged += (_, _) => Redraw();
    }

    /// <summary>The drive's colour: the line and the area under it are drawn in it. The accent blue until one is given.</summary>
    public static readonly DependencyProperty TintProperty = DependencyProperty.Register(
        nameof(Tint), typeof(Windows.UI.Color), typeof(SpeedGraph), new PropertyMetadata(default(Windows.UI.Color), (d, _) => ((SpeedGraph)d).Recolour()));

    public Windows.UI.Color Tint
    {
        get => (Windows.UI.Color)GetValue(TintProperty);
        set => SetValue(TintProperty, value);
    }

    private void Recolour()
    {
        var c = Tint;
        if (c.A == 0) return;
        line.Stroke = new SolidColorBrush(c);
        area.Fill = new SolidColorBrush(Windows.UI.Color.FromArgb(0x40, c.R, c.G, c.B));
    }

    public double[]? Samples
    {
        get => (double[]?)GetValue(SamplesProperty);
        set => SetValue(SamplesProperty, value);
    }

    private void Redraw()
    {
        double w = ActualWidth - 2, h = ActualHeight - 2;
        var samples = Samples;
        if (w < 4 || h < 4 || samples == null || samples.Length == 0)
        {
            line.Points = new PointCollection();
            area.Points = new PointCollection();
            peak.Text = now.Text = "";
            return;
        }

        double max = Math.Max(samples.Max(), 1);
        int slots = Math.Max(2, Math.Min(samples.Length, Slots)); // fills the width; slides once a full minute is kept
        var points = new PointCollection();
        for (int i = 0; i < samples.Length; i++)
            points.Add(new Point(i * w / (slots - 1), h - 2 - samples[i] / max * (h - 18)));

        var fill = new PointCollection { new Point(0, h) };
        foreach (var p in points) fill.Add(p);
        fill.Add(new Point((samples.Length - 1) * w / (slots - 1), h));

        line.Points = points;
        area.Points = fill;
        peak.Text = "Peak " + Format.Bytes((long)max) + "/s";
        now.Text = Format.Bytes((long)samples[^1]) + "/s";
    }
}
