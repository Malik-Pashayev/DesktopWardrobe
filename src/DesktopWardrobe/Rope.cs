using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;

namespace DesktopWardrobe;

/// <summary>
/// A twine clothesline simulated as a chain of points (Verlet integration with distance constraints).
/// Pegged items make their point heavier, so the line dips under each one and bounces when something lands.
/// </summary>
internal sealed class Rope : FrameworkElement
{
    public const double AnchorInset = 26;

    private const int Segments = 32;
    private const double Gravity = 1100;        // px/s²
    private const double Slack = 1.0012;        // rope length relative to the gap between the nails
    private const double LoadMass = 2.5;        // a pegged item weighs this many rope points
    // Verlet integration is only stable with a constant time step; feeding it the uneven frame
    // times of a UI timer pumps energy in and the line never comes to rest.
    private const double StepTime = 1.0 / 120;
    private const int MaxStepsPerFrame = 12;
    private const double VelocityRetention = 0.985; // per step; lower = settles faster
    private const int Iterations = 40;          // each one is a forward and a backward pass
    // The solver never fully converges, so a resting line still creeps by a hair;
    // anything slower than this counts as still (and freezing it there is invisible).
    private const double SleepSpeed = 5;        // px/s
    private const double TimeToSleep = 0.4;     // still this long: stop simulating until something touches it

    private static readonly Pen BasePen = RoundPen(Color.FromRgb(0x7E, 0x5D, 0x35), 4.4);
    private static readonly Pen BodyPen = RoundPen(Color.FromRgb(0xC2, 0x9B, 0x62), 3.0);
    private static readonly Pen SheenPen = RoundPen(Color.FromArgb(170, 0xF0, 0xDB, 0xB0), 1.0);
    private static readonly Pen TwistPen = Frozen(new Pen(new SolidColorBrush(Color.FromArgb(150, 0x5E, 0x44, 0x24)), 4.4)
    {
        DashStyle = new DashStyle(new[] { 0.45, 1.2 }, 0), // fewer, longer bands: much cheaper to draw
        DashCap = PenLineCap.Flat,
    });
    private static readonly Pen ShadowPen = RoundPen(Color.FromArgb(55, 0, 0, 0), 4.5);
    private static readonly Pen GlowPen = RoundPen(Color.FromArgb(90, 0x7A, 0xA2, 0xF7), 12);
    private static readonly Brush ScrewBrush = Frozen(new RadialGradientBrush(Color.FromRgb(0xF4, 0xF5, 0xF7), Color.FromRgb(0x5E, 0x62, 0x6B)) { GradientOrigin = new Point(0.35, 0.3) });
    private static readonly Pen ScrewSlot = RoundPen(Color.FromRgb(0x4A, 0x4D, 0x55), 1.2);
    private static readonly Brush KnotBrush = Frozen(new SolidColorBrush(Color.FromRgb(0xA8, 0x82, 0x4C)));

    private Point[] _pos = Array.Empty<Point>();
    private Point[] _prev = Array.Empty<Point>();
    private Vector[] _velocity = Array.Empty<Vector>();
    private double[] _inverseMass = Array.Empty<double>();
    private double _restLength;
    private double _width = -1;
    private bool _highlight;
    private bool _sleeping;
    private double _stillTime;
    private double _accumulator;

    /// <summary>Height of the two nails the line is tied to.</summary>
    public double AnchorY { get; }

    public Rope(double anchorY)
    {
        AnchorY = anchorY;
        IsHitTestVisible = false;
    }

    public bool Highlight
    {
        get => _highlight;
        set
        {
            if (value == _highlight)
                return;
            _highlight = value;
            InvalidateVisual();
        }
    }

