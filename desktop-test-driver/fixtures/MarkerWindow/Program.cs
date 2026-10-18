using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using DesktopTestDriver;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args is not [var run, var root, var duration] || !Wire.Id(run) || !int.TryParse(duration, out int seconds) || seconds is < 1 or > 300) return 2;
        try
        {
            RunStore.CheckPrivate(Path.GetFullPath(root)); WindowsDesktop.EnableDpi();
            var colors = VisualMarker.Colors(run);
            var band = new System.Windows.Controls.Primitives.UniformGrid { Columns = 16, Rows = 1, Height = 64 };
            foreach (var rgb in colors) band.Children.Add(new Border { Background = new SolidColorBrush(Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb)) });
            var stack = new StackPanel(); stack.Children.Add(band);
            stack.Children.Add(new TextBlock { Text = "DesktopTestDriver 전용 화면 표식 · " + run, Margin = new Thickness(12) });
            var window = new Window { Title = "DesktopTestDriver marker " + run, Width = 560, Height = 180, Content = stack };
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) }; var clock = Stopwatch.StartNew();
            var receipt = Path.Combine(root, "marker.json");
            timer.Tick += (_, _) =>
            {
                var origin = band.PointToScreen(new(0, 0)); var end = band.PointToScreen(new(band.ActualWidth, band.ActualHeight));
                var json = Wire.Encode(new { runId = run, utc = DateTimeOffset.UtcNow, tick = Stopwatch.GetTimestamp(), identity = Processes.Identify(Process.GetCurrentProcess()), colors,
                    screenRegion = new DesktopTestDriver.Rect((int)origin.X, (int)origin.Y, (int)(end.X - origin.X), (int)(end.Y - origin.Y)) });
                File.WriteAllText(receipt + ".tmp", json); File.Move(receipt + ".tmp", receipt, true);
                if (clock.Elapsed.TotalSeconds >= seconds) window.Close();
            };
            window.Loaded += (_, _) => timer.Start(); window.Closed += (_, _) => timer.Stop();
            new Application().Run(window); return 0;
        }
        catch { return 1; }
    }
}
