using System.Diagnostics;
using System.Windows.Media;

namespace DynamicIsland.Infrastructure;

/// <summary>单个弹簧通道（欠阻尼二阶系统）。</summary>
internal sealed class SpringChannel
{
    public double Value;
    public double Velocity;
    public double Target;

    public double Stiffness { get; set; } = 520;
    public double Damping { get; set; } = 34;

    public bool IsSettled =>
        Math.Abs(Value - Target) < 0.15 && Math.Abs(Velocity) < 4;

    public void Advance(double dt)
    {
        // 半隐式欧拉，dt 已被钳制，足够稳定
        double force = (Target - Value) * Stiffness - Velocity * Damping;
        Velocity += force * dt;
        Value += Velocity * dt;
    }
}

/// <summary>
/// 用 CompositionTarget.Rendering 逐帧驱动的弹簧动画（宽 / 高 / 圆角三条通道）。
/// WPF 自带的 DoubleAnimation 只能做缓动曲线，没有速度继承，形态变化会显得"硬"；
/// 这里用真实弹簧积分，可随时打断并保留速度，接近 iOS 灵动岛的手感。
/// </summary>
internal sealed class IslandSpringEngine : IDisposable
{
    private readonly SpringChannel _width = new();
    private readonly SpringChannel _height = new();
    private readonly SpringChannel _radius = new();
    // 悬停/拖入时的轻微纵向回弹（-4px 起始，回 0），和尺寸弹簧独立
    private readonly SpringChannel _nudge = new() { Stiffness = 260, Damping = 22 };
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    private double _lastSeconds;
    private bool _running;
    private bool _primed;

    /// <summary>(width, height, cornerRadius, progress 0-1)</summary>
    public event Action<double, double, double>? Updated;

    public double Width => _width.Value;
    public double Height => _height.Value;
    public double Radius => _radius.Value;
    public double NudgeOffset => _nudge.Value;

    /// <summary>高度方向的当前速度（负值表示正在收缩），用于判断"收起途中被移回"。</summary>
    public double HeightVelocity => _height.Velocity;

    public bool IsSettled => _width.IsSettled && _height.IsSettled && _radius.IsSettled && _nudge.IsSettled;

    /// <summary>设置目标尺寸。repeat 为 true 表示回到同一形态时稍微"弹"一下。</summary>
    public void SetTarget(double width, double height, double radius)
    {
        // 扩张与收缩用不同的阻尼：展开时略欠阻尼（有轻微 overshoot），
        // 收起时更紧一些，避免整块面板"拖着尾巴"缩回去。
        // 用"旧目标 → 新目标"判断方向，而不是当前值：
        // 快速收起途中立刻移回时，当前值可能还停在展开尺寸，
        // 但旧目标已经是折叠尺寸，这时应该按扩张参数来算。
        bool expanding = width > _width.Target + 0.5 || height > _height.Target + 0.5;
        _width.Stiffness = expanding ? 355 : 300;
        _width.Damping = expanding ? 21 : 29;
        _height.Stiffness = _width.Stiffness;
        _height.Damping = _width.Damping;
        _radius.Stiffness = _width.Stiffness;
        _radius.Damping = _width.Damping;

        _width.Target = width;
        _height.Target = height;
        _radius.Target = radius;

        if (!_primed)
        {
            // 首帧直接落位，避免启动时从 0 弹出来
            _width.Value = width;
            _height.Value = height;
            _radius.Value = radius;
            _width.Velocity = _height.Velocity = _radius.Velocity = 0;
            _nudge.Value = 0;
            _nudge.Velocity = 0;
            _nudge.Target = 0;
            _primed = true;
            Updated?.Invoke(width, height, radius);
            return;
        }

        Start();
    }

    /// <summary>给一点初始速度，做出"被顶开"的手感。</summary>
    public void Kick(double velocity)
    {
        _width.Velocity += velocity;
        _height.Velocity += velocity * 0.35;
        Start();
    }

    /// <summary>纵向轻弹：先位移 offset（负值向上），再弹簧回 0。</summary>
    public void NudgeY(double offset)
    {
        _nudge.Value = offset;
        _nudge.Velocity = 0;
        _nudge.Target = 0;
        Start();
    }

    private void Start()
    {
        if (_running)
        {
            return;
        }

        _running = true;
        _lastSeconds = _clock.Elapsed.TotalSeconds;
        CompositionTarget.Rendering += OnRendering;
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        double now = _clock.Elapsed.TotalSeconds;
        double dt = now - _lastSeconds;
        _lastSeconds = now;

        // 掉帧时钳制步长，避免弹簧数值爆炸
        dt = Math.Clamp(dt, 0.001, 0.05);

        // 固定子步长积分，保证不同刷新率下表现一致
        int steps = Math.Max(1, (int)Math.Ceiling(dt / 0.004));
        double sub = dt / steps;
        for (int i = 0; i < steps; i++)
        {
            _width.Advance(sub);
            _height.Advance(sub);
            _radius.Advance(sub);
            _nudge.Advance(sub);
        }

        Updated?.Invoke(_width.Value, _height.Value, _radius.Value);

        if (IsSettled)
        {
            _width.Value = _width.Target;
            _height.Value = _height.Target;
            _radius.Value = _radius.Target;
            _width.Velocity = _height.Velocity = _radius.Velocity = 0;
            _nudge.Value = 0;
            _nudge.Velocity = 0;
            Updated?.Invoke(_width.Value, _height.Value, _radius.Value);
            Stop();
        }
    }

    private void Stop()
    {
        if (!_running)
        {
            return;
        }

        _running = false;
        CompositionTarget.Rendering -= OnRendering;
    }

    public void Dispose() => Stop();
}
