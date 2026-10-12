using System;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace DesktopWardrobe;

/// <summary>A file or folder pegged to the rope with a wooden clothespin. Swings like a damped pendulum around the peg.</summary>
internal sealed class ClothespinItem : FrameworkElement
{
    public const double VisualWidth = 112;
    public const double VisualHeight = 138;
    public const double PinY = 30; // where the rope runs through the peg's hole, in local coordinates
    private const double Center = VisualWidth / 2;

    // A slow, heavy swing: ~1.5 s period and gentle damping, so it sways rather than flicks.
    private const double Stiffness = 22;
    private const double Damping = 2.6;         // settles within a couple of small swings
    private const double AnchorCoupling = 0.2;  // how much sideways jerks of the peg swing the item
    private const double MaxAngle = 14;
    private const double MaxSpin = 80;

    private static readonly Brush WoodBrush = Frozen(new LinearGradientBrush(
        new GradientStopCollection
        {
            new(Color.FromRgb(0xB8, 0x8A, 0x52), 0),
            new(Color.FromRgb(0xEE, 0xD3, 0xA4), 0.35),
            new(Color.FromRgb(0xE0, 0xBC, 0x84), 0.6),
            new(Color.FromRgb(0xA9, 0x7C, 0x46), 1),
        }, 0));
    private static readonly Brush EndGrainBrush = Frozen(new SolidColorBrush(Color.FromArgb(70, 0x6E, 0x48, 0x1E)));
    private static readonly Pen WoodEdge = Frozen(new Pen(new SolidColorBrush(Color.FromRgb(0x7C, 0x55, 0x2A)), 0.8) { LineJoin = PenLineJoin.Round });
    private static readonly Pen GrainPen = Frozen(new Pen(new SolidColorBrush(Color.FromArgb(55, 0x6E, 0x48, 0x1E)), 0.6));
    private static readonly Brush HoleBrush = Frozen(new RadialGradientBrush(Color.FromRgb(0x3A, 0x26, 0x10), Color.FromRgb(0x7A, 0x55, 0x2C)));
    private static readonly Pen SteelPen = Frozen(new Pen(new LinearGradientBrush(Color.FromRgb(0xF6, 0xF7, 0xF9), Color.FromRgb(0x6A, 0x6F, 0x78), 90), 1.4)
    {
        StartLineCap = PenLineCap.Round,
        EndLineCap = PenLineCap.Round,
    });
    private static readonly Pen SteelShadow = Frozen(new Pen(new SolidColorBrush(Color.FromArgb(90, 0x2A, 0x1C, 0x0C)), 1.4));
    private static readonly Brush TagBrush = Frozen(new LinearGradientBrush(Color.FromRgb(0xFB, 0xF5, 0xE6), Color.FromRgb(0xEE, 0xE3, 0xCA), 90));
    private static readonly Pen TagEdge = Frozen(new Pen(new SolidColorBrush(Color.FromArgb(60, 0x5A, 0x44, 0x22)), 0.8));
    private static readonly Brush TagText = Frozen(new SolidColorBrush(Color.FromRgb(0x3B, 0x2F, 0x22)));
    private static readonly Brush TagShadowBrush = Frozen(new SolidColorBrush(Color.FromArgb(45, 0, 0, 0)));
    private static readonly Brush FolderBrush = Frozen(new SolidColorBrush(Color.FromRgb(0xF4, 0xC4, 0x30)));

    // One prong seen from the side: a long handle on top that narrows into a jaw at the bottom,
    // with a half-round cut on the inside that (with its twin) forms the hole the rope runs through.
    private static readonly Geometry Prong = Frozen(Geometry.Parse(string.Create(CultureInfo.InvariantCulture,
        $"M {Center - 0.9},1 L {Center - 6.2},1 Q {Center - 8.4},1 {Center - 8.2},3.5 " +
        $"L {Center - 7.4},20 Q {Center - 7.2},22.5 {Center - 6.4},24 " +
        $"L {Center - 4.2},44 Q {Center - 3.8},46.5 {Center - 2},46.5 L {Center - 0.6},46.5 " +
        $"L {Center - 0.6},{PinY + 3.2} A 3.2,3.2 0 0 1 {Center - 0.6},{PinY - 3.2} Z")));

    private readonly RotateTransform _rotation = new(0, Center, PinY);
    private readonly ScaleTransform _scale = new(1, 1, Center, PinY);
    private ImageSource? _icon;
    private ImageSource? _shadow;
    private readonly string _name;
    private double _tilt;

    public string ItemPath { get; }

    // Layout state, owned by the rack.
    public int Row { get; set; }
    public double TargetX { get; set; }
    public double RopeX { get; set; } = double.NaN;
    public double YOffset { get; set; }      // eases to 0 after jumping between ropes
    public double AnchorVx { get; set; }

    public double Angle { get; private set; }
    public double Omega { get; private set; }

    public ClothespinItem(string path)
    {
        ItemPath = path;
        _name = System.IO.Path.GetFileName(path);
        ShellIcons.LoadAsync(path, 96, (icon, shadow) =>
        {
            _icon = icon;
            _shadow = shadow;
            InvalidateVisual();
        });
        Width = VisualWidth;
        Height = VisualHeight;
        RenderTransform = new TransformGroup { Children = { _scale, _rotation } };
        Cursor = Cursors.Hand;
        ToolTip = _name;
    }

