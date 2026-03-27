using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using LibVLCSharp.Shared;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Xabe.FFmpeg;

namespace SevenCuts
{
    public enum EditTool { Selection, Razor, Ripple }

    public partial class MainWindow : Window
    {
        // ─── State ───────────────────────────────────────────────────────────
        private HashSet<string> _exportedFiles = new();
        private string? _rootFolderPath = null;
        private string? _currentFilePath = null;
        private string? _customOutputPath = null;

        private LibVLC? _libVLC;
        private MediaPlayer? _mediaPlayer;

        private float _playbackRate = 1.0f;
        private long _inPoint = -1;
        private long _outPoint = -1;
        private long _stopwatchOffsetMs = 0;
        private Stopwatch _playbackStopwatch = new();

        // Segments & tools
        private List<Segment> _segments = new();
        private Segment? _selectedSegment = null;
        private List<long> _markers = new();
        private EditTool _currentTool = EditTool.Selection;

        // Edge drag state
        private Segment? _dragSegment = null;
        private bool _draggingStart = false;

        private DispatcherTimer _uiTimer;

        // ─── Constructor ─────────────────────────────────────────────────────
        public MainWindow()
        {
            InitializeComponent();

            Core.Initialize();
            _libVLC = new LibVLC();
            _mediaPlayer = new MediaPlayer(_libVLC);

            BtnOpenFolder.Click += OnOpenFolderClick;
            BtnPlayPause.Click += OnPlayPauseClick;
            BtnSkipBack.Click += OnSkipBackClick;
            BtnSkipFwd.Click += OnSkipFwdClick;
            BtnSetIn.Click += OnSetInClick;
            BtnSetOut.Click += OnSetOutClick;
            BtnClearPoints.Click += OnClearPointsClick;
            BtnOutputFolder.Click += OnOutputFolderClick;
            BtnExport.Click += OnExportClick;
            BtnToolSelect.Click += (s, e) => SetTool(EditTool.Selection);
            BtnToolRazor.Click += (s, e) => SetTool(EditTool.Razor);
            BtnToolRipple.Click += (s, e) => SetTool(EditTool.Ripple);
            BtnAddMarker.Click += OnAddMarkerClick;
            FolderTree.SelectionChanged += OnTreeSelectionChanged;
            ScrubberCanvas.PointerPressed += OnCanvasPointerPressed;
            ScrubberCanvas.PointerMoved += OnCanvasPointerMoved;
            ScrubberCanvas.PointerReleased += OnCanvasPointerReleased;

            this.Opened += (s, e) =>
            {
                if (VideoView.VideoHandle != IntPtr.Zero)
                    _mediaPlayer!.Hwnd = VideoView.VideoHandle;
            };
            this.KeyDown += OnKeyDown;

            _uiTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(16)
            };
            _uiTimer.Tick += OnUiTimerTick;
            _uiTimer.Start();
        }

        // ─── Tool Switching ───────────────────────────────────────────────────
        private void SetTool(EditTool tool)
        {
            _currentTool = tool;
            var orange = new SolidColorBrush(Color.Parse("#e8a020"));
            var grey = new SolidColorBrush(Color.Parse("#333333"));

            BtnToolSelect.Background = tool == EditTool.Selection ? orange : grey;
            BtnToolRazor.Background = tool == EditTool.Razor ? orange : grey;
            BtnToolRipple.Background = tool == EditTool.Ripple ? orange : grey;

            BtnToolSelect.Foreground = new SolidColorBrush(Color.Parse(tool == EditTool.Selection ? "White" : "#aaaaaa"));
            BtnToolRazor.Foreground = new SolidColorBrush(Color.Parse(tool == EditTool.Razor ? "White" : "#aaaaaa"));
            BtnToolRipple.Foreground = new SolidColorBrush(Color.Parse(tool == EditTool.Ripple ? "White" : "#aaaaaa"));

            ToolHintText.Text = tool switch
            {
                EditTool.Selection => "Selection: click segment to select  |  drag edge to trim  |  Delete removes selected",
                EditTool.Razor => "Razor: click on timeline to cut at that point",
                EditTool.Ripple => "Ripple: drag edge — subsequent segments shift automatically",
                _ => ""
            };
        }

