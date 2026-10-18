using Nina.LivePolarAlignment.Core;
using Xunit;
using Xunit.Abstractions;

public class GeometryTests {
 readonly ITestOutputHelper output;
 public GeometryTests(ITestOutputHelper output) => this.output=output;
 static Vector3d Rotate(Vector3d p,Vector3d axis,double angle) => p*Math.Cos(angle)+axis.Cross(p)*Math.Sin(angle)+axis*(axis.Dot(p)*(1-Math.Cos(angle)));
 [Fact] public void TwoSolvedOrientationsRecoverAxis() {
  var axis=new Vector3d(.02,.8,.6).Normalized(); var first=new SkyOrientation(new(0,0,1),new(1,0,0),new(0,1,0));
  var second=new SkyOrientation(Rotate(first.Center,axis,.55),Rotate(first.Right,axis,.55),Rotate(first.Up,axis,.55));
  var result=PolarAlignmentGeometry.EstimateAxis(first,second);
  Assert.True(result.Axis.Dot(axis)>1-1e-12);
 }
 [Fact] public void MultiSampleCircleRecoversAxisAndRejectsDegeneracy() {
  var axis=new Vector3d(.02,.8,.6).Normalized(); var initial=new Vector3d(.15,.7,.7).Normalized();
  var samples=Enumerable.Range(0,12).Select(i=>Rotate(initial,axis,i*.07)).ToArray();
  Assert.True(PolarAlignmentGeometry.EstimateAxis(samples).Axis.Dot(axis)>1-1e-10);
  Assert.Throws<ArgumentException>(()=>PolarAlignmentGeometry.EstimateAxis(new[]{initial,initial,initial}));
 }
 [Theory][InlineData(37)][InlineData(-37)] public void VisiblePoleHasZeroError(double latitude) {
  double rad=latitude*Math.PI/180;var pole=new Vector3d(0,Math.Cos(rad)*Math.Sign(rad),Math.Abs(Math.Sin(rad)));
  var error=PolarAlignmentGeometry.CalculateError(pole,latitude);
  Assert.InRange(error.TotalArcminutes,0,1e-9); Assert.InRange(Math.Abs(error.AltitudeArcminutes),0,1e-9);
 }
 [Fact] public void ProjectionUsesDownwardImageYAndRejectsBehindCamera() {
  var orientation=new SkyOrientation(new(0,0,1),new(1,0,0),new(0,1,0));
  Assert.Equal(new PixelPoint(600,200),PolarAlignmentGeometry.ProjectDirection(new(.1,.2,1),orientation,1000,500,400)!.Value);
  Assert.Null(PolarAlignmentGeometry.ProjectDirection(new(0,0,-1),orientation,1000,500,400));
 }
 [Fact] public void RegistrationSurvivesMissingStarsOutliersAndNoise() {
  var random=new Random(42);var stars=Enumerable.Range(0,22).Select(_=>new PixelPoint(random.NextDouble()*1200,random.NextDouble()*800)).ToArray();
  var expected=new RigidTransform(.025,14,-9);
  var current=stars.Skip(4).Select(p=> {var q=expected.Apply(p);return new PixelPoint(q.X+(random.NextDouble()-.5)*.2,q.Y+(random.NextDouble()-.5)*.2);}).Reverse().Concat(new[]{new PixelPoint(22,33),new PixelPoint(880,77)}).ToArray();
  var result=FrameRegistration.Estimate(stars,current,1);
  Assert.Equal(18,result.MatchedStars);Assert.InRange(result.RmsPixels,0,.15);
  Assert.InRange(Math.Abs(result.Transform.RotationRadians-expected.RotationRadians),0,.0002);
  Assert.InRange(Math.Abs(result.Transform.TranslationX-expected.TranslationX),0,.15);
 }
 [Fact] public void TwoFramesWithoutRotationAreRejected() {
  var frame=new SkyOrientation(new(0,0,1),new(1,0,0),new(0,1,0));
  Assert.Throws<ArgumentException>(()=>PolarAlignmentGeometry.EstimateAxis(frame,frame));
 }
 [Fact] public void EquatorialPoleTransformsToLocalNorthPole() {
  var frame=PolarAlignmentGeometry.FromEquatorial(125,90,0);
  var local=PolarAlignmentGeometry.ToHorizon(frame,205,37);
  Assert.InRange(PolarAlignmentGeometry.CalculateError(local.Center,37).TotalArcminutes,0,1e-9);
  Assert.InRange(Math.Abs(local.Right.Dot(local.Up)),0,1e-12);
 }
 [Fact] public void DirectionTransformCarriesCalibrationWithCameraMotion() {
  var axis=new Vector3d(.01,.8,.6).Normalized();
  var initial=new SkyOrientation(new(0,0,1),new(1,0,0),new(0,1,0));
  var final=new SkyOrientation(Rotate(initial.Center,axis,.5),Rotate(initial.Right,axis,.5),Rotate(initial.Up,axis,.5));
  var arbitraryDirection=new Vector3d(.2,.3,.8).Normalized();
  var moved=PolarAlignmentGeometry.TransformDirection(arbitraryDirection,initial,final);
  Assert.True(moved.Dot(Rotate(arbitraryDirection,axis,.5))>1-1e-12);
 }
 [Fact] public void OrientationParityDoesNotChangeRecoveredAxis() {
  var axis=new Vector3d(.1,.7,.7).Normalized();
  var initial=PolarAlignmentGeometry.FromEquatorial(120,65,15,true);
  var final=new SkyOrientation(Rotate(initial.Center,axis,.6),Rotate(initial.Right,axis,.6),Rotate(initial.Up,axis,.6));
  Assert.True(PolarAlignmentGeometry.EstimateAxis(initial,final).Axis.Dot(axis)>1-1e-12);
 }
 [Fact] public void CalibrationRejectsParityChangeAndMalformedBases() {
  var first=PolarAlignmentGeometry.FromEquatorial(120,65,15);
  Assert.Throws<ArgumentException>(()=>PolarAlignmentGeometry.EstimateAxis(first,PolarAlignmentGeometry.FromEquatorial(125,65,30,true)));
  Assert.Throws<ArgumentException>(()=>PolarAlignmentGeometry.EstimateAxis(first,new(first.Center,first.Up,first.Up)));
  Assert.Throws<ArgumentException>(()=>PolarAlignmentGeometry.EstimateAxis(first,new(first.Center,new Vector3d(double.NaN,0,0),first.Up)));
 }
 [Theory][InlineData(25)][InlineData(30)][InlineData(35)] public void RegistrationBenchmark(int count) {
  var random=new Random(101);var stars=Enumerable.Range(0,count).Select(_=>new PixelPoint(random.NextDouble()*1600,random.NextDouble()*1000)).ToArray();
  var expected=new RigidTransform(.035,23,-17);
  var current=stars.Skip(5).Select(p=>expected.Apply(p)).Reverse().Concat(Enumerable.Range(0,5).Select(_=>new PixelPoint(random.NextDouble()*1600,random.NextDouble()*1000))).ToArray();
  var watch=System.Diagnostics.Stopwatch.StartNew();var result=FrameRegistration.Estimate(stars,current,1);watch.Stop();
  output.WriteLine($"{count} stars, five replaced: {watch.Elapsed.TotalMilliseconds:F2} ms, {result.MatchedStars} matches; <= {FrameRegistration.MaximumHypotheses} hypotheses.");
  Assert.Equal(count-5,result.MatchedStars); Assert.InRange(result.RmsPixels,0,1e-8);
 }
 [Fact] public void UnrelatedFramesExhaustBoundedSearchAndRequireRecalibration() {
  var random=new Random(771);
  PixelPoint[] Field()=>Enumerable.Range(0,35).Select(_=>new PixelPoint(random.NextDouble()*1600,random.NextDouble()*1000)).ToArray();
  var reference=Field();var current=Field();var watch=System.Diagnostics.Stopwatch.StartNew();
  Assert.Throws<InvalidOperationException>(()=>FrameRegistration.Estimate(reference,current,2));
  watch.Stop();output.WriteLine($"Unrelated 35-star frames, bounded failed search: {watch.Elapsed.TotalMilliseconds:F2} ms.");
 }
}
