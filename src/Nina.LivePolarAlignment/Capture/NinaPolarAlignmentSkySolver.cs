using System.Threading;
using System.Threading.Tasks;
using NINA.Image.Interfaces;
using NINA.PlateSolving;
using Nina.LivePolarAlignment.Core;
namespace Nina.LivePolarAlignment.Capture;
public sealed partial class NinaPolarAlignmentCaptureService {
    private bool UsePolarisPattern=>PoleMasterMode;
    private Core.TrackedStar[]? patternMatchedStars;
    private Core.TrackedStar[]? patternTemplateStars;
    private double? calibratedPatternScale;
    public void SetPatternScale(double scale)=>calibratedPatternScale=scale;
    private Core.PixelPoint[] FindAlignmentStars(IImageData image,QuickLookPreview.Prepared prepared,CancellationToken token) {
        if(!UsePolarisPattern)return prepared.FindStars(token);
        var p=image.Properties;var masked=SkyRegionSelection.Mask(image.Data.FlatArray,p.Width,p.Height,new(0,0,p.Width,p.Height,"sky"),true,token);
        return StarCentroids.Detect(masked,p.Width,p.Height,masked,p.Width,p.Height,token);
    }
    private async Task<PlateSolveResult> SolveAlignmentImageAsync(IImageData image,PlateSolveParameter parameter,ImageSolver? solver,CancellationToken token,bool followTemplate=false) {
        if(UsePolarisPattern) {
            if(profiles.ActiveProfile.AstrometrySettings.Latitude<=0)throw new System.InvalidOperationException("Polaris pattern mode requires a northern observing site.");
            var properties=image.Properties;
            var clicked=fixedPolarisSeed is {} fixedSeed?new PixelPoint(fixedSeed.X*properties.Width,fixedSeed.Y*properties.Height)
                :streamingAnchor==null && PolarisCandidate is {} p?new PixelPoint(p.X*properties.Width,p.Y*properties.Height):(PixelPoint?)null;
            PolarisPatternResult result;
            try {result=await Task.Run(()=> {
                var prepared=QuickLookPreview.Prepare(image,token);
                var stars=FindAlignmentStars(image,prepared,token);
                if(fixedPolarisSeed!=null && clicked is {} fixedPoint
                    && SkyRegionSelection.ConfirmStarNear(image.Data.FlatArray,properties.Width,properties.Height,fixedPoint.X,fixedPoint.Y,20) is {} confirmed) {
                    stars=System.Linq.Enumerable.ToArray(System.Linq.Enumerable.Concat(new[]{confirmed},
                        System.Linq.Enumerable.Where(stars,p=>System.Math.Abs(p.X-confirmed.X)>3 || System.Math.Abs(p.Y-confirmed.Y)>3)));
                }
                double year=2000+(System.DateTime.UtcNow-new System.DateTime(2000,1,1)).TotalDays/365.25;
                if(followTemplate && fixedPolarisSeed==null && calibratedPatternScale is {} lensScale && streamingAnchor is {} anchor
                    && anchor.Frame.Width==properties.Width && anchor.Frame.Height==properties.Height
                    && System.DateTimeOffset.UtcNow-anchor.Frame.CapturedAtUtc<System.TimeSpan.FromSeconds(30)) {
                    var pose=PolarAlignmentGeometry.FromEquatorial(anchor.Frame.RightAscensionDegrees,anchor.Frame.DeclinationDegrees,anchor.Frame.PositionAngleDegrees,anchor.Frame.Flipped);
                    // Native video has different levels from still images. Building
                    // highlights can crowd real stars out of the global peak list.
                    // Measure known stars against their own local sky background.
                    var localStars=new System.Collections.Generic.List<PixelPoint>();
                    foreach(var star in Core.PolarisPattern.Catalog(year)) {
                        var expected=PolarAlignmentGeometry.ProjectDirection(star.Direction,pose,206264.80624709636/lensScale,properties.Width/2d,properties.Height/2d);
                        if(expected is {} p && SkyRegionSelection.ConfirmStarNear(image.Data.FlatArray,properties.Width,properties.Height,p.X,p.Y) is {} measured)
                            localStars.Add(measured);
                    }
                    try {return Core.PolarisPattern.MatchReference(localStars,properties.Width,properties.Height,pose,lensScale,anchor.Frame.Flipped,year,token);}
                    catch(System.InvalidOperationException) { }
                    if(anchor.Frame.RawImage is {} referenceRaw)try {
                        var oldStars=FindAlignmentStars(referenceRaw,QuickLookPreview.Prepare(referenceRaw,token),token);
                        var registration=FrameRegistration.Estimate(oldStars,stars,2.5);
                        if(registration.MatchedStars>=8 && registration.RmsPixels<1.5) {
                            var identified=new System.Collections.Generic.List<TrackedStar>();
                            foreach(var star in Core.PolarisPattern.Catalog(year)) {
                                var expected=PolarAlignmentGeometry.ProjectDirection(star.Direction,pose,206264.80624709636/lensScale,properties.Width/2d,properties.Height/2d);
                                if(expected is not {} q)continue;
                                var shifted=registration.Transform.Apply(q);
                                if(SkyRegionSelection.ConfirmStarNear(image.Data.FlatArray,properties.Width,properties.Height,shifted.X,shifted.Y) is {} measured)identified.Add(new(star.Id,measured));
                            }
                            if(identified.Count>=2 && System.Linq.Enumerable.Any(identified,s=>s.Id==11767))
                                return Core.PolarisPattern.RefitAtScale(new(pose,lensScale,anchor.Frame.Flipped,0,identified.ToArray()),properties.Width,properties.Height,year,lensScale,token);
                        }
                    } catch(System.InvalidOperationException) { } catch(System.ArgumentException) { }
                }
                PixelPoint? predicted=null;
                if(clicked==null && streamingAnchor is {} previous && previous.Frame.RawImage is {} raw
                    && previous.Frame.Width==properties.Width && previous.Frame.Height==properties.Height) {
                    try {
                        var oldStars=FindAlignmentStars(raw,QuickLookPreview.Prepare(raw,token),token);
                        var pose=PolarAlignmentGeometry.FromEquatorial(previous.Frame.RightAscensionDegrees,previous.Frame.DeclinationDegrees,previous.Frame.PositionAngleDegrees,previous.Frame.Flipped);
                        var polaris=System.Linq.Enumerable.Single(Core.PolarisPattern.Catalog(year),s=>s.Id==11767);
                        var oldPolaris=PolarAlignmentGeometry.ProjectDirection(polaris.Direction,pose,206264.80624709636/previous.Frame.PixelScaleArcseconds,properties.Width/2d,properties.Height/2d);
                        if(oldPolaris is {} stationary)predicted=stationary;
                        var registration=FrameRegistration.Estimate(oldStars,stars,2.5);
                        if(registration.MatchedStars>=8 && registration.RmsPixels<1.5 && oldPolaris is {} q)predicted=registration.Transform.Apply(q);
                    } catch(System.InvalidOperationException) { } catch(System.ArgumentException) { }
                }
                try { return Core.PolarisPattern.Match(stars,properties.Width,properties.Height,clicked??predicted,year,token,allowSparse:clicked!=null || streamingAnchor!=null,calibratedScale:calibratedPatternScale); }
                catch(System.InvalidOperationException) when(clicked==null && predicted!=null) {
                    return Core.PolarisPattern.Match(stars,properties.Width,properties.Height,null,year,token,calibratedScale:calibratedPatternScale);
                }
            },token);}
            catch(System.InvalidOperationException error) when(streamingAnchor==null) {throw new PolarisIdentificationException(error);}
            if(calibratedPatternScale is {} fixedScale)result=Core.PolarisPattern.RefitAtScale(result,properties.Width,properties.Height,
                2000+(System.DateTime.UtcNow-new System.DateTime(2000,1,1)).TotalDays/365.25,fixedScale,token);
            if(fixedPolarisSeed!=null && streamingAnchor is {} adjustmentReference) {
                var reference=PolarAlignmentGeometry.FromEquatorial(adjustmentReference.Frame.RightAscensionDegrees,adjustmentReference.Frame.DeclinationDegrees,
                    adjustmentReference.Frame.PositionAngleDegrees,adjustmentReference.Frame.Flipped);
                Core.PolarisPattern.ValidateAdjustmentOrientation(reference,result.Orientation);
            }
            patternMatchedStars=result.Stars;
            patternTemplateStars=System.Linq.Enumerable.ToArray(System.Linq.Enumerable.Select(Core.PolarisPattern.Catalog(2000+(System.DateTime.UtcNow-new System.DateTime(2000,1,1)).TotalDays/365.25),star=>new Core.TrackedStar(star.Id,
                PolarAlignmentGeometry.ProjectDirection(star.Direction,result.Orientation,206264.80624709636/result.PixelScale,properties.Width/2d,properties.Height/2d)!.Value)));
            var c=result.Orientation.Center;var up=result.Orientation.Up;
            double ra=(System.Math.Atan2(c.Y,c.X)*180/System.Math.PI+360)%360,dec=System.Math.Asin(c.Z)*180/System.Math.PI;
            var basis=PolarAlignmentGeometry.FromEquatorial(ra,dec,0);
            double angle=System.Math.Atan2(up.Dot(basis.Right),up.Dot(basis.Up))*180/System.Math.PI;
            NINA.Core.Utility.Logger.Info($"Polar local Polaris pattern: matched={result.Stars.Length}; scale={result.PixelScale:F4}; RMS={result.RmsPixels:F3}px; external solver not used.");
            return new PlateSolveResult{Success=true,Coordinates=new NINA.Astrometry.Coordinates(ra,dec,NINA.Astrometry.Epoch.J2000,NINA.Astrometry.Coordinates.RAType.Degrees),PositionAngle=angle,Pixscale=result.PixelScale,Flipped=result.Flipped};
        }
        return await Task.Run(()=>solver!.Solve(image,parameter,SilentProgress.Instance,token),token);
    }
}
