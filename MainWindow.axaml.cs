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

        private double _lastMouseX = -1;
        private double _razorInputX = 0;

        //Segments
        private Segment? _movingSegment = null;
        private long _moveDragOffsetMs = 0;

        // Scrubber
        private bool _isScrubbing = false;
        private bool _wasPlayingBeforeScrub = false;
        private bool _hoveringPlayhead = false;
        private long _razorPreviewMs = -1;

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

            RazorTextBox.TextChanged += (s, te) =>
            {
                if (RazorTextBox.Tag is bool busy && busy) return;
                RazorTextBox.Tag = true; // prevent re-entry

                // Strip everything except digits
                string digits = new string(RazorTextBox.Text?.Where(char.IsDigit).ToArray() ?? Array.Empty<char>());
                if (digits.Length > 9) digits = digits[..9];

                // Rebuild as HH:MM:SS.mmm
                string formatted = "";
                for (int i = 0; i < digits.Length; i++)
                {
                    if (i == 2 || i == 4) formatted += ":";
                    if (i == 6) formatted += ".";
                    formatted += digits[i];
                }

                RazorTextBox.Text = formatted;
                RazorTextBox.CaretIndex = formatted.Length;

                // Validate and color the text
                bool valid = TimeSpan.TryParseExact(formatted,
                    new[] { @"hh\:mm\:ss\.fff", @"mm\:ss\.fff", @"mm\:ss", @"hh\:mm\:ss" },
                    null, out TimeSpan parsed)
                    && (long)parsed.TotalMilliseconds > 0
                    && (long)parsed.TotalMilliseconds < (_mediaPlayer?.Length ?? 0);

                RazorTextBox.Foreground = new SolidColorBrush(
                    valid ? Color.Parse("#44cc44") : Color.Parse("#ff4444"));
                RazorDurationHint.Foreground = new SolidColorBrush(
                    valid ? Color.Parse("#44cc44") : Color.Parse("#888888"));

                RazorTextBox.Tag = false;
            };

            RazorTextBox.KeyDown += (s, ke) =>
            {
                if (ke.Key == Key.Enter)
                {
                    ke.Handled = true;
                    if (TimeSpan.TryParseExact(RazorTextBox.Text,
                        new[] { @"hh\:mm\:ss\.fff", @"mm\:ss\.fff", @"mm\:ss", @"hh\:mm\:ss" },
                        null, out TimeSpan result))
                    {
                        long ms = (long)result.TotalMilliseconds;
                        if (ms <= 0 || ms >= (_mediaPlayer?.Length ?? 0))
                        {
                            ShakeRazorInput();
                            return;
                        }
                        RazorTextBox.IsVisible = false;
                        RazorDurationHint.IsVisible = false;
                        RazorCutAt(ms);
                        StatusText.Text = $"Cut at {FormatTime(ms)}";
                    }
                    else
                    {
                        ShakeRazorInput();
                    }
                }
            };

            ScrubberCanvas.PointerExited += (s, e) =>
            {
                _razorPreviewMs = -1;
                ScrubberCanvas.Cursor = new Cursor(StandardCursorType.Arrow);
            };

            this.Opened += (s, e) =>
            {
                if (VideoView.VideoHandle != IntPtr.Zero)
                    _mediaPlayer!.Hwnd = VideoView.VideoHandle;
            };

            this.KeyDown += OnKeyDown;
            this.AddHandler(KeyDownEvent, OnWindowKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);

            _uiTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(16)
            };
            _uiTimer.Tick += OnUiTimerTick;
            _uiTimer.Start();

            _mediaPlayer.EndReached += (s, e) =>
            {
                Dispatcher.UIThread.Post(() =>
                {
                    _mediaPlayer.Stop();
                    _mediaPlayer.Play();
                    _mediaPlayer.SetPause(true);
                    _stopwatchOffsetMs = 0;
                    _playbackStopwatch.Stop();
                    BtnPlayPause.Content = "▶";
                });
            };
        }

        // ─── Tool Switching ───────────────────────────────────────────────────
        private void SetTool(EditTool tool)
        {
            _razorPreviewMs = -1;
            _lastMouseX = -1;

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
                EditTool.Razor => "Razor: click on timeline to cut  |  Tab to type exact timestamp",
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

            if (!RazorTextBox.IsVisible)
            {
                if (_currentTool == EditTool.Razor && _lastMouseX >= 0 && duration > 0)
                    _razorPreviewMs = (long)(Math.Clamp(_lastMouseX / ScrubberCanvas.Bounds.Width, 0, 1) * duration);
                else if (_currentTool != EditTool.Razor)
                    _razorPreviewMs = -1;
            }

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
            StatusText.Text = $"Marker added at {FormatTime(t)}";
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
                    var (seg, isStart) = FindEdgeAt(x, w, duration);
                    if (seg != null)
                    {
                        _dragSegment = seg;
                        _draggingStart = isStart;
                        break;
                    }

                    var midSeg = FindSegmentAt(clickMs);
                    if (midSeg != null)
                    {
                        SelectSegment(midSeg);
                        _movingSegment = midSeg;
                        _moveDragOffsetMs = clickMs - midSeg.StartMs;
                        break;
                    }

                    double playheadX = (_stopwatchOffsetMs +
                        (_mediaPlayer.IsPlaying ? _playbackStopwatch.ElapsedMilliseconds : 0))
                        / (double)duration * w;

                    if (Math.Abs(x - playheadX) <= 16)
                    {
                        _isScrubbing = true;
                        _wasPlayingBeforeScrub = _mediaPlayer.IsPlaying;
                        _mediaPlayer.SetPause(true);
                        _playbackStopwatch.Stop();
                        BtnPlayPause.Content = "▶";
                        SeekTo(x, w);
                        break;
                    }

                    SelectSegment(null);
                    SeekTo(x, w);
                    break;
            }
        }

        private void OnCanvasPointerMoved(object? sender, PointerEventArgs e)
        {
            double duration = _mediaPlayer?.Length ?? 0;
            _lastMouseX = e.GetPosition(ScrubberCanvas).X;
            double x = _lastMouseX;
            double w = ScrubberCanvas.Bounds.Width;

            if (duration > 0)
            {
                double playheadX = (_stopwatchOffsetMs +
                    (_mediaPlayer!.IsPlaying ? _playbackStopwatch.ElapsedMilliseconds : 0))
                    / duration * w;
                _hoveringPlayhead = Math.Abs(x - playheadX) <= 16;
            }

            if (_currentTool == EditTool.Razor)
                _razorPreviewMs = (long)(Math.Clamp(x / w, 0, 1) * duration);
            else
                _razorPreviewMs = -1;

            if (_currentTool == EditTool.Razor)
                ScrubberCanvas.Cursor = new Cursor(StandardCursorType.Cross);
            else if (_hoveringPlayhead || _isScrubbing)
                ScrubberCanvas.Cursor = new Cursor(StandardCursorType.SizeWestEast);
            else if (FindSegmentAt((long)((x / w) * duration)) != null)
                ScrubberCanvas.Cursor = new Cursor(StandardCursorType.SizeAll);
            else if (FindEdgeAt(x, w, duration).seg != null)
                ScrubberCanvas.Cursor = new Cursor(StandardCursorType.SizeWestEast);
            else
                ScrubberCanvas.Cursor = new Cursor(StandardCursorType.Arrow);

            if (_mediaPlayer == null || _mediaPlayer.Length <= 0) return;
            if (!e.GetCurrentPoint(ScrubberCanvas).Properties.IsLeftButtonPressed) return;

            if (_dragSegment != null)
            {
                long dragMs = (long)(Math.Clamp(x / w, 0, 1) * _mediaPlayer.Length);
                DragEdge(_dragSegment, _draggingStart, dragMs);
            }
            else if (_isScrubbing)
            {
                SeekTo(x, w);
            }

            if (_dragSegment != null)
            {
                long dragMs = (long)(Math.Clamp(x / w, 0, 1) * _mediaPlayer.Length);
                DragEdge(_dragSegment, _draggingStart, dragMs);
            }
            else if (_movingSegment != null)
            {
                long newStart = (long)(Math.Clamp(x / w, 0, 1) * _mediaPlayer.Length) - _moveDragOffsetMs;
                long duration2 = _movingSegment.DurationMs;
                newStart = Math.Clamp(newStart, 0, _mediaPlayer.Length - duration2);
                _movingSegment.StartMs = newStart;
                _movingSegment.EndMs = newStart + duration2;
            }
            else if (_isScrubbing)
            {
                SeekTo(x, w);
            }
        }

        private void OnCanvasPointerReleased(object? sender, PointerReleasedEventArgs e)
        {
            _dragSegment = null;
            _movingSegment = null;

            if (_isScrubbing)
            {
                _isScrubbing = false;
                if (_wasPlayingBeforeScrub)
                {
                    _mediaPlayer?.Play();
                    _stopwatchOffsetMs = _mediaPlayer!.Time;
                    _playbackStopwatch.Restart();
                    BtnPlayPause.Content = "⏸";
                }
            }
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

            StatusText.Text = $"Cut at {FormatTime(ms)}";
        }

        private void SelectSegment(Segment? seg)
        {
            if (_selectedSegment != null) _selectedSegment.IsSelected = false;
            _selectedSegment = seg;
            if (_selectedSegment != null) _selectedSegment.IsSelected = true;
        }

        private void DeleteSelectedSegment()
        {
            if (_selectedSegment == null) return;
            StatusText.Text = $"Deleted segment ({FormatTime(_selectedSegment.DurationMs)})";
            _segments.Remove(_selectedSegment);
            _selectedSegment = null;
        }

        private void DragEdge(Segment seg, bool isStart, long ms)
        {
            if (isStart)
                seg.StartMs = Math.Clamp(ms, 0, seg.EndMs - 100);
            else
                seg.EndMs = Math.Clamp(ms, seg.StartMs + 100, _mediaPlayer!.Length);
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
            double h = ScrubberCanvas.Bounds.Height * 2;
            if (w <= 0 || durationMs <= 0) return;


            // Razor preview line
            if (_razorPreviewMs >= 0 && _currentTool == EditTool.Razor && !RazorTextBox.IsVisible)
            {
                double rx = (_razorPreviewMs / (double)durationMs) * w;
                ScrubberCanvas.Children.Add(new Line
                {
                    StartPoint = new Avalonia.Point(rx, 0),
                    EndPoint = new Avalonia.Point(rx, h),
                    Stroke = new SolidColorBrush(Color.Parse("#ff4444")),
                    StrokeThickness = 1,
                    StrokeDashArray = new Avalonia.Collections.AvaloniaList<double> { 4, 4 }
                });

                // Time label
                var label = new TextBlock
                {
                    Text = FormatTime(_razorPreviewMs),
                    Foreground = new SolidColorBrush(Color.Parse("#ff4444")),
                    FontSize = 10,
                    FontFamily = new FontFamily("Consolas")
                };
                Canvas.SetLeft(label, rx + 4);
                Canvas.SetTop(label, 2);
                ScrubberCanvas.Children.Add(label);

                // TAB hint badge
                var tabBadge = new Border
                {
                    Background = new SolidColorBrush(Color.Parse("#ff4444")),
                    CornerRadius = new Avalonia.CornerRadius(3),
                    Padding = new Avalonia.Thickness(4, 2),
                    Child = new TextBlock
                    {
                        Text = "TAB to type",
                        Foreground = Brushes.White,
                        FontSize = 9,
                        FontWeight = Avalonia.Media.FontWeight.Bold
                    }
                };
                Canvas.SetLeft(tabBadge, rx + 4);
                Canvas.SetTop(tabBadge, h - 20);
                ScrubberCanvas.Children.Add(tabBadge);
            }

            // ENTER hint when input box is open
            if (RazorTextBox.IsVisible && _currentTool == EditTool.Razor)
            {
                double rx = (_razorPreviewMs >= 0)
                    ? (_razorPreviewMs / (double)durationMs) * w
                    : RazorTextBox.Margin.Left;

                var enterBadge = new Border
                {
                    Background = new SolidColorBrush(Color.Parse("#e8a020")),
                    CornerRadius = new Avalonia.CornerRadius(3),
                    Padding = new Avalonia.Thickness(4, 2),
                    Child = new TextBlock
                    {
                        Text = "ENTER to cut  •  ESC to cancel",
                        Foreground = Brushes.White,
                        FontSize = 9,
                        FontWeight = Avalonia.Media.FontWeight.Bold
                    }
                };
                Canvas.SetLeft(enterBadge, RazorTextBox.Margin.Left);
                Canvas.SetTop(enterBadge, RazorTextBox.Margin.Top + 32);
                ScrubberCanvas.Children.Add(enterBadge);
            }

            // Subtle lane backgrounds
            ScrubberCanvas.Children.Add(new Rectangle
            {
                Width = w,
                Height = h / 2,
                Fill = new SolidColorBrush(Color.FromArgb(15, 78, 168, 232))
            });
            var audioBg = new Rectangle
            {
                Width = w,
                Height = h / 2,
                Fill = new SolidColorBrush(Color.FromArgb(15, 78, 201, 78))
            };
            Canvas.SetTop(audioBg, h / 2);
            ScrubberCanvas.Children.Add(audioBg);

            // Draw segments
            foreach (var seg in _segments)
            {
                double x1 = (seg.StartMs / (double)durationMs) * w;
                double x2 = (seg.EndMs / (double)durationMs) * w;
                double segW = x2 - x1;
                double stripH = 20;
                double gap = 2;
                double videoY = h / 2 - stripH - gap;
                double audioY = h / 2 + gap;

                ScrubberCanvas.Children.Add(new Rectangle
                {
                    Width = segW,
                    Height = stripH,
                    Fill = new SolidColorBrush(seg.IsSelected ? Color.FromArgb(220, 78, 168, 232) : Color.FromArgb(180, 78, 168, 232)),
                    [Canvas.LeftProperty] = x1,
                    [Canvas.TopProperty] = videoY
                });

                ScrubberCanvas.Children.Add(new Rectangle
                {
                    Width = segW,
                    Height = stripH,
                    Fill = new SolidColorBrush(seg.IsSelected ? Color.FromArgb(220, 78, 201, 78) : Color.FromArgb(180, 78, 201, 78)),
                    [Canvas.LeftProperty] = x1,
                    [Canvas.TopProperty] = audioY
                });

                if (seg.IsSelected)
                    ScrubberCanvas.Children.Add(new Rectangle
                    {
                        Width = segW,
                        Height = stripH * 2 + 4,
                        Fill = Brushes.Transparent,
                        Stroke = new SolidColorBrush(Color.Parse("#e8a020")),
                        StrokeThickness = 2,
                        [Canvas.LeftProperty] = x1,
                        [Canvas.TopProperty] = videoY
                    });
            }

            ScrubberCanvas.Children.Add(new Line
            {
                StartPoint = new Avalonia.Point(0, h / 2),
                EndPoint = new Avalonia.Point(w, h / 2),
                Stroke = new SolidColorBrush(Color.Parse("#555555")),
                StrokeThickness = 2
            });

            // Dividing line between VIDEO and AUDIO rows
            ScrubberCanvas.Children.Add(new Line
            {
                StartPoint = new Avalonia.Point(0, h / 2),
                EndPoint = new Avalonia.Point(w, h / 2),
                Stroke = new SolidColorBrush(Color.Parse("#555555")),
                StrokeThickness = 2
            });

            // Draw In/Out highlight region
            if (_inPoint >= 0 && _outPoint > _inPoint)
            {
                double x1 = (_inPoint / (double)durationMs) * w;
                double x2 = (_outPoint / (double)durationMs) * w;
                var highlight = new Rectangle
                {
                    Width = x2 - x1,
                    Height = h,
                    Fill = new SolidColorBrush(Color.FromArgb(40, 255, 255, 255))
                };
                Canvas.SetLeft(highlight, x1);
                Canvas.SetTop(highlight, 0);
                ScrubberCanvas.Children.Add(highlight);
            }

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
            var playheadColor = _hoveringPlayhead ? Color.Parse("#ffffff") : Color.Parse("#e8a020");

            // Full height line from top to bottom
            ScrubberCanvas.Children.Add(new Line
            {
                StartPoint = new Avalonia.Point(playX, 0),
                EndPoint = new Avalonia.Point(playX, h),
                Stroke = new SolidColorBrush(playheadColor),
                StrokeThickness = _hoveringPlayhead ? 3 : 2
            });

            // Triangle at very top
            ScrubberCanvas.Children.Add(new Polygon
            {
                Points = new Avalonia.Collections.AvaloniaList<Avalonia.Point>
    {
        new(playX - 6, 0), new(playX + 6, 0), new(playX, 10)
    },
                Fill = new SolidColorBrush(playheadColor)
            });

            // Draw gap markers between segments
            var sorted = _segments.OrderBy(s => s.StartMs).ToList();
            for (int i = 0; i < sorted.Count - 1; i++)
            {
                double gapX1 = (sorted[i].EndMs / (double)durationMs) * w;
                double gapX2 = (sorted[i + 1].StartMs / (double)durationMs) * w;
                if (gapX2 - gapX1 > 2)
                {
                    var gapRect = new Rectangle
                    {
                        Width = gapX2 - gapX1,
                        Height = h,
                        Fill = new SolidColorBrush(Color.FromArgb(120, 0, 0, 0))
                    };
                    Canvas.SetLeft(gapRect, gapX1);
                    Canvas.SetTop(gapRect, 0);
                    ScrubberCanvas.Children.Add(gapRect);

                    var gapLabel = new TextBlock
                    {
                        Text = "GAP",
                        Foreground = new SolidColorBrush(Color.Parse("#555555")),
                        FontSize = 9,
                        FontWeight = Avalonia.Media.FontWeight.Bold
                    };
                    Canvas.SetLeft(gapLabel, gapX1 + (gapX2 - gapX1) / 2 - 10);
                    Canvas.SetTop(gapLabel, h / 2 - 6);
                    ScrubberCanvas.Children.Add(gapLabel);
                }
            }
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
                _exportedFiles.Clear();
                LoadExportedFiles();
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
            bool exported = _exportedFiles.Contains(filePath);

            // emoji and color selection
            string iconText = exported ? "✅" : "🎬";
            var iconColor = exported ? Color.Parse("#4ec94e") : Color.Parse("#4ea8e8");

            // icon TextBlock with colored foreground
            var iconTextBlock = new TextBlock
            {
                Text = iconText,
                Foreground = new SolidColorBrush(iconColor),
                FontSize = 14,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                Margin = new Avalonia.Thickness(0, 0, 6, 0)
            };

            // filename TextBlock
            var fileNameTextBlock = new TextBlock
            {
                Text = System.IO.Path.GetFileName(filePath),
                Foreground = new SolidColorBrush(Color.Parse("#cccccc")),
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
            };

            // combine into a horizontal header
            var headerPanel = new StackPanel
            {
                Orientation = Avalonia.Layout.Orientation.Horizontal,
                Children = { iconTextBlock, fileNameTextBlock }
            };

            return new TreeViewItem
            {
                Header = headerPanel,
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
            StatusText.Text = $"Loaded: {System.IO.Path.GetFileName(filePath)}"};
            RazorDurationHint.IsVisible = false;
            NoClipText.IsVisible = false;
            VideoView.IsVisible = true;

            _inPoint = -1;
            _outPoint = -1;
            InPointDisplay.Text = "In: --:--:--";
            OutPointDisplay.Text = "Out: --:--:--";

            _markers.Clear();
            _selectedSegment = null;
            RazorTextBox.IsVisible = false;

            var media = new Media(_libVLC!, new Uri(filePath));
            _mediaPlayer!.Media = media;

            _segments.Clear();
            _mediaPlayer.Play();
            _stopwatchOffsetMs = 0;
            _playbackStopwatch.Restart();
            BtnPlayPause.Content = "⏸";

            var initTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
            initTimer.Tick += (s, ev) =>
            {
                initTimer.Stop();
                if (_mediaPlayer.Length > 0 && _segments.Count == 0)
                    _segments.Add(new Segment { StartMs = 0, EndMs = _mediaPlayer.Length });
            };
            initTimer.Start();

            _mediaPlayer.EndReached += (s, e) =>
            {
                Dispatcher.UIThread.Post(() =>
                {
                    _mediaPlayer.Stop();
                    _mediaPlayer.Play();
                    _mediaPlayer.SetPause(true);
                    _stopwatchOffsetMs = 0;
                    _playbackStopwatch.Stop();
                    BtnPlayPause.Content = "▶";
                });
            };
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
            InPointDisplay.Text = $"In: {FormatTime(_inPoint)}";
            StatusText.Text = $"In point set at {FormatTime(_inPoint)}";
        }

        private void OnSetOutClick(object? sender, RoutedEventArgs e)
        {
            if (_mediaPlayer == null) return;
            _outPoint = _mediaPlayer.IsPlaying
                ? _stopwatchOffsetMs + _playbackStopwatch.ElapsedMilliseconds
                : _mediaPlayer.Time;
            OutPointDisplay.Text = $"Out: {FormatTime(_outPoint)}";
            StatusText.Text = $"Out point set at {FormatTime(_outPoint)}";
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
        private void OnWindowKeyDown(object? sender, KeyEventArgs e)
        {
            // Tab opens razor timestamp input
            if (e.Key == Key.Tab && _currentTool == EditTool.Razor && _lastMouseX >= 0)
            {
                e.Handled = true;
                if (RazorTextBox.IsVisible) return;

                long snappedMs = _razorPreviewMs;
                RazorTextBox.Text = FormatTime(snappedMs);
                _razorInputX = Math.Min(_lastMouseX, ScrubberCanvas.Bounds.Width - 130);
                RazorTextBox.Margin = new Avalonia.Thickness(
                    _razorInputX, ScrubberCanvas.Bounds.Height / 2 - 12, 0, 0);
                RazorTextBox.IsVisible = true;
                RazorDurationHint.Text = $"  {FormatTime(_mediaPlayer?.Length ?? 0)}";
                RazorDurationHint.Margin = new Avalonia.Thickness(_razorInputX, RazorTextBox.Margin.Top + 20, 0, 0); RazorDurationHint.IsVisible = true;
                RazorTextBox.CaretIndex = RazorTextBox.Text.Length;
                RazorTextBox.Focus();
                return;
            }

            // Escape closes it
            if (e.Key == Key.Escape && RazorTextBox.IsVisible)
            {
                e.Handled = true;
                RazorTextBox.IsVisible = false;
                RazorDurationHint.IsVisible = false;
                return;
            }

            // Block all nav keys while input is open
            if (RazorTextBox.IsVisible)
            {
                if (e.Key == Key.Left || e.Key == Key.Right ||
                    e.Key == Key.Up || e.Key == Key.Down)
                    e.Handled = true;
            }
        }

        private void OnKeyDown(object? sender, KeyEventArgs e)
        {
            if (RazorTextBox.IsVisible) return;
            if (_mediaPlayer == null) return;

            switch (e.Key)
            {
                case Key.Space: OnPlayPauseClick(null, null!); break;
                case Key.I: OnSetInClick(null, null!); break;
                case Key.O: OnSetOutClick(null, null!); break;
                case Key.M: OnAddMarkerClick(null, null!); break;
                case Key.V: SetTool(EditTool.Selection); break;
                case Key.C: SetTool(EditTool.Razor); break;
                case Key.B: SetTool(EditTool.Ripple); break;
                case Key.Delete: DeleteSelectedSegment(); break;

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

        private void ShakeRazorInput()
        {
            int shakeCount = 0;
            var shakeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
            shakeTimer.Tick += (s, ev) =>
            {
                shakeCount++;
                double offsetX = shakeCount % 2 == 0 ? _razorInputX : _razorInputX + 6;
                double topBox = ScrubberCanvas.Bounds.Height / 2 - 12;
                RazorTextBox.Margin = new Avalonia.Thickness(offsetX, topBox, 0, 0);
                RazorDurationHint.Margin = new Avalonia.Thickness(offsetX, topBox + 20, 0, 0);
                if (shakeCount >= 10)
                {
                    shakeTimer.Stop();
                    RazorTextBox.Margin = new Avalonia.Thickness(_razorInputX, topBox, 0, 0);
                    RazorDurationHint.Margin = new Avalonia.Thickness(_razorInputX, topBox + 20, 0, 0);
                }
            };
            shakeTimer.Start();
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

            // Warn if output file already exists
            if (File.Exists(outputFilePath))
            {
                var dialog = new Window
                {
                    Title = "File already exported",
                    Width = 360,
                    Height = 150,
                    WindowStartupLocation = WindowStartupLocation.CenterOwner,
                    Background = new SolidColorBrush(Color.Parse("#222222")),
                    CanResize = false
                };

                bool confirmed = false;

                var yesBtn = new Button
                {
                    Content = "Yes, overwrite",
                    Background = new SolidColorBrush(Color.Parse("#e8a020")),
                    Foreground = Brushes.White,
                    Padding = new Avalonia.Thickness(16, 6),
                    FontWeight = Avalonia.Media.FontWeight.Bold
                };
                var noBtn = new Button
                {
                    Content = "No, cancel",
                    Background = new SolidColorBrush(Color.Parse("#333333")),
                    Foreground = new SolidColorBrush(Color.Parse("#cccccc")),
                    Padding = new Avalonia.Thickness(16, 6)
                };

                yesBtn.Click += (_, __) => { confirmed = true; dialog.Close(); };
                noBtn.Click += (_, __) => { confirmed = false; dialog.Close(); };

                dialog.Content = new StackPanel
                {
                    Margin = new Avalonia.Thickness(20),
                    Spacing = 16,
                    Children =
        {
            new TextBlock
            {
                Text       = "This clip has already been exported.\nAre you sure you want to overwrite it?",
                Foreground = new SolidColorBrush(Color.Parse("#cccccc")),
                FontSize   = 13,
                TextWrapping = Avalonia.Media.TextWrapping.Wrap
            },
            new StackPanel
            {
                Orientation = Avalonia.Layout.Orientation.Horizontal,
                Spacing     = 10,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                Children    = { noBtn, yesBtn }
            }
        }
                };

                await dialog.ShowDialog(this);

                if (!confirmed)
                {
                    BtnExport.IsEnabled = true;
                    return;
                }
            }

            _mediaPlayer?.SetPause(true);
            BtnExport.IsEnabled = false;

            try
            {
                if (_segments.Count == 1)
                {
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

                    string listFile = System.IO.Path.Combine(tempDir, "concat.txt");
                    File.WriteAllLines(listFile, tempFiles.Select(f => $"file '{f}'"));

                    StatusText.Text = "Joining segments...";
                    string concatArgs = $"-y -f concat -safe 0 -i \"{listFile}\" -c copy \"{outputFilePath}\"";
                    await FFmpeg.Conversions.New().Start(concatArgs);
                    Directory.Delete(tempDir, true);
                }

                _exportedFiles.Add(_currentFilePath);
                SaveExportedFiles();
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
                if (shakeCount >= 10)
                {
                    shakeTimer.Stop();
                    BtnExport.Margin = new Avalonia.Thickness(0);
                }
            };
            shakeTimer.Start();
        }

        private string GetOutputRoot()
        {
            if (_customOutputPath != null && Directory.Exists(_customOutputPath))
            {
                string rootNameCustom = System.IO.Path.GetFileName(
                    _rootFolderPath!.TrimEnd(System.IO.Path.DirectorySeparatorChar));
                return System.IO.Path.Combine(_customOutputPath, $"SevenCuts_{rootNameCustom}");
            }
            string cleanRoot = _rootFolderPath!.TrimEnd(System.IO.Path.DirectorySeparatorChar);
            string rootParent = System.IO.Path.GetDirectoryName(cleanRoot)!;
            string rootName = System.IO.Path.GetFileName(cleanRoot);
            return System.IO.Path.Combine(rootParent, $"SevenCuts_{rootName}");
        }

        private void SaveExportedFiles()
        {
            if (_rootFolderPath == null) return;
            string outputRoot = GetOutputRoot();
            Directory.CreateDirectory(outputRoot);
            string jsonPath = System.IO.Path.Combine(outputRoot, "exported.json");
            File.WriteAllText(jsonPath,
                System.Text.Json.JsonSerializer.Serialize(_exportedFiles.ToList()));
        }

        private void LoadExportedFiles()
        {
            if (_rootFolderPath == null) return;
            string outputRoot = GetOutputRoot();
            string jsonPath = System.IO.Path.Combine(outputRoot, "exported.json");
            if (!File.Exists(jsonPath)) return;
            try
            {
                var list = System.Text.Json.JsonSerializer.Deserialize<List<string>>(
                    File.ReadAllText(jsonPath));
                if (list != null)
                    foreach (var f in list)
                        _exportedFiles.Add(f);
            }
            catch { }
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