using Nina.LivePolarAlignment.Core;
using Xunit;

public class PolarAlignmentImpactTests {
    [Fact]
    public void PerfectAlignmentAddsNoPolarDrift() {
        Assert.Equal(new UnguidedDriftBound(0, 0), PolarAlignmentImpact.Estimate(0, 300));
    }

    [Fact]
    public void OneArcminuteHasSubArcsecondPerMinuteDriftAndScalesWithDuration() {
        var bound = PolarAlignmentImpact.Estimate(60, 300);
        Assert.InRange(bound.ArcsecondsPerMinute, .262, .263);
        Assert.InRange(bound.ExposureArcseconds, 1.31, 1.32);
        Assert.Equal(bound.ExposureArcseconds * 2, PolarAlignmentImpact.Estimate(60, 600).ExposureArcseconds, 10);
    }

    [Theory]
    [InlineData(60, 300)]
    [InlineData(600, 60)]
    [InlineData(3600, 3600)]
    public void BoundContainsIndependentRigidRotationDriftAcrossTheSky(double error, double seconds) {
        var radians = error * Math.PI / (180 * 3600);
        var pole = new Vector3d(0, 0, 1);
        var axis = new Vector3d(Math.Sin(radians), 0, Math.Cos(radians));
        double rotation = DriftValidation.SiderealRadiansPerSecond * seconds;
        Vector3d Turn(Vector3d v, Vector3d a, double theta) => v * Math.Cos(theta)
            + a.Cross(v) * Math.Sin(theta) + a * (a.Dot(v) * (1 - Math.Cos(theta)));
        for (int i = 0; i < 100; i++) {
            double longitude = i * 2.399963229728653;
            double z = 1 - 2 * (i + .5) / 100;
            double radius = Math.Sqrt(1 - z*z);
            var star = new Vector3d(radius * Math.Cos(longitude), radius * Math.Sin(longitude), z);
            var tracked = Turn(Turn(star, axis, rotation), pole, -rotation);
            double displacement = Math.Atan2(star.Cross(tracked).Length, star.Dot(tracked)) * 180 * 3600 / Math.PI;
            Assert.True(displacement <= PolarAlignmentImpact.Estimate(error, seconds).ExposureArcseconds + 1e-7);
        }
    }

    [Theory]
    [InlineData(-1, 300)]
    [InlineData(double.NaN, 300)]
    [InlineData(60, -1)]
    public void InvalidInputsAreRejected(double angle, double seconds) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => PolarAlignmentImpact.Estimate(angle, seconds));
}
