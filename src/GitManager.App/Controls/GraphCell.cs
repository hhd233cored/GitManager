using System.Windows;
using System.Windows.Media;
using GitManager.Core;

namespace GitManager.App.Controls;

public sealed class GraphCell : FrameworkElement
{
    public const double LaneSpacing = 14;
    public const double NodeInset = 12;
    public static double WidthFor(GraphRow row) => 24 + Math.Max(0, row.LaneCount - 1) * LaneSpacing;
    public static readonly DependencyProperty RowProperty = DependencyProperty.Register(nameof(Row), typeof(GraphRow), typeof(GraphCell),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public GraphRow? Row { get => (GraphRow?)GetValue(RowProperty); set => SetValue(RowProperty, value); }
    public static readonly DependencyProperty IsNodeVisibleProperty = DependencyProperty.Register(nameof(IsNodeVisible), typeof(bool), typeof(GraphCell),
        new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender));
    public bool IsNodeVisible { get => (bool)GetValue(IsNodeVisibleProperty); set => SetValue(IsNodeVisibleProperty, value); }
    public static readonly DependencyProperty IsContinuationProperty = DependencyProperty.Register(nameof(IsContinuation), typeof(bool), typeof(GraphCell),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));
    public bool IsContinuation { get => (bool)GetValue(IsContinuationProperty); set => SetValue(IsContinuationProperty, value); }
    private static readonly Brush[] Palette = [ColorBrush("#3D8BEB"), ColorBrush("#D99C32"), ColorBrush("#C65BA5"),
        ColorBrush("#30A46C"), ColorBrush("#8E6BCD"), ColorBrush("#22A6B3"), ColorBrush("#E07A53")];
    private static Brush ColorBrush(string color)
    {
        var brush = (SolidColorBrush)new BrushConverter().ConvertFromString(color)!;
        brush.Freeze();
        return brush;
    }
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        if (Row is null) return;
        double X(int lane) => NodeInset + lane * LaneSpacing;
        if (IsContinuation)
        {
            foreach (var edge in Row.Edges.Where(edge => edge.ToY == 1).DistinctBy(edge => (edge.ToLane, edge.Color)))
                dc.DrawLine(new Pen(Palette[edge.Color % Palette.Length], 2), new Point(X(edge.ToLane), 0), new Point(X(edge.ToLane), ActualHeight));
            return;
        }
        foreach (var edge in Row.Edges)
        {
            var geometry = new StreamGeometry();
            using (var context = geometry.Open())
            {
                var from = new Point(X(edge.FromLane), edge.FromY * ActualHeight);
                var to = new Point(X(edge.ToLane), edge.ToY * ActualHeight);
                context.BeginFigure(from, false, false);
                if (edge.FromLane == edge.ToLane) context.LineTo(to, true, false);
                else
                {
                    var middle = (from.Y + to.Y) / 2;
                    context.BezierTo(new Point(from.X, middle), new Point(to.X, middle), to, true, false);
                }
            }
            geometry.Freeze();
            dc.DrawGeometry(null, new Pen(Palette[edge.Color % Palette.Length], 2), geometry);
        }
        if (!IsNodeVisible) return;
        var center = new Point(X(Row.NodeLane), ActualHeight / 2);
        var color = Palette[Row.Color % Palette.Length];
        if (Row.IsHead) dc.DrawEllipse(Brushes.White, new Pen(color, 1.6), center, 6.1, 6.1);
        dc.DrawEllipse(color, null, center, 3.8, 3.8);
    }
}
