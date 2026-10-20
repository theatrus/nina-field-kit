namespace Nina.FieldKit.Core;

public sealed record FlatRotationSample(DateTime Night, double MechanicalDegrees);

public static class FlatRotations {
    public static DateTime ObservingNight(DateTime localTime) => localTime.Date.AddDays(localTime.Hour < 12 ? -1 : 0);
    public static bool ValidAngle(double value) => double.IsFinite(value) && value >= 0 && value <= 360;
    public static double Normalize(double value) => value == 360 ? 0 : value;
    public static double Distance(double a, double b) {
        var distance = Math.Abs(Normalize(a) - Normalize(b));
        return Math.Min(distance, 360 - distance);
    }
    public static double[] Distinct(IEnumerable<double> angles, double tolerance) {
        if (!double.IsFinite(tolerance) || tolerance is < .01 or > 5)
            throw new ArgumentException("Angle tolerance must be between 0.01 and 5 degrees.");
        var result = new List<double>();
        foreach (var value in angles) {
            if (!ValidAngle(value)) throw new ArgumentException("A recorded mechanical angle is invalid.");
            var angle = Normalize(value);
            if (result.All(existing => Distance(existing, angle) > tolerance + .000001)) result.Add(angle);
        }
        return result.ToArray();
    }
}
