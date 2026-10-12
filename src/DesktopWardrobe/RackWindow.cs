using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;

namespace DesktopWardrobe;

/// <summary>
/// A transparent, always-on-top strip across the top of the primary screen holding one or more
/// clotheslines. The lines drop down when the mouse rests on the top edge (or something is dragged
/// there) and pull back up when the mouse leaves. Items can be dragged along and between lines to
/// reorder them, or dragged off the bottom to put them back on the desktop.
/// </summary>
internal sealed class RackWindow : Window
{
    private const double FirstRopeY = 46;
    private const double RowHeight = 182;
    private const double RowBottom = 190;    // room below the last rope for sag plus the hanging items
    private const double SideMargin = 70;
    private const double MinSpacing = 110;   // a line holds as many items as fit at this spacing
    private const double MaxSpacing = 125;
    private const double ShowDelay = 0.3;    // seconds the mouse must rest on the edge
    private const double HideDelay = 0.4;

    private const double BrushStrength = 0.25;   // how much a passing mouse swings an item
    private const double PluckStrength = 0.05;   // how much a passing mouse dips a rope
    private const double MinMouseSpeed = 40;     // px/s; slower than this doesn't disturb anything
    private const double CursorSmoothing = 0.07; // seconds; evens out jittery mouse speed
    private const double SlideTime = 0.09;       // seconds for items to glide into new places
    private const double GlideSwing = 0.15;      // how much gliding into a new place swings an item
    private const double DragThreshold = 6;

    private readonly Wardrobe _wardrobe;
    private readonly Grid _root;
    private readonly List<Rope> _ropes = new();
    private readonly TextBlock _hint;
    private readonly Canvas _itemLayer = new();
    private readonly TranslateTransform _slide = new();
    private readonly Dictionary<string, ClothespinItem> _items = new(StringComparer.OrdinalIgnoreCase);
    private readonly DispatcherTimer _timer = new(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(15) };
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly Random _random = new();

    private bool _shown, _draggingOut, _menuOpen;
    private double _edgeSince = -1, _awaySince = -1, _lastFrame;
    private Point _lastCursor;
    private double _cursorVx, _cursorAx;

    private ClothespinItem? _pressed;
    private Point _pressPoint;
    private ClothespinItem? _lifted;   // item being moved along the lines
    private Vector _grabOffset;        // from the cursor to the lifted item's peg
    private int _insertAt;

