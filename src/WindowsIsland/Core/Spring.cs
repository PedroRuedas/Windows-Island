namespace WindowsIsland.Core;

/// <summary>Slightly under-damped spring: the secret behind the "bouncy" island feel.</summary>
public sealed class Spring(double initial)
{
    public double Value { get; private set; } = initial;
    public double Velocity { get; private set; }
    public double Target { get; set; } = initial;

    public double Stiffness { get; set; } = 260;

    /// <summary>Lower = bouncier. Critical damping is 2·√Stiffness (≈32 at the default stiffness).</summary>
    public double Damping { get; set; } = 24;

    /// <returns>True while still moving.</returns>
    public bool Step(double dt)
    {
        const double maxStep = 1 / 240.0;
        while (dt > 0)
        {
            double h = Math.Min(maxStep, dt);
            double acceleration = -Stiffness * (Value - Target) - Damping * Velocity;
            Velocity += acceleration * h;
            Value += Velocity * h;
            dt -= h;
        }

        if (Math.Abs(Value - Target) < 0.1 && Math.Abs(Velocity) < 0.1)
        {
            Value = Target;
            Velocity = 0;
            return false;
        }
        return true;
    }
}
