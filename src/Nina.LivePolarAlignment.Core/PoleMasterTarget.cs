namespace Nina.LivePolarAlignment.Core;

public static class PoleMasterTarget {
    public static double AngularDistance(PixelPoint star,PixelPoint target,int width,int height,double pixelScaleArcseconds) {
        if(width<=0 || height<=0 || !double.IsFinite(pixelScaleArcseconds) || pixelScaleArcseconds<=0
            || !double.IsFinite(star.X) || !double.IsFinite(star.Y) || !double.IsFinite(target.X) || !double.IsFinite(target.Y))
            throw new ArgumentOutOfRangeException(nameof(pixelScaleArcseconds));
        double focal=206264.80624709636/pixelScaleArcseconds;
        Vector3d Ray(PixelPoint p)=>new Vector3d((p.X-width/2d)/focal,(height/2d-p.Y)/focal,1).Normalized();
        var a=Ray(star);var b=Ray(target);var cross=a.Cross(b);
        return Math.Atan2(Math.Sqrt(cross.Dot(cross)),a.Dot(b))*206264.80624709636;
    }
    // Ideal camera pose after the mount's azimuth and altitude have been set
    // to the northern pole, with the calibrated RA/DEC pose left unchanged.
    // Store this once at the returned adjustment reference: the target must not
    // chase subsequent camera motion while the user turns the bolts.
    public static SkyOrientation AlignedCamera(SkyOrientation camera, Vector3d mountAxis, double latitudeDegrees) {
        if(!double.IsFinite(latitudeDegrees) || latitudeDegrees is <=0 or >=89.5)
            throw new ArgumentOutOfRangeException(nameof(latitudeDegrees));
        var a=mountAxis.Normalized();
        double az=Math.Atan2(a.X,a.Y),alt=Math.Asin(Math.Clamp(a.Z,-1,1));
        double correction=latitudeDegrees*Math.PI/180-alt;
        Vector3d Rotate(Vector3d v) {
            var p=new Vector3d(Math.Cos(az)*v.X-Math.Sin(az)*v.Y,Math.Sin(az)*v.X+Math.Cos(az)*v.Y,v.Z);
            return new(p.X,Math.Cos(correction)*p.Y-Math.Sin(correction)*p.Z,Math.Sin(correction)*p.Y+Math.Cos(correction)*p.Z);
        }
        return new(Rotate(camera.Center),Rotate(camera.Right),Rotate(camera.Up));
    }
}