        // ─── Timer ───────────────────────────────────────────────────────────
        private void OnUiTimerTick(object? sender, EventArgs e)
        {
            if (_mediaPlayer == null) return;
            long duration = _mediaPlayer.Length;
            if (duration <= 0) return;

            long current;
            if (_mediaPlayer.IsPlaying)
            {
                current = Math.Min(_stopwatchOffsetMs + _playbackStopwatch.ElapsedMilliseconds, duration);
                if (_playbackStopwatch.ElapsedMilliseconds % 2000 < 20)
                {
                    _stopwatchOffsetMs = _mediaPlayer.Time;
                    _playbackStopwatch.Restart();
                    current = _stopwatchOffsetMs;
                }
            }
            else
            {
                current = _mediaPlayer.Time;
            }

            TimecodeDisplay.Text = FormatTime(current);
            DrawTimeline(current, duration);
        }

        // ─── Markers ─────────────────────────────────────────────────────────
        private void OnAddMarkerClick(object? sender, RoutedEventArgs e)
        {
            if (_mediaPlayer == null) return;
            long t = _mediaPlayer.IsPlaying
                ? _stopwatchOffsetMs + _playbackStopwatch.ElapsedMilliseconds
                : _mediaPlayer.Time;
            _markers.Add(t);
            StatusText.Text = $"Marker added at {FormatTimeShort(t)}";
        }

        // ─── Canvas Interaction ───────────────────────────────────────────────
        private void OnCanvasPointerPressed(object? sender, PointerPressedEventArgs e)
        {
            if (_mediaPlayer == null || _mediaPlayer.Length <= 0) return;

            double x = e.GetPosition(ScrubberCanvas).X;
            double w = ScrubberCanvas.Bounds.Width;
            double duration = _mediaPlayer.Length;
            long clickMs = (long)((x / w) * duration);

            switch (_currentTool)
            {
                case EditTool.Razor:
                    RazorCutAt(clickMs);
                    break;

                case EditTool.Selection:
                case EditTool.Ripple:
                    // Check for edge drag first
                    var (seg, isStart) = FindEdgeAt(x, w, duration);
                    if (seg != null)
                    {
                        _dragSegment = seg;
                        _draggingStart = isStart;
                        break;
                    }
                    // Otherwise select segment or scrub
                    var clicked = FindSegmentAt(clickMs);
                    if (clicked != null)
                    {
                        SelectSegment(clicked);
                    }
                    else
                    {
                        SelectSegment(null);
                        SeekTo(x, w);
                    }
                    break;
            }
        }

        private void OnCanvasPointerMoved(object? sender, PointerEventArgs e)
        {
            if (_mediaPlayer == null || _mediaPlayer.Length <= 0) return;
            if (!e.GetCurrentPoint(ScrubberCanvas).Properties.IsLeftButtonPressed) return;

            double x = e.GetPosition(ScrubberCanvas).X;
            double w = ScrubberCanvas.Bounds.Width;
            double duration = _mediaPlayer.Length;
            long dragMs = (long)Math.Clamp(x / w, 0, 1) * _mediaPlayer.Length;

            if (_dragSegment != null)
            {
                DragEdge(_dragSegment, _draggingStart, dragMs);
            }
            else if (_currentTool == EditTool.Selection && _selectedSegment == null)
            {
                SeekTo(x, w);
            }
        }

        private void OnCanvasPointerReleased(object? sender, PointerReleasedEventArgs e)
        {
            _dragSegment = null;
        }

        // ─── Segment Operations ───────────────────────────────────────────────
        private void RazorCutAt(long ms)
        {
            var seg = FindSegmentAt(ms);
            if (seg == null) return;
            if (ms <= seg.StartMs || ms >= seg.EndMs) return;

            var left = new Segment { StartMs = seg.StartMs, EndMs = ms };
            var right = new Segment { StartMs = ms, EndMs = seg.EndMs };

            int idx = _segments.IndexOf(seg);
            _segments.RemoveAt(idx);
            _segments.Insert(idx, right);
            _segments.Insert(idx, left);

            StatusText.Text = $"Cut at {FormatTimeShort(ms)}";
        }

        private void SelectSegment(Segment? seg)
        {
            if (_selectedSegment != null)
                _selectedSegment.IsSelected = false;
            _selectedSegment = seg;
            if (_selectedSegment != null)
                _selectedSegment.IsSelected = true;
        }

