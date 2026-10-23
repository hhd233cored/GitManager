using System.Windows;
using System.Windows.Media;

namespace GitManager.App.Controls;

public sealed class FileStatusIcon : FrameworkElement
{
    public static readonly DependencyProperty StatusProperty = DependencyProperty.Register(nameof(Status), typeof(char), typeof(FileStatusIcon),
        new FrameworkPropertyMetadata('M', FrameworkPropertyMetadataOptions.AffectsRender));
    public char Status { get => (char)GetValue(StatusProperty); set => SetValue(StatusProperty, value); }
    private static readonly Brush Added = CreateBrush("#1A7F37");
    private static readonly Brush Modified = CreateBrush("#9A7500");
    private static readonly Brush Removed = CreateBrush("#CF222E");
    private static Brush CreateBrush(string value)
    {
        var brush = (SolidColorBrush)new BrushConverter().ConvertFromString(value)!;
        brush.Freeze();
        return brush;
    }
    protected override void OnRender(DrawingContext dc)
    {
        var color = Status is 'A' or 'C' ? Added : Status is 'D' or 'U' ? Removed : Modified;
        var x = (ActualWidth - 12) / 2;
        var y = (ActualHeight - 12) / 2;
        dc.DrawRoundedRectangle(null, new Pen(color, 1), new Rect(x + .5, y + .5, 11, 11), .7, .7);
        var center = new Point(x + 6, y + 6);
        var pen = new Pen(color, 1.3);
        if (Status is 'A' or 'C')
        {
            dc.DrawLine(pen, new Point(center.X - 3, center.Y), new Point(center.X + 3, center.Y));
            dc.DrawLine(pen, new Point(center.X, center.Y - 3), new Point(center.X, center.Y + 3));
        }
        else if (Status == 'D') dc.DrawLine(pen, new Point(center.X - 3, center.Y), new Point(center.X + 3, center.Y));
        else if (Status == 'U')
        {
            dc.DrawLine(pen, new Point(center.X, center.Y - 3), center);
            dc.DrawEllipse(color, null, new Point(center.X, center.Y + 2.5), .9, .9);
        }
        else dc.DrawEllipse(color, null, center, 1.45, 1.45);
    }
}