    public RackWindow(Wardrobe wardrobe)
    {
        _wardrobe = wardrobe;

        Title = "Desktop Wardrobe";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent; // fully transparent pixels let clicks through to the windows below
        ResizeMode = ResizeMode.NoResize;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        AllowDrop = true;
        Left = 0;
        Top = 0;
        Width = SystemParameters.PrimaryScreenWidth;
        Height = FirstRopeY + RowBottom;
        _slide.Y = -Height;

        // Looks fully transparent, but alpha 1 still makes Windows deliver drops and clicks to us while the lines are down.
        var dropZone = new Border { Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)) };

        _hint = new TextBlock
        {
            Text = "Drag folders up here to hang them",
            Foreground = Brushes.White,
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, FirstRopeY + 70, 0, 0),
            IsHitTestVisible = false,
            Effect = new DropShadowEffect { BlurRadius = 8, ShadowDepth = 0, Opacity = 1 },
        };

        _root = new Grid { RenderTransform = _slide };
        _root.Children.Add(dropZone);
        _root.Children.Add(_hint);
        _root.Children.Add(_itemLayer);
        Content = _root;

        SourceInitialized += (_, _) => HideFromAltTab();
        _wardrobe.Changed += Rebuild;
        Loaded += (_, _) => Rebuild();

        _timer.Tick += OnFrame;
        _timer.Start();
    }

    private void HideFromAltTab()
    {
        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        long exStyle = Native.GetWindowLongPtr(hwnd, Native.GWL_EXSTYLE).ToInt64();
        Native.SetWindowLongPtr(hwnd, Native.GWL_EXSTYLE, new IntPtr(exStyle | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE));
    }

    // ---- Items and lines -----------------------------------------------------------------

    private void Rebuild()
    {
        var paths = _wardrobe.Items;
        foreach (string gone in _items.Keys.Except(paths, StringComparer.OrdinalIgnoreCase).ToList())
        {
            if (_lifted == _items[gone])
                DropLifted(commit: false);
            _itemLayer.Children.Remove(_items[gone]);
            _items.Remove(gone);
        }

        foreach (string path in paths)
        {
            if (_items.ContainsKey(path))
                continue;

            var item = CreateItem(path);
            _items[path] = item;
            _itemLayer.Children.Add(item);
            if (_shown)
                item.Push((_random.Next(2) == 0 ? -1 : 1) * 35); // freshly pegged: a gentle swing; the rope dips by itself
        }

        _hint.Visibility = paths.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        LayoutItems();
    }

    private int Capacity(int count)
    {
        int perLine = Math.Max(1, (int)((Width - 2 * SideMargin) / MinSpacing));
        int maxLines = Math.Max(1, (int)((SystemParameters.PrimaryScreenHeight * 0.75 - FirstRopeY - RowBottom) / RowHeight) + 1);
        return Math.Max(perLine, (int)Math.Ceiling(count / (double)maxLines)); // squeeze rather than run off screen
    }

    /// <summary>Where each of <paramref name="count"/> places is: line number and x, filling lines top to bottom.</summary>
    private List<(int Row, double X)> Slots(int count)
    {
        var slots = new List<(int, double)>(count);
        int capacity = Capacity(count);
        double usable = Width - 2 * SideMargin;
        for (int row = 0; row * capacity < count; row++)
        {
            int inRow = Math.Min(capacity, count - row * capacity);
            double spacing = Math.Min(MaxSpacing, usable / inRow);
            double first = Width / 2 - spacing * (inRow - 1) / 2;
            for (int i = 0; i < inRow; i++)
                slots.Add((row, first + i * spacing));
        }
        return slots;
    }

    /// <summary>Gives every item a place. While an item is lifted, its future place is left empty.</summary>
    private void LayoutItems()
    {
        var paths = _wardrobe.Items;
        var slots = Slots(paths.Count);
        EnsureRopes(slots.Count == 0 ? 1 : slots[^1].Row + 1);

        int slot = 0;
        foreach (string path in paths)
        {
            var item = _items[path];
            if (item == _lifted)
                continue;
            if (_lifted != null && slot == _insertAt)
                slot++;

            var (row, x) = slots[slot++];
            if (item.Row != row && !double.IsNaN(item.RopeX) && item.Row < _ropes.Count)
                item.YOffset += RopeY(item.Row, item.RopeX) - RopeY(row, item.RopeX);
            item.Row = row;
            item.TargetX = x;
            if (double.IsNaN(item.RopeX))
                item.RopeX = x;
        }
        FollowRopes();
        SettleHiddenRopes();
    }

    private double RopeY(int row, double x) => _ropes[row].PointAt(_ropes[row].IndexAt(x)).Y;

    private void EnsureRopes(int count)
    {
        while (_ropes.Count < count)
        {
            var rope = new Rope(FirstRopeY + _ropes.Count * RowHeight);
            rope.EnsureWidth(Width);
            _ropes.Add(rope);
            _root.Children.Insert(_ropes.Count, rope); // above the drop zone, below the hint and items
        }
        while (_ropes.Count > count && _ropes.Count > 1)
        {
            _root.Children.Remove(_ropes[^1]);
            _ropes.RemoveAt(_ropes.Count - 1);
        }

        Height = FirstRopeY + (_ropes.Count - 1) * RowHeight + RowBottom;
        if (!_shown)
        {
            _slide.BeginAnimation(TranslateTransform.YProperty, null);
            _slide.Y = -Height;
        }
    }

    private void FollowRopes()
    {
        for (int row = 0; row < _ropes.Count; row++)
        {
            var rope = _ropes[row];
            rope.SetLoads(_items.Values.Where(i => i != _lifted && i.Row == row).Select(i => rope.IndexAt(i.RopeX)));
        }

        foreach (var item in _items.Values)
        {
            if (item == _lifted)
                continue;
            var rope = _ropes[item.Row];
            double t = rope.IndexAt(item.RopeX);
            Point peg = rope.PointAt(t);
            Canvas.SetLeft(item, peg.X - ClothespinItem.VisualWidth / 2);
            Canvas.SetTop(item, peg.Y + item.YOffset - ClothespinItem.PinY);
            item.Tilt = rope.SlopeAt(t) * 0.5; // a pegged item leans a little with the line
        }
    }

    /// <summary>While the lines are up out of sight, changes in weight are settled instantly.</summary>
    private void SettleHiddenRopes()
    {
        if (_shown)
            return;
        foreach (var rope in _ropes)
            rope.Settle();
        FollowRopes();
    }

    private ClothespinItem CreateItem(string path)
    {
        var item = new ClothespinItem(path);

        item.MouseLeftButtonDown += (_, e) =>
        {
            _pressed = item;
            _pressPoint = e.GetPosition(this);
            item.CaptureMouse();
            e.Handled = true;
        };
        item.MouseMove += (_, e) =>
        {
            if (_pressed == item && _lifted == null && e.LeftButton == MouseButtonState.Pressed &&
                (e.GetPosition(this) - _pressPoint).Length > DragThreshold)
            {
                Lift(item);
            }
        };
        item.MouseLeftButtonUp += (_, e) =>
        {
            if (_pressed != item)
                return;
            _pressed = null;
            if (_lifted == item)
            {
                DropLifted(commit: true);
            }
            else
            {
                item.ReleaseMouseCapture();
                item.Push(15);
                Shell.Open(item.ItemPath);
            }
            e.Handled = true;
        };
        item.LostMouseCapture += (_, _) =>
        {
            if (_lifted == item && !_draggingOut)
                DropLifted(commit: true);
            _pressed = null;
        };

        var menu = new ContextMenu();
        menu.Items.Add(MenuItem("Open", () => Shell.Open(item.ItemPath)));
        menu.Items.Add(MenuItem("Put back on the desktop", () => _wardrobe.ReturnToDesktop(item.ItemPath)));
        menu.Items.Add(MenuItem("Copy (paste it into an upload)", () =>
            Clipboard.SetFileDropList(new System.Collections.Specialized.StringCollection { item.ItemPath })));
        menu.Items.Add(MenuItem("Show in Explorer", () => Shell.ShowInExplorer(item.ItemPath)));
        menu.Opened += (_, _) => _menuOpen = true;
        menu.Closed += (_, _) => _menuOpen = false;
        item.ContextMenu = menu;

        return item;
    }

    private static MenuItem MenuItem(string header, Action action)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => action();
        return item;
    }

    // ---- Moving items around -------------------------------------------------------------

    /// <summary>Unpegs an item so it hangs from the mouse and can be moved to another place.</summary>
    private void Lift(ClothespinItem item)
    {
        _lifted = item;
        _grabOffset = new Point(Canvas.GetLeft(item) + ClothespinItem.VisualWidth / 2, Canvas.GetTop(item) + ClothespinItem.PinY) - _pressPoint;
        _insertAt = _wardrobe.Items.ToList().FindIndex(p => string.Equals(p, item.ItemPath, StringComparison.OrdinalIgnoreCase));
        Panel.SetZIndex(item, 1000);
        item.Lifted = true;
        LayoutItems();
    }

    /// <summary>Pegs the lifted item back on: in its new place if <paramref name="commit"/>, else where it was.</summary>
    private void DropLifted(bool commit)
    {
        var item = _lifted;
        if (item == null)
            return;

        _lifted = null;
        Panel.SetZIndex(item, 0);
        item.Lifted = false;
        if (item.IsMouseCaptured)
            item.ReleaseMouseCapture();

        // Glide from wherever it was let go into its place on the line.
        item.RopeX = Canvas.GetLeft(item) + ClothespinItem.VisualWidth / 2;
        double hangingY = Canvas.GetTop(item) + ClothespinItem.PinY;

        if (commit)
            _wardrobe.MoveTo(item.ItemPath, _insertAt);
        LayoutItems();

        item.YOffset = hangingY - RopeY(item.Row, item.RopeX);
        item.Push(-_cursorVx * 0.02);
    }

    /// <summary>Picks the free place nearest the lifted item's peg.</summary>
    private void UpdateInsertion(Point peg)
    {
        var slots = Slots(_wardrobe.Items.Count);
        int best = 0;
        double bestDistance = double.MaxValue;
        for (int i = 0; i < slots.Count; i++)
        {
            var (row, x) = slots[i];
            double ropeY = row < _ropes.Count ? RopeY(row, x) : FirstRopeY + row * RowHeight + 40;
            double dx = peg.X - x, dy = (peg.Y - ropeY) * 1.5; // lines are far apart; favour the one you're on
            double distance = dx * dx + dy * dy;
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = i;
            }
        }

        if (best != _insertAt)
        {
            _insertAt = best;
            LayoutItems();
        }
    }

    // ---- Drag and drop with Explorer -----------------------------------------------------

    /// <summary>Hands the item to Explorer as a normal file drag, e.g. back onto the desktop.</summary>
    private void DragOut(ClothespinItem item)
    {
        _draggingOut = true;
        item.Opacity = 0.35;
        try
        {
            var data = new DataObject(DataFormats.FileDrop, new[] { item.ItemPath });
            data.SetData("Preferred DropEffect", new MemoryStream(BitConverter.GetBytes((int)DragDropEffects.Move)));
            // Copy is allowed too, so browsers and chat apps can take it as an upload; the item then stays on the line.
            DragDrop.DoDragDrop(item, data, DragDropEffects.Move | DragDropEffects.Copy);
        }
        finally
        {
            _draggingOut = false;
            item.Opacity = 1;
            _wardrobe.Refresh();
        }
    }

    protected override void OnDragEnter(DragEventArgs e) => OnDragOver(e);

    protected override void OnDragOver(DragEventArgs e)
    {
        bool accept = !_draggingOut && DroppedPaths(e) is { Length: > 0 } paths && paths.Any(_wardrobe.CanHang);
        e.Effects = accept ? DragDropEffects.Move : DragDropEffects.None;
        foreach (var rope in _ropes)
            rope.Highlight = accept;
        e.Handled = true;
    }

    protected override void OnDragLeave(DragEventArgs e)
    {
        foreach (var rope in _ropes)
            rope.Highlight = false;
    }

    protected override void OnDrop(DragEventArgs e)
    {
        OnDragLeave(e);
        // We move the items ourselves, so report "nothing happened" and the source never deletes anything.
        e.Effects = DragDropEffects.None;
        e.Handled = true;

        if (!_draggingOut && DroppedPaths(e) is { } paths)
            _wardrobe.Hang(paths.Where(_wardrobe.CanHang).ToList());
    }

    private static string[]? DroppedPaths(DragEventArgs e) =>
        e.Data.GetDataPresent(DataFormats.FileDrop) ? e.Data.GetData(DataFormats.FileDrop) as string[] : null;

    // ---- Animation loop ------------------------------------------------------------------

    private void OnFrame(object? sender, EventArgs e)
    {
        double now = _clock.Elapsed.TotalSeconds;
        double dt = Math.Min(now - _lastFrame, 0.05);
        _lastFrame = now;
        if (dt <= 0)
            return;

        Native.GetCursorPos(out var screenCursor);
        Point cursor = PointFromScreen(new Point(screenCursor.X, screenCursor.Y));
        UpdateVisibility(cursor, now);

        // Smoothed mouse speed and acceleration, so a jittery hand still gives a soft, even sway.
        double rawVx = Math.Clamp((cursor.X - _lastCursor.X) / dt, -3000, 3000);
        double smoothing = 1 - Math.Exp(-dt / CursorSmoothing);
        double previousVx = _cursorVx;
        _cursorVx += (rawVx - _cursorVx) * smoothing;
        _cursorAx += ((_cursorVx - previousVx) / dt - _cursorAx) * smoothing;
        _lastCursor = cursor;

        // Poll lazily while hidden; animate smoothly while the lines are down.
        _timer.Interval = TimeSpan.FromMilliseconds(_shown ? 20 : 40);
        if (!_shown)
            return;
        bool mouseMoving = Math.Abs(_cursorVx) > MinMouseSpeed;

        if (_lifted != null)
        {
            // Pulled below the lines: hand it over to Explorer so it can go back on the desktop.
            if (cursor.Y > Height + 10)
            {
                var item = _lifted;
                DropLifted(commit: false);
                DragOut(item);
                return;
            }
            UpdateInsertion(cursor + _grabOffset);
        }

        // Running the mouse across a line plucks it.
        foreach (var rope in _ropes)
        {
            double t = rope.IndexAt(cursor.X);
            if (mouseMoving && Math.Abs(cursor.Y - rope.PointAt(t).Y) < 12)
                rope.Nudge(t, new Vector(0, Math.Min(Math.Abs(_cursorVx), 1500) * PluckStrength), dt);
        }

        double glide = 1 - Math.Exp(-dt / SlideTime);
        foreach (var item in _items.Values)
        {
            // Ease towards the target, then snap so nothing keeps creeping (and redrawing) forever.
            item.RopeX = Math.Abs(item.TargetX - item.RopeX) < 0.05 ? item.TargetX : item.RopeX + (item.TargetX - item.RopeX) * glide;
            item.YOffset = Math.Abs(item.YOffset) < 0.05 ? 0 : item.YOffset * (1 - glide);
        }

        foreach (var rope in _ropes)
            rope.Step(dt);
        FollowRopes();

        foreach (var item in _items.Values)
        {
            if (item == _lifted)
            {
                Point peg = cursor + _grabOffset;
                Canvas.SetLeft(item, peg.X - ClothespinItem.VisualWidth / 2);
                Canvas.SetTop(item, peg.Y - ClothespinItem.PinY);
                item.Tilt = 0;
                item.Step(dt, _cursorAx); // swings from your hand as you carry it
                continue;
            }

            var rope = _ropes[item.Row];
            double t = rope.IndexAt(item.RopeX);
            Point pin = rope.PointAt(t);
            if (_lifted == null && mouseMoving && Math.Abs(cursor.X - pin.X) < 38 && cursor.Y > pin.Y + 8 && cursor.Y < pin.Y + ClothespinItem.VisualHeight - ClothespinItem.PinY)
                item.Push(_cursorVx * BrushStrength * dt);

            // Sliding aside to make room only nudges the swing a little.
            double anchorVx = rope.VelocityAt(t).X + (item.TargetX - item.RopeX) * glide / dt * GlideSwing;
            item.Step(dt, (anchorVx - item.AnchorVx) / dt);
            item.AnchorVx = anchorVx;
        }
    }

    private void UpdateVisibility(Point cursor, double now)
    {
        bool overScreen = cursor.X >= 0 && cursor.X <= Width;

        if (!_shown)
        {
            bool atTopEdge = overScreen && cursor.Y >= -1 && cursor.Y <= 2;
            if (!atTopEdge)
            {
                _edgeSince = -1;
                return;
            }

            if (_edgeSince < 0)
                _edgeSince = now;

            // Dragging something up opens the lines immediately; a resting mouse waits a moment
            // so clicking browser tabs doesn't keep popping them open.
            if ((Native.IsLeftButtonDown() || now - _edgeSince >= ShowDelay) && !Native.IsForegroundFullscreen())
                ShowRack();
        }
        else
        {
            bool inside = overScreen && cursor.Y >= -1 && cursor.Y <= Height;
            if (inside || _draggingOut || _menuOpen || _pressed != null || _lifted != null)
            {
                _awaySince = -1;
                return;
            }

            if (_awaySince < 0)
                _awaySince = now;
            if (now - _awaySince >= HideDelay)
                HideRack();
        }
    }

    private void ShowRack()
    {
        _shown = true;
        _awaySince = -1;
        if (Math.Abs(Width - SystemParameters.PrimaryScreenWidth) > 0.5)
        {
            Width = SystemParameters.PrimaryScreenWidth;
            foreach (var rope in _ropes)
                rope.EnsureWidth(Width);
        }
        LayoutItems();

        Topmost = false; // re-assert so we land above other topmost windows
        Topmost = true;
        Slide(0, 0.45, new QuadraticEase { EasingMode = EasingMode.EaseOut });

        foreach (var rope in _ropes)
            rope.Kick(new Vector(0, 15)); // the lines settle with a soft bounce as they come down
        foreach (var item in _items.Values)
            item.Push(_random.Next(-8, 8));
    }

    private void HideRack()
    {
        _shown = false;
        _edgeSince = -1;
        Slide(-Height, 0.32, new SineEase { EasingMode = EasingMode.EaseIn });
    }

    private void Slide(double to, double seconds, IEasingFunction easing) =>
        _slide.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(to, TimeSpan.FromSeconds(seconds)) { EasingFunction = easing });
}