    /// <summary>(Re)builds the chain between the two nails when the screen width changes.</summary>
    public void EnsureWidth(double width)
    {
        if (Math.Abs(width - _width) < 0.5)
            return;

        _width = width;
        int count = Segments + 1;
        _pos = new Point[count];
        _prev = new Point[count];
        _velocity = new Vector[count];
        _inverseMass = new double[count];

        double left = AnchorInset, span = width - 2 * AnchorInset;
        for (int i = 0; i < count; i++)
        {
            double u = 2.0 * i / Segments - 1;
            _pos[i] = _prev[i] = new Point(left + span * i / Segments, AnchorY + 30 * (1 - u * u));
            _inverseMass[i] = 1;
        }
        _inverseMass[0] = _inverseMass[Segments] = 0; // nailed down
        _restLength = span / Segments * Slack;
        Wake();
    }

    /// <summary>A sleeping line costs nothing: no simulation and no redraws until something disturbs it.</summary>
    private void Wake()
    {
        _sleeping = false;
        _stillTime = 0;
    }

    /// <summary>Position along the line as a fractional point index, so items can sit between points.</summary>
    public double IndexAt(double x)
    {
        double span = Math.Max(1, _width - 2 * AnchorInset);
        return Math.Clamp((x - AnchorInset) / span * Segments, 0.5, Segments - 0.5);
    }

    public Point PointAt(double t)
    {
        var (i, f) = Split(t);
        return _pos[i] + (_pos[i + 1] - _pos[i]) * f;
    }

    public Vector VelocityAt(double t)
    {
        var (i, f) = Split(t);
        return _velocity[i] * (1 - f) + _velocity[i + 1] * f;
    }

    /// <summary>Angle of the line at <paramref name="t"/>, in degrees.</summary>
    public double SlopeAt(double t)
    {
        var (i, _) = Split(t);
        Vector d = _pos[i + 1] - _pos[i];
        return Math.Atan2(d.Y, d.X) * 180 / Math.PI;
    }

    /// <summary>Hangs each item's weight on the two points either side of its peg.</summary>
    public void SetLoads(IEnumerable<double> positions)
    {
        var mass = new double[Segments + 1];
        Array.Fill(mass, 1);
        foreach (double t in positions)
        {
            var (i, f) = Split(t);
            mass[i] += LoadMass * (1 - f);
            mass[i + 1] += LoadMass * f;
        }
        for (int i = 1; i < Segments; i++)
        {
            double inverse = 1 / mass[i];
            if (Math.Abs(inverse - _inverseMass[i]) > 1e-9)
            {
                _inverseMass[i] = inverse;
                Wake();
            }
        }
    }

    /// <summary>Adds velocity (px/s) to the line at <paramref name="t"/>, e.g. when the mouse plucks it.</summary>
    public void Nudge(double t, Vector velocity, double dt)
    {
        var (i, f) = Split(t);
        double scale = dt / StepTime; // a push spread over this frame
        if (_inverseMass[i] > 0)
            _prev[i] -= velocity * ((1 - f) * StepTime * Math.Min(scale, 1));
        if (_inverseMass[i + 1] > 0)
            _prev[i + 1] -= velocity * (f * StepTime * Math.Min(scale, 1));
        Wake();
    }

    private static (int Index, double Fraction) Split(double t)
    {
        int i = Math.Clamp((int)Math.Floor(t), 0, Segments - 1);
        return (i, t - i);
    }

    /// <summary>Adds velocity (px/s) to the whole line.</summary>
    public void Kick(Vector velocity)
    {
        for (int i = 1; i < Segments; i++)
            _prev[i] -= velocity * StepTime;
        Wake();
    }

    /// <summary>Lets the line come to rest off-screen, so it appears already hanging still.</summary>
    public void Settle(double seconds = 4)
    {
        if (_pos.Length == 0)
            return;
        for (int n = 0; n < seconds / StepTime; n++)
            Integrate();
        GoToSleep();
        InvalidateVisual();
    }