    /// <summary>Lean that follows the slope of the rope at the peg.</summary>
    public double Tilt
    {
        set
        {
            _tilt = value;
            _rotation.Angle = Angle + _tilt;
        }
    }

    public void Push(double angularVelocity) =>
        Omega = Math.Clamp(Omega + angularVelocity, -MaxSpin, MaxSpin);

    /// <param name="anchorAccelerationX">Sideways acceleration of whatever holds the peg (px/s²).</param>
    public void Step(double dt, double anchorAccelerationX)
    {
        double drive = Math.Clamp(anchorAccelerationX, -2000, 2000) * AnchorCoupling;
        if (Angle == 0 && Omega == 0 && Math.Abs(drive) < 1)
            return;

        Omega = Math.Clamp(Omega + (-Stiffness * Angle - Damping * Omega - drive) * dt, -MaxSpin, MaxSpin);
        Angle = Math.Clamp(Angle + Omega * dt, -MaxAngle, MaxAngle);
        if (Math.Abs(Angle) < 0.15 && Math.Abs(Omega) < 1.5) // too small to see: stop, so nothing redraws
            Angle = Omega = 0;

        _rotation.Angle = Angle + _tilt;
    }

    /// <summary>Picked up by the mouse: a little bigger, as if lifted towards you.</summary>
    public bool Lifted
    {
        set => AnimateScale(value ? 1.1 : IsMouseOver ? 1.06 : 1);
    }

    protected override void OnMouseEnter(MouseEventArgs e) => AnimateScale(1.06);

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        if (!IsMouseCaptured)
            AnimateScale(1);
    }

    private void AnimateScale(double to)
    {
        var animation = new DoubleAnimation(to, TimeSpan.FromSeconds(0.25)) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } };
        _scale.BeginAnimation(ScaleTransform.ScaleXProperty, animation);
        _scale.BeginAnimation(ScaleTransform.ScaleYProperty, animation);
    }

    protected override void OnRender(DrawingContext dc)
    {
        // Invisible hit area so the whole item is clickable, not just the opaque pixels.
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(8, 0, VisualWidth - 16, VisualHeight));

        // The jaws bite the top edge of the icon.
        var iconRect = new Rect(Center - 32, 33, 64, 64); // shell icons have empty space on top
        if (_icon != null)
        {
            if (_shadow != null)
                dc.DrawImage(_shadow, new Rect(iconRect.X + 2, iconRect.Y + 4, 64, 64));
            dc.DrawImage(_icon, iconRect);
        }
        else
            dc.DrawRoundedRectangle(FolderBrush, null, new Rect(iconRect.X + 4, iconRect.Y + 12, 56, 42), 5, 5);

        DrawClothespin(dc);
        DrawTag(dc);
    }

    private static void DrawClothespin(DrawingContext dc)
    {
        // The rope hole sits behind the two prongs.
        dc.DrawEllipse(HoleBrush, null, new Point(Center, PinY), 3.4, 3.4);

        DrawProng(dc);
        dc.PushTransform(new ScaleTransform(-1, 1, Center, 0));
        DrawProng(dc);
        dc.Pop();

        // Steel spring: coils wrapped round both prongs, with its legs pressing on the handles.
        for (int i = 0; i < 3; i++)
        {
            double y = 15 + i * 2.6;
            dc.DrawLine(SteelShadow, new Point(Center - 8.6, y + 0.9), new Point(Center + 8.6, y + 0.9));
            dc.DrawLine(SteelPen, new Point(Center - 8.6, y), new Point(Center + 8.6, y));
        }
        dc.DrawLine(SteelPen, new Point(Center - 8.4, 15), new Point(Center - 7.6, 6));
        dc.DrawLine(SteelPen, new Point(Center - 7.6, 6), new Point(Center - 3.2, 5));
        dc.DrawLine(SteelPen, new Point(Center + 8.4, 15), new Point(Center + 7.6, 6));
        dc.DrawLine(SteelPen, new Point(Center + 7.6, 6), new Point(Center + 3.2, 5));
    }

    private static void DrawProng(DrawingContext dc)
    {
        dc.DrawGeometry(WoodBrush, WoodEdge, Prong);
        dc.DrawRectangle(EndGrainBrush, null, new Rect(Center - 7.8, 1.2, 6.8, 1.6)); // end grain on top
        dc.DrawLine(GrainPen, new Point(Center - 5.6, 4), new Point(Center - 4.6, 40));
        dc.DrawLine(GrainPen, new Point(Center - 3.4, 3), new Point(Center - 2.6, 26));
    }

    private void DrawTag(DrawingContext dc)
    {
        var label = new FormattedText(_name, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal),
            11.5, TagText, VisualTreeHelper.GetDpi(this).PixelsPerDip)
        {
            MaxTextWidth = VisualWidth - 20,
            MaxLineCount = 2,
            Trimming = TextTrimming.CharacterEllipsis,
            TextAlignment = TextAlignment.Center,
        };
        double tagWidth = Math.Min(label.WidthIncludingTrailingWhitespace, VisualWidth - 20) + 14;
        var tag = new Rect(Center - tagWidth / 2, 106, tagWidth, label.Height + 7);
        dc.DrawRoundedRectangle(TagShadowBrush, null, new Rect(tag.X + 1, tag.Y + 2.5, tag.Width, tag.Height), 4, 4);
        dc.DrawRoundedRectangle(TagBrush, TagEdge, tag, 4, 4);
        dc.DrawText(label, new Point(10, tag.Y + 3.5));
    }

    private static T Frozen<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}
