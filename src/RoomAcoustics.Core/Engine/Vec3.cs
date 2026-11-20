namespace RoomAcoustics.Core;

/// <summary>A point or direction in metres, double precision.</summary>
public readonly record struct Vec3(double X, double Y, double Z)
{
    public static Vec3 Zero => new(0, 0, 0);

    public static Vec3 operator +(Vec3 a, Vec3 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);

    public static Vec3 operator -(Vec3 a, Vec3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);

    public static Vec3 operator -(Vec3 a) => new(-a.X, -a.Y, -a.Z);

    public static Vec3 operator *(Vec3 a, double s) => new(a.X * s, a.Y * s, a.Z * s);

    public static Vec3 operator *(double s, Vec3 a) => a * s;

    public double Dot(Vec3 b) => X * b.X + Y * b.Y + Z * b.Z;

    public Vec3 Cross(Vec3 b) => new(Y * b.Z - Z * b.Y, Z * b.X - X * b.Z, X * b.Y - Y * b.X);

    public double Length => Math.Sqrt(Dot(this));

    public Vec3 Normalized()
    {
        var length = Length;
        if (length <= 0) throw new InvalidOperationException("Cannot normalise a zero-length vector.");
        return this * (1 / length);
    }
}
