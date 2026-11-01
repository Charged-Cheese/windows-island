namespace DynamicIsland.Infrastructure;

/// <summary>Page motion, independent of the 240ms island-size channel.</summary>
internal sealed record PagingMotion(double Stiffness, double Damping, double ClickDurationMs,
    bool Cubic, double OpacityExponent, double HideOpacity)
{
    public static PagingMotion Default { get; } = new(1200, 68, 200, true, 1, 0.002);

    // Explicit diagnostic sessions only; these comparisons never become saved user settings.
    public static PagingMotion Load()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DI_PAGING_LOG"))) return Default;
        return Environment.GetEnvironmentVariable("DI_PAGING_PROFILE") switch
        {
            "baseline" => new(700, 52, 240, false, 1.45, 0.02),
            "opacity" => new(700, 52, 240, false, 1, 0.002),
            "wheel1000" => new(1000, 62, 240, false, 1, 0.002),
            "wheel1200" => new(1200, 68, 240, false, 1, 0.002),
            "cubic240" => new(1200, 68, 240, true, 1, 0.002),
            "cubic200" => Default,
            _ => Default
        };
    }

    public double ClickProgress(double t) => 1 - Math.Pow(1 - Math.Clamp(t, 0, 1), Cubic ? 3 : 5);
    public double ClickSpeed(double t) => (Cubic ? 3 : 5) *
        Math.Pow(1 - Math.Clamp(t, 0, 1), Cubic ? 2 : 4) / (ClickDurationMs / 1000);

    public double Opacity(double distance)
    {
        double d = Math.Clamp(distance, 0, 1);
        double value = Math.Pow(1 - d * d * (3 - 2 * d), OpacityExponent);
        return value < HideOpacity ? 0 : value;
    }

    public (double Position, double Velocity) Advance(double position, double velocity, double target, double dt)
    {
        double halfDamping = Damping / 2;
        double frequency = Math.Sqrt(Stiffness - halfDamping * halfDamping);
        double error = position - target;
        double decay = Math.Exp(-halfDamping * dt);
        double cosine = Math.Cos(frequency * dt);
        double sine = Math.Sin(frequency * dt) / frequency;
        return (target + decay * (error * cosine + (velocity + halfDamping * error) * sine),
            decay * (velocity * cosine - (halfDamping * velocity + Stiffness * error) * sine));
    }
}
