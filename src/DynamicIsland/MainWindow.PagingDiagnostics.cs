using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media;
using DynamicIsland.Core.Services;

namespace DynamicIsland;

public partial class MainWindow
{
    // Opt-in diagnostics: collect values in memory; format/write only outside measurement windows.
    private List<PagingSample>? _pagingSamples;
    private Stopwatch? _pagingClock;
    private string? _pagingPath;

    private readonly record struct PagingSample(
        double TimeMs, string Event, int Delta, int State, int ActiveTab,
        double Position, double Target, bool Animating, int Remainder, int Pending, int Queued,
        double Width, double Height, bool SizeSettled, double ContentOpacity,
        int FromPage, double FromY, double FromOpacity, double TargetY, double TargetOpacity,
        bool WheelGliding, double Velocity);

    private void StartPagingProbeIfRequested()
    {
        string? path = Environment.GetEnvironmentVariable("DI_PAGING_LOG");
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            _pagingPath = Path.GetFullPath(path);
            Directory.CreateDirectory(Path.GetDirectoryName(_pagingPath)!);
            File.WriteAllText(_pagingPath,
                "time_ms,event,delta,state,active_tab,position,target,animating,remainder,pending,queued," +
                "width,height,size_settled,content_opacity,from_page,from_y,from_opacity,target_y,target_opacity,wheel_gliding,velocity\n",
                Encoding.UTF8);
            _pagingClock = Stopwatch.StartNew();
            _pagingSamples = new List<PagingSample>(8192);
            CompositionTarget.Rendering += OnPagingProbeRendering;
            RecordPagingSample("probe_start");
        }
        catch (Exception ex)
        {
            LogService.Warn("切页探针启动失败：" + ex.Message);
            _pagingSamples = null;
        }
    }

    private void OnPagingProbeRendering(object? sender, EventArgs e) => RecordPagingSample("frame");

    private void RecordPagingSample(string kind, int delta = 0)
    {
        if (_pagingSamples is null || _pagingClock is null) return;
        // Bound memory for forgotten diagnostic sessions; never write inside a measured frame.
        if (_pagingSamples.Count >= 100_000)
        {
            CompositionTarget.Rendering -= OnPagingProbeRendering;
            return;
        }
        int from = Math.Clamp((int)Math.Round(_tabAnimFrom), 0, _tabPages.Length - 1);
        int target = Math.Clamp((int)Math.Round(_tabAnimTo), 0, _tabPages.Length - 1);
        FrameworkElement oldPage = _tabPages[from], newPage = _tabPages[target];
        _pagingSamples.Add(new PagingSample(
            _pagingClock.Elapsed.TotalMilliseconds, kind, delta, (int)_state, _viewModel.ActiveTab,
            _tabPosition, _tabAnimTo, _tabAnimating, _wheelAccum, 0, _tabQueue.Count,
            _spring.Width, _spring.Height, _spring.IsSettled, ExpandedContent.Opacity,
            from, PageY(oldPage), VisibleOpacity(oldPage), PageY(newPage), VisibleOpacity(newPage),
            _wheelGliding, _wheelVelocity));
    }

    // Layout/render callback samples, not timestamps of pixels presented by the compositor.
    private double PageY(FrameworkElement page) => page.Visibility == Visibility.Visible
        ? page.TranslatePoint(new Point(0, 0), Island).Y : double.NaN;

    private static double VisibleOpacity(FrameworkElement page) =>
        page.Visibility == Visibility.Visible ? page.Opacity : 0;

    private bool HandlePagingProbeCommand(string command)
    {
        if (_pagingSamples is null) return false;
        if (command.StartsWith("probe:mark:", StringComparison.Ordinal))
        {
            string marker = command[11..];
            if (marker.Length is > 0 and <= 80 && marker.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-'))
                RecordPagingSample("mark_" + marker);
            return true;
        }
        if (command.StartsWith("probe:wheel:", StringComparison.Ordinal) &&
            int.TryParse(command[12..], out int delta) && delta is >= -12000 and <= 12000)
        {
            HandleWheel(delta);
            return true;
        }
        if (command == "probe:flush")
        {
            FlushPagingProbe();
            return true;
        }
        return false;
    }

    private void FlushPagingProbe()
    {
        if (_pagingSamples is null || _pagingPath is null) return;
        try
        {
            var csv = new StringBuilder();
            foreach (PagingSample s in _pagingSamples)
                csv.Append(CultureInfo.InvariantCulture,
                    $"{s.TimeMs:F4},{s.Event},{s.Delta},{s.State},{s.ActiveTab},{s.Position:F6},{s.Target:F6},{s.Animating},{s.Remainder},{s.Pending},{s.Queued},{s.Width:F4},{s.Height:F4},{s.SizeSettled},{s.ContentOpacity:F6},{s.FromPage},{s.FromY:F4},{s.FromOpacity:F6},{s.TargetY:F4},{s.TargetOpacity:F6},{s.WheelGliding},{s.Velocity:F6}\n");
            File.AppendAllText(_pagingPath, csv.ToString(), Encoding.UTF8);
            _pagingSamples.Clear();
        }
        catch (Exception ex)
        {
            LogService.Warn("切页探针写盘失败：" + ex.Message);
            CompositionTarget.Rendering -= OnPagingProbeRendering;
            _pagingSamples = null;
        }
    }

    private void StopPagingProbe()
    {
        CompositionTarget.Rendering -= OnPagingProbeRendering;
        RecordPagingSample("probe_stop");
        FlushPagingProbe();
        _pagingSamples = null;
    }
}