        private void DeleteSelectedSegment()
        {
            if (_selectedSegment == null) return;
            _segments.Remove(_selectedSegment);

            // Ripple: shift all segments after the gap left
            long gap = _selectedSegment.DurationMs;
            long gapStart = _selectedSegment.StartMs;
            foreach (var s in _segments.Where(s => s.StartMs >= gapStart))
            {
                s.StartMs -= gap;
                s.EndMs -= gap;
            }

            StatusText.Text = $"Deleted segment ({FormatTimeShort(_selectedSegment.DurationMs)})";
            _selectedSegment = null;
        }

        private void DragEdge(Segment seg, bool isStart, long ms)
        {
            bool isRipple = _currentTool == EditTool.Ripple;
            long delta;

            if (isStart)
            {
                ms = Math.Clamp(ms, seg.StartMs, seg.EndMs - 100);
                delta = ms - seg.StartMs;
                seg.StartMs = ms;
                if (isRipple)
                {
                    // Shift all segments before this one
                    foreach (var s in _segments.Where(s => s.EndMs <= seg.StartMs))
                    {
                        s.StartMs -= delta;
                        s.EndMs -= delta;
                    }
                }
            }
            else
            {
                ms = Math.Clamp(ms, seg.StartMs + 100, _mediaPlayer!.Length);
                delta = ms - seg.EndMs;
                seg.EndMs = ms;
                if (isRipple)
                {
                    // Shift all segments after this one
                    foreach (var s in _segments.Where(s => s.StartMs >= seg.EndMs - delta))
                    {
                        s.StartMs += delta;
                        s.EndMs += delta;
                    }
                }
            }
        }

        private Segment? FindSegmentAt(long ms)
            => _segments.FirstOrDefault(s => ms >= s.StartMs && ms <= s.EndMs);

        private (Segment? seg, bool isStart) FindEdgeAt(double x, double w, double durationMs)
        {
            const double edgeTolerance = 8;
            foreach (var seg in _segments)
            {
                double x1 = (seg.StartMs / durationMs) * w;
                double x2 = (seg.EndMs / durationMs) * w;
                if (Math.Abs(x - x1) <= edgeTolerance) return (seg, true);
                if (Math.Abs(x - x2) <= edgeTolerance) return (seg, false);
            }
            return (null, false);
        }

        private void SeekTo(double x, double w)
        {
            double fraction = Math.Clamp(x / w, 0, 1);
            _mediaPlayer!.Position = (float)fraction;
            _stopwatchOffsetMs = (long)(fraction * _mediaPlayer.Length);
            _playbackStopwatch.Restart();
        }

