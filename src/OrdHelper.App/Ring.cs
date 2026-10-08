using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Path = System.Windows.Shapes.Path;
using Ellipse = System.Windows.Shapes.Ellipse;

namespace OrdHelper.App;

/// <summary>완료 비율 링 + 가운데 "남은 %". 값이 바뀔 때만 짧게 움직인다.</summary>
public sealed class Ring : Grid
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(Ring), new PropertyMetadata(0.0, (d, _) => ((Ring)d).Draw()));

    private readonly Path _arc = new() { StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
    private readonly TextBlock _number = new() { HorizontalAlignment = HorizontalAlignment.Center, FontFamily = new FontFamily("Bahnschrift, Segoe UI") };
    private readonly TextBlock _caption = new() { HorizontalAlignment = HorizontalAlignment.Center, Text = "남음" };
    private readonly double _size, _thickness;

    /// <summary>완료 비율 0~1 (애니메이션 대상).</summary>
    public double Value
    {
        get => (double)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public Ring(double size, double thickness, bool caption = true)
    {
        _size = size;
        _thickness = thickness;
        Width = Height = size;
        var track = new Ellipse { Stroke = Theme.Get("Deck"), StrokeThickness = thickness };
        _arc.Stroke = Theme.Get("Accent");
        _arc.StrokeThickness = thickness;
        _number.Foreground = Theme.Get("Sail");
        _number.FontSize = size * 0.27;
        _number.FontWeight = FontWeights.SemiBold;
        _caption.Foreground = Theme.Get("Fog");
        _caption.FontSize = Math.Max(10, size * 0.1);
        var center = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        center.Children.Add(_number);
        if (caption) center.Children.Add(_caption);
        Children.Add(track);
        Children.Add(_arc);
        Children.Add(center);
        Draw();
    }

    /// <summary>완료 비율을 바꾼다. 줄면 바로, 늘면 0.25초 이징.</summary>
    public void Set(double done)
    {
        done = Math.Clamp(done, 0, 1);
        _number.Text = $"{Math.Round((1 - done) * 100)}%";
        if (done <= Value || SystemParameters.ClientAreaAnimation == false)
        {
            BeginAnimation(ValueProperty, null);
            Value = done;
            return;
        }
        BeginAnimation(ValueProperty, new DoubleAnimation(done, TimeSpan.FromMilliseconds(250))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        });
    }

    private void Draw()
    {
        var value = Math.Clamp(Value, 0, 0.9999);
        var r = (_size - _thickness) / 2;
        var c = _size / 2;
        var angle = value * 2 * Math.PI;
        var end = new Point(c + r * Math.Sin(angle), c - r * Math.Cos(angle));
        var figure = new PathFigure { StartPoint = new Point(c, c - r), IsClosed = false };
        figure.Segments.Add(new ArcSegment(end, new Size(r, r), 0, value > 0.5, SweepDirection.Clockwise, true));
        _arc.Data = value <= 0 ? null : new PathGeometry([figure]);
    }
}
