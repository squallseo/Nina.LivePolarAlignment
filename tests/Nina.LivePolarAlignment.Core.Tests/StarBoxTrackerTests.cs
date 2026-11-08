using Nina.LivePolarAlignment.Core;
using Xunit;

namespace Nina.LivePolarAlignment.Core.Tests;

public sealed class StarBoxTrackerTests {
    [Fact]
    public void BoxesKeepStarIdentitiesThroughFortyDegreeRaRotationInAnOffAxisWideField() {
        var initial=PolarAlignmentGeometry.FromEquatorial(150,88,20);
        var random=new Random(71);const double focal=6666.6666667;
        var reference=Enumerable.Range(0,35).Select(_=>new PixelPoint(220+random.NextDouble()*840,180+random.NextDouble()*600)).ToArray();
        var sky=reference.Select(p=>(initial.Center+initial.Right*((p.X-640)/focal)-initial.Up*((p.Y-480)/focal)).Normalized()).ToArray();
        var tracker=new StarBoxTracker();Assert.False(tracker.Update(reference,1280,960).Verified);
        for(int degrees=2;degrees<=40;degrees+=2) {
            double angle=degrees*Math.PI/180;var axis=new Vector3d(0,0,1);
            Vector3d Turn(Vector3d v)=>v*Math.Cos(angle)+axis.Cross(v)*Math.Sin(angle)+axis*axis.Dot(v)*(1-Math.Cos(angle));
            var pose=new SkyOrientation(Turn(initial.Center),Turn(initial.Right),Turn(initial.Up));
            var positions=sky.Select(v=>PolarAlignmentGeometry.ProjectDirection(v,pose,focal,640,480)!.Value).ToArray();
            var visible=positions.Where(p=>p.X>=0&&p.Y>=0&&p.X<1280&&p.Y<960).Reverse().ToArray();
            var result=tracker.Update(visible,1280,960);
            Assert.True(result.Verified,$"Lost matches at {degrees} degrees");
            foreach(var marker in result.Markers) {
                var expected=positions[marker.Id-1];
                Assert.InRange(Math.Abs(marker.Position.X-expected.X),0,1e-6);
                Assert.InRange(Math.Abs(marker.Position.Y-expected.Y),0,1e-6);
            }
        }
        Assert.Empty(tracker.Update([],1280,960).Markers);
        Assert.False(tracker.Update(reference,1280,960).Verified); // A new field is never claimed as an uninterrupted track.
    }
}