        // ─── Timeline Drawing ─────────────────────────────────────────────────
        private void DrawTimeline(long currentMs, long durationMs)
        {
            ScrubberCanvas.Children.Clear();
            double w = ScrubberCanvas.Bounds.Width;
            double h = ScrubberCanvas.Bounds.Height;
            if (w <= 0 || durationMs <= 0) return;

            // Draw segments
            foreach (var seg in _segments)
            {
                double x1 = (seg.StartMs / (double)durationMs) * w;
                double x2 = (seg.EndMs / (double)durationMs) * w;
                double segW = x2 - x1;

                // Video track block (top half)
                var videoBlock = new Rectangle
                {
                    Width = segW,
                    Height = h / 2,
                    Fill = seg.IsSelected
                        ? new SolidColorBrush(Color.FromArgb(180, 78, 168, 232))
                        : new SolidColorBrush(Color.FromArgb(100, 78, 168, 232))
                };
                Canvas.SetLeft(videoBlock, x1);
                Canvas.SetTop(videoBlock, 0);
                ScrubberCanvas.Children.Add(videoBlock);

                // Audio track block (bottom half)
                var audioBlock = new Rectangle
                {
                    Width = segW,
                    Height = h / 2,
                    Fill = seg.IsSelected
                        ? new SolidColorBrush(Color.FromArgb(180, 78, 201, 78))
                        : new SolidColorBrush(Color.FromArgb(100, 78, 201, 78))
                };
                Canvas.SetLeft(audioBlock, x1);
                Canvas.SetTop(audioBlock, h / 2);
                ScrubberCanvas.Children.Add(audioBlock);

                // Segment borders
                var border = new Rectangle
                {
                    Width = segW,
                    Height = h,
                    Fill = Brushes.Transparent,
                    Stroke = seg.IsSelected
                        ? new SolidColorBrush(Color.Parse("#e8a020"))
                        : new SolidColorBrush(Color.Parse("#4ea8e8")),
                    StrokeThickness = seg.IsSelected ? 2 : 1
                };
                Canvas.SetLeft(border, x1);
                Canvas.SetTop(border, 0);
                ScrubberCanvas.Children.Add(border);
            }

            // Draw In/Out markers
            if (_inPoint >= 0)
            {
                double inX = (_inPoint / (double)durationMs) * w;
                ScrubberCanvas.Children.Add(new Line
                {
                    StartPoint = new Avalonia.Point(inX, 0),
                    EndPoint = new Avalonia.Point(inX, h),
                    Stroke = new SolidColorBrush(Color.Parse("#4ea8e8")),
                    StrokeThickness = 2
                });
            }
            if (_outPoint >= 0 && _outPoint > _inPoint)
            {
                double outX = (_outPoint / (double)durationMs) * w;
                ScrubberCanvas.Children.Add(new Line
                {
                    StartPoint = new Avalonia.Point(outX, 0),
                    EndPoint = new Avalonia.Point(outX, h),
                    Stroke = new SolidColorBrush(Color.Parse("#4ec94e")),
                    StrokeThickness = 2
                });
            }

            // Draw markers
            foreach (long marker in _markers)
            {
                double mx = (marker / (double)durationMs) * w;
                ScrubberCanvas.Children.Add(new Line
                {
                    StartPoint = new Avalonia.Point(mx, 0),
                    EndPoint = new Avalonia.Point(mx, h),
                    Stroke = new SolidColorBrush(Color.Parse("#cc44cc")),
                    StrokeThickness = 1
                });
                ScrubberCanvas.Children.Add(new Polygon
                {
                    Points = new Avalonia.Collections.AvaloniaList<Avalonia.Point>
                    {
                        new(mx - 4, 0), new(mx + 4, 0), new(mx, 8)
                    },
                    Fill = new SolidColorBrush(Color.Parse("#cc44cc"))
                });
            }

            // Draw playhead
            double playX = (currentMs / (double)durationMs) * w;
            ScrubberCanvas.Children.Add(new Line
            {
                StartPoint = new Avalonia.Point(playX, 0),
                EndPoint = new Avalonia.Point(playX, h),
                Stroke = new SolidColorBrush(Color.Parse("#e8a020")),
                StrokeThickness = 2
            });
            ScrubberCanvas.Children.Add(new Polygon
            {
                Points = new Avalonia.Collections.AvaloniaList<Avalonia.Point>
                {
                    new(playX - 5, 0), new(playX + 5, 0), new(playX, 10)
                },
                Fill = new SolidColorBrush(Color.Parse("#e8a020"))
            });
        }