    /// <summary>Advances the simulation by <paramref name="dt"/> seconds of real time, in fixed steps.</summary>
    public void Step(double dt)
    {
        if (_pos.Length == 0 || dt <= 0 || _sleeping)
            return;

        _accumulator = Math.Min(_accumulator + dt, MaxStepsPerFrame * StepTime);
        var before = (Point[])_pos.Clone();
        int steps = 0;
        for (; _accumulator >= StepTime; _accumulator -= StepTime)
        {
            Integrate();
            steps++;
        }
        if (steps == 0)
            return;

        double elapsed = steps * StepTime, fastest = 0;
        for (int i = 0; i <= Segments; i++)
        {
            _velocity[i] = (_pos[i] - before[i]) / elapsed;
            fastest = Math.Max(fastest, _velocity[i].Length);
        }
        InvalidateVisual();

        _stillTime = fastest > SleepSpeed ? 0 : _stillTime + elapsed;
        if (_stillTime >= TimeToSleep)
            GoToSleep();
    }

    private void GoToSleep()
    {
        _sleeping = true;
        _accumulator = 0;
        Array.Copy(_pos, _prev, _pos.Length);
        Array.Clear(_velocity);
    }

    private void Integrate()
    {
        for (int i = 1; i < Segments; i++)
        {
            Point p = _pos[i];
            Vector v = (p - _prev[i]) * VelocityRetention;
            _prev[i] = p;
            _pos[i] = p + v + new Vector(0, Gravity * StepTime * StepTime);
        }

        // Alternating sweep directions spreads the corrections evenly, so the line doesn't stretch.
        for (int it = 0; it < Iterations; it++)
        {
            for (int i = 0; i < Segments; i++)
                SatisfyLength(i);
            for (int i = Segments - 1; i >= 0; i--)
                SatisfyLength(i);
        }
    }

    private void SatisfyLength(int i)
    {
        double wa = _inverseMass[i], wb = _inverseMass[i + 1];
        if (wa + wb == 0)
            return;
        Vector d = _pos[i + 1] - _pos[i];
        double length = d.Length;
        if (length < 1e-6)
            return;
        Vector correction = d * ((length - _restLength) / (length * (wa + wb)));
        _pos[i] += correction * wa;
        _pos[i + 1] -= correction * wb;
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (_pos.Length == 0)
            return;

        var curve = SmoothCurve(_pos);
        if (_highlight)
            dc.DrawGeometry(null, GlowPen, curve);

        // A plain offset stroke is a far cheaper shadow than a blur effect on a transparent window.
        dc.PushTransform(new TranslateTransform(0, 4));
        dc.DrawGeometry(null, ShadowPen, curve);
        dc.Pop();

        // Layered strokes fake a round, twisted cord: dark edge, body, light sheen on top, twist bands.
        dc.DrawGeometry(null, BasePen, curve);
        dc.DrawGeometry(null, BodyPen, curve);
        dc.PushTransform(new TranslateTransform(0, -0.8));
        dc.DrawGeometry(null, SheenPen, curve);
        dc.Pop();
        dc.DrawGeometry(null, TwistPen, curve);

        DrawScrewHook(dc, _pos[0]);
        DrawScrewHook(dc, _pos[Segments]);
    }

    private static void DrawScrewHook(DrawingContext dc, Point at)
    {
        dc.DrawEllipse(KnotBrush, BasePen, at + new Vector(0, 3), 5.5, 4);
        dc.DrawEllipse(ScrewBrush, null, at, 6.5, 6.5);
        dc.DrawLine(ScrewSlot, at + new Vector(-3.5, -2), at + new Vector(3.5, 2));
    }

    /// <summary>Catmull-Rom spline through the chain points so the line has no visible corners.</summary>
    private static StreamGeometry SmoothCurve(Point[] p)
    {
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(p[0], false, false);
            for (int i = 0; i < p.Length - 1; i++)
            {
                Point p0 = p[Math.Max(i - 1, 0)], p1 = p[i], p2 = p[i + 1], p3 = p[Math.Min(i + 2, p.Length - 1)];
                ctx.BezierTo(p1 + (p2 - p0) / 6, p2 - (p3 - p1) / 6, p2, true, true);
            }
        }
        geometry.Freeze();
        return geometry;
    }

    private static Pen RoundPen(Color color, double thickness) =>
        Frozen(new Pen(new SolidColorBrush(color), thickness)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round,
        });

    private static T Frozen<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}
