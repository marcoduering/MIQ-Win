namespace MIQ;

/// <summary>
/// Cross-target helpers so the parser/renderer compile identically on .NET 8
/// (the <c>MIQ</c> project) and .NET Framework 4.6.2 (the QuickLook plugin,
/// whose host CLR lacks <c>Math.Clamp</c>, <c>MathF</c>,
/// <c>BitConverter.Int32BitsToSingle</c>, etc.). Behaviour matches the .NET 8
/// originals exactly.
/// </summary>
internal static class MiqCompat
{
    public static int Clamp(int v, int lo, int hi) => v < lo ? lo : (v > hi ? hi : v);
    public static float Clamp(float v, float lo, float hi) => v < lo ? lo : (v > hi ? hi : v);
    public static double Clamp(double v, double lo, double hi) => v < lo ? lo : (v > hi ? hi : v);

    /// Round-half-to-even then truncate to int — matches <c>(int)MathF.Round(x)</c>.
    public static int RoundToInt(float v) =>
        (int)System.Math.Round((double)v, System.MidpointRounding.ToEven);

    public static bool IsFinite(float v) => !float.IsNaN(v) && !float.IsInfinity(v);

    public static unsafe float Int32BitsToSingle(int value) => *(float*)&value;

    public static unsafe double Int64BitsToDouble(long value) => *(double*)&value;
}