        // ─── Folder / File Loading ────────────────────────────────────────────
        private async void OnOpenFolderClick(object? sender, RoutedEventArgs e)
        {
            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "Select your video folder",
                AllowMultiple = false
            });
            if (folders.Count > 0)
            {
                _rootFolderPath = folders[0].Path.LocalPath;
                StatusText.Text = $"Loaded: {_rootFolderPath}";
                PopulateFolderTree(_rootFolderPath);
            }
        }

        private void PopulateFolderTree(string folderPath)
        {
            FolderTree.Items.Clear();
            var root = BuildTreeItem(folderPath, isRoot: true);
            if (root != null) FolderTree.Items.Add(root);
        }

        private TreeViewItem? BuildTreeItem(string path, bool isRoot = false)
        {
            var videoExtensions = new[] { ".mp4", ".mov", ".mkv", ".avi", ".wmv" };
            if (!Directory.Exists(path)) return null;

            var dirInfo = new DirectoryInfo(path);
            var videoFiles = dirInfo.GetFiles()
                .Where(f => videoExtensions.Contains(f.Extension.ToLower()))
                .ToList();
            var subDirs = dirInfo.GetDirectories();

            if (!isRoot && videoFiles.Count == 0 && subDirs.Length == 0)
                return null;

            var folderItem = new TreeViewItem
            {
                Header = "📂  " + dirInfo.Name,
                IsExpanded = isRoot
            };
            foreach (var sub in subDirs)
            {
                var subItem = BuildTreeItem(sub.FullName);
                if (subItem != null) folderItem.Items.Add(subItem);
            }
            foreach (var file in videoFiles)
                folderItem.Items.Add(BuildFileItem(file.FullName));

            return folderItem;
        }

        private TreeViewItem BuildFileItem(string filePath)
        {
            string icon = _exportedFiles.Contains(filePath) ? "✅" : "🎬";
            return new TreeViewItem
            {
                Header = $"{icon}  {System.IO.Path.GetFileName(filePath)}",
                Tag = filePath
            };
        }

        private void OnTreeSelectionChanged(object? sender, SelectionChangedEventArgs e)
        {
            if (FolderTree.SelectedItem is TreeViewItem item && item.Tag is string filePath)
                LoadClip(filePath);
        }

        private void LoadClip(string filePath)
        {
            _playbackRate = 1.0f;
            _mediaPlayer?.SetRate(1.0f);
            _currentFilePath = filePath;
            StatusText.Text = $"Loaded: {System.IO.Path.GetFileName(filePath)}";
            NoClipText.IsVisible = false;
            VideoView.IsVisible = true;

            _inPoint = -1;
            _outPoint = -1;
            InPointDisplay.Text = "In: --:--:--";
            OutPointDisplay.Text = "Out: --:--:--";

            _markers.Clear();
            _selectedSegment = null;

            var media = new Media(_libVLC!, new Uri(filePath));
            _mediaPlayer!.Media = media;

            // Wait briefly for VLC to get the duration, then init segments
            _segments.Clear();
            _mediaPlayer.Play();
            _stopwatchOffsetMs = 0;
            _playbackStopwatch.Restart();
            BtnPlayPause.Content = "⏸";

            // Init the single full-clip segment after a short delay
            var initTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
            initTimer.Tick += (s, ev) =>
            {
                initTimer.Stop();
                if (_mediaPlayer.Length > 0 && _segments.Count == 0)
                {
                    _segments.Add(new Segment { StartMs = 0, EndMs = _mediaPlayer.Length });
                }
            };
            initTimer.Start();
        }

        // ─── Playback Controls ────────────────────────────────────────────────
        private void OnPlayPauseClick(object? sender, RoutedEventArgs e)
        {
            if (_mediaPlayer == null) return;
            if (_mediaPlayer.IsPlaying)
            {
                _mediaPlayer.SetPause(true);
                _playbackStopwatch.Stop();
                BtnPlayPause.Content = "▶";
            }
            else
            {
                _stopwatchOffsetMs = _mediaPlayer.Time;
                _playbackStopwatch.Restart();
                _mediaPlayer.Play();
                BtnPlayPause.Content = "⏸";
            }
        }

        private void OnSkipBackClick(object? sender, RoutedEventArgs e)
        {
            if (_mediaPlayer == null) return;
            _mediaPlayer.Time = Math.Max(0, _mediaPlayer.Time - 5000);
            _stopwatchOffsetMs = _mediaPlayer.Time;
            _playbackStopwatch.Restart();
        }

        private void OnSkipFwdClick(object? sender, RoutedEventArgs e)
        {
            if (_mediaPlayer == null) return;
            _mediaPlayer.Time = Math.Min(_mediaPlayer.Length, _mediaPlayer.Time + 5000);
            _stopwatchOffsetMs = _mediaPlayer.Time;
            _playbackStopwatch.Restart();
        }

        // ─── Set In / Set Out ─────────────────────────────────────────────────
        private void OnSetInClick(object? sender, RoutedEventArgs e)
        {
            if (_mediaPlayer == null) return;
            _inPoint = _mediaPlayer.IsPlaying
                ? _stopwatchOffsetMs + _playbackStopwatch.ElapsedMilliseconds
                : _mediaPlayer.Time;
            InPointDisplay.Text = $"In: {FormatTimeShort(_inPoint)}";
            StatusText.Text = $"In point set at {FormatTimeShort(_inPoint)}";
        }

        private void OnSetOutClick(object? sender, RoutedEventArgs e)
        {
            if (_mediaPlayer == null) return;
            _outPoint = _mediaPlayer.IsPlaying
                ? _stopwatchOffsetMs + _playbackStopwatch.ElapsedMilliseconds
                : _mediaPlayer.Time;
            OutPointDisplay.Text = $"Out: {FormatTimeShort(_outPoint)}";
            StatusText.Text = $"Out point set at {FormatTimeShort(_outPoint)}";
        }

        private void OnClearPointsClick(object? sender, RoutedEventArgs e)
        {
            _inPoint = -1;
            _outPoint = -1;
            InPointDisplay.Text = "In: --:--:--";
            OutPointDisplay.Text = "Out: --:--:--";
            StatusText.Text = "In/Out points cleared";
        }

        // ─── Keyboard Shortcuts ───────────────────────────────────────────────
        private void OnKeyDown(object? sender, KeyEventArgs e)
        {
            if (_mediaPlayer == null) return;
            switch (e.Key)
            {
                case Key.Space:
                    OnPlayPauseClick(null, null!);
                    break;
                case Key.I:
                    OnSetInClick(null, null!);
                    break;
                case Key.O:
                    OnSetOutClick(null, null!);
                    break;
                case Key.M:
                    OnAddMarkerClick(null, null!);
                    break;
                case Key.V:
                    SetTool(EditTool.Selection);
                    break;
                case Key.C:
                    SetTool(EditTool.Razor);
                    break;
                case Key.B:
                    SetTool(EditTool.Ripple);
                    break;
                case Key.Delete:
                    DeleteSelectedSegment();
                    break;
                case Key.K:
                    _mediaPlayer.SetPause(true);
                    _mediaPlayer.SetRate(1.0f);
                    _playbackRate = 1.0f;
                    _playbackStopwatch.Stop();
                    BtnPlayPause.Content = "▶";
                    break;
                case Key.L:
                    if (_mediaPlayer.Rate < 0) _playbackRate = 1.0f;
                    else _playbackRate = Math.Min(_playbackRate * 2f, 16f);
                    _mediaPlayer.SetRate(_playbackRate);
                    _mediaPlayer.Play();
                    _stopwatchOffsetMs = _mediaPlayer.Time;
                    _playbackStopwatch.Restart();
                    BtnPlayPause.Content = "⏸";
                    StatusText.Text = $"Speed: {_playbackRate}x";
                    break;
                case Key.J:
                    if (_mediaPlayer.Rate > 0) _playbackRate = -1.0f;
                    else _playbackRate = Math.Max(_playbackRate * 2f, -16f);
                    _mediaPlayer.SetRate(_playbackRate);
                    _mediaPlayer.Play();
                    _stopwatchOffsetMs = _mediaPlayer.Time;
                    _playbackStopwatch.Restart();
                    BtnPlayPause.Content = "⏸";
                    StatusText.Text = $"Speed: {_playbackRate}x";
                    break;
            }
        }

        // ─── Export ───────────────────────────────────────────────────────────
        private async void OnExportClick(object? sender, RoutedEventArgs e)
        {
            if (_currentFilePath == null) { StatusText.Text = "No clip loaded!"; return; }
            if (_segments.Count == 0) { StatusText.Text = "No segments to export!"; return; }
            if (_rootFolderPath == null) { StatusText.Text = "No root folder selected!"; return; }

            string ffmpegDir = System.IO.Path.Combine(AppContext.BaseDirectory, "ffmpeg");
            FFmpeg.SetExecutablesPath(ffmpegDir);

            string outputRoot;
            if (_customOutputPath != null)
            {
                if (!Directory.Exists(_customOutputPath))
                {
                    ShakeExportButton();
                    StatusText.Text = "Export location no longer exists!";
                    return;
                }
                string rootNameCustom = System.IO.Path.GetFileName(
                    _rootFolderPath.TrimEnd(System.IO.Path.DirectorySeparatorChar));
                outputRoot = System.IO.Path.Combine(_customOutputPath, $"SevenCuts_{rootNameCustom}");
            }
            else
            {
                string cleanRoot = _rootFolderPath.TrimEnd(System.IO.Path.DirectorySeparatorChar);
                string rootParent = System.IO.Path.GetDirectoryName(cleanRoot)!;
                string rootName = System.IO.Path.GetFileName(cleanRoot);
                outputRoot = System.IO.Path.Combine(rootParent, $"SevenCuts_{rootName}");
            }

            string relativePath = System.IO.Path.GetRelativePath(_rootFolderPath, _currentFilePath);
            string outputFilePath = System.IO.Path.Combine(outputRoot, relativePath);
            string outputDir = System.IO.Path.GetDirectoryName(outputFilePath)!;
            Directory.CreateDirectory(outputDir);

            _mediaPlayer?.SetPause(true);
            BtnExport.IsEnabled = false;

            try
            {
                if (_segments.Count == 1)
                {
                    // Single segment — simple cut
                    var seg = _segments[0];
                    var start = TimeSpan.FromMilliseconds(seg.StartMs);
                    var duration = TimeSpan.FromMilliseconds(seg.DurationMs);
                    string args = $"-y -ss {start:hh\\:mm\\:ss\\.fff} -i \"{_currentFilePath}\" " +
                                   $"-t {duration:hh\\:mm\\:ss\\.fff} -c copy \"{outputFilePath}\"";
                    StatusText.Text = "Exporting...";
                    await FFmpeg.Conversions.New().Start(args);
                }
                else
                {
                    // Multiple segments — cut each then concat
                    string tempDir = System.IO.Path.Combine(outputDir, "_sevcuts_temp");
                    Directory.CreateDirectory(tempDir);
                    var tempFiles = new List<string>();

                    for (int i = 0; i < _segments.Count; i++)
                    {
                        var seg = _segments[i];
                        string temp = System.IO.Path.Combine(tempDir, $"part{i:D3}.mp4");
                        var start = TimeSpan.FromMilliseconds(seg.StartMs);
                        var duration = TimeSpan.FromMilliseconds(seg.DurationMs);
                        string args = $"-y -ss {start:hh\\:mm\\:ss\\.fff} -i \"{_currentFilePath}\" " +
                                       $"-t {duration:hh\\:mm\\:ss\\.fff} -c copy \"{temp}\"";
                        StatusText.Text = $"Exporting segment {i + 1}/{_segments.Count}...";
                        await FFmpeg.Conversions.New().Start(args);
                        tempFiles.Add(temp);
                    }

                    // Write concat list
                    string listFile = System.IO.Path.Combine(tempDir, "concat.txt");
                    File.WriteAllLines(listFile, tempFiles.Select(f => $"file '{f}'"));

                    StatusText.Text = "Joining segments...";
                    string concatArgs = $"-y -f concat -safe 0 -i \"{listFile}\" -c copy \"{outputFilePath}\"";
                    await FFmpeg.Conversions.New().Start(concatArgs);

                    // Cleanup temp files
                    Directory.Delete(tempDir, true);
                }

                _exportedFiles.Add(_currentFilePath);
                PopulateFolderTree(_rootFolderPath!);
                StatusText.Text = $"✅ Exported to: {outputFilePath}";
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Export failed: {ex.Message}";
            }
            finally
            {
                BtnExport.IsEnabled = true;
            }
        }

        private async void OnOutputFolderClick(object? sender, RoutedEventArgs e)
        {
            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "Select export destination folder",
                AllowMultiple = false
            });
            if (folders.Count > 0)
            {
                _customOutputPath = folders[0].Path.LocalPath;
                BtnOutputFolder.Foreground = new SolidColorBrush(Color.Parse("#e8a020"));
                ToolTip.SetTip(BtnOutputFolder, $"Export Location: {_customOutputPath}");
                StatusText.Text = $"Export location set: {_customOutputPath}";
            }
        }

        private void ShakeExportButton()
        {
            BtnOutputFolder.Foreground = new SolidColorBrush(Color.Parse("#cc4444"));
            ToolTip.SetTip(BtnOutputFolder, "Folder not found! Click to set a new one.");
            int shakeCount = 0;
            var shakeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
            shakeTimer.Tick += (s, ev) =>
            {
                shakeCount++;
                BtnExport.Margin = shakeCount % 2 == 0
                    ? new Avalonia.Thickness(0)
                    : new Avalonia.Thickness(6, 0, 0, 0);
                if (shakeCount >= 10) { shakeTimer.Stop(); BtnExport.Margin = new Avalonia.Thickness(0); }
            };
            shakeTimer.Start();
        }

        // ─── Helpers ──────────────────────────────────────────────────────────
        private static string FormatTime(long ms)
        {
            if (ms < 0) ms = 0;
            var t = TimeSpan.FromMilliseconds(ms);
            return $"{t.Hours:D2}:{t.Minutes:D2}:{t.Seconds:D2}.{t.Milliseconds:D3}";
        }

        private static string FormatTimeShort(long ms)
        {
            if (ms < 0) ms = 0;
            var t = TimeSpan.FromMilliseconds(ms);
            return $"{t.Hours:D2}:{t.Minutes:D2}:{t.Seconds:D2}";
        }
    }
}