namespace Nina.LivePolarAlignment.Core;

public readonly record struct Vector3d(double X, double Y, double Z) {
    public double Length => Math.Sqrt(Dot(this));
    public double Dot(Vector3d b) => X*b.X+Y*b.Y+Z*b.Z;
    public Vector3d Cross(Vector3d b) => new(Y*b.Z-Z*b.Y,Z*b.X-X*b.Z,X*b.Y-Y*b.X);
    public Vector3d Normalized() => Length > 1e-12 && double.IsFinite(Length) ? this / Length : throw new ArgumentException("Direction must be finite and nonzero.");
    public static Vector3d operator +(Vector3d a, Vector3d b) => new(a.X+b.X,a.Y+b.Y,a.Z+b.Z);
    public static Vector3d operator -(Vector3d a, Vector3d b) => new(a.X-b.X,a.Y-b.Y,a.Z-b.Z);
    public static Vector3d operator *(Vector3d a, double b) => new(a.X*b,a.Y*b,a.Z*b);
    public static Vector3d operator /(Vector3d a, double b) => a*(1/b);
}

public readonly record struct SkyOrientation(Vector3d Center, Vector3d Right, Vector3d Up);
public readonly record struct PixelPoint(double X, double Y);
public readonly record struct AxisEstimate(Vector3d Axis, double PlaneResidualRadians, double Conditioning);
public readonly record struct PolarError(double AltitudeArcminutes, double AzimuthArcminutes, double TotalArcminutes);

public static class PolarAlignmentGeometry {
    // Position angle measures image UP from celestial north toward east. If the solver's
    // angle convention differs, adapt it at the integration boundary. Parity flips image right.
    public static SkyOrientation FromEquatorial(double rightAscensionDegrees, double declinationDegrees, double positionAngleDegrees, bool flipped=false) {
        if(!double.IsFinite(rightAscensionDegrees)||!double.IsFinite(declinationDegrees)||Math.Abs(declinationDegrees)>90||!double.IsFinite(positionAngleDegrees)) throw new ArgumentOutOfRangeException(nameof(declinationDegrees));
        double ra=rightAscensionDegrees*Math.PI/180,dec=declinationDegrees*Math.PI/180,pa=positionAngleDegrees*Math.PI/180;
        var center=new Vector3d(Math.Cos(dec)*Math.Cos(ra),Math.Cos(dec)*Math.Sin(ra),Math.Sin(dec));
        var north=new Vector3d(-Math.Sin(dec)*Math.Cos(ra),-Math.Sin(dec)*Math.Sin(ra),Math.Cos(dec));
        var east=new Vector3d(-Math.Sin(ra),Math.Cos(ra),0);
        return new(center,(east*Math.Cos(pa)-north*Math.Sin(pa))*(flipped?-1:1),north*Math.Cos(pa)+east*Math.Sin(pa));
    }

    // RA/Dec must be of-date; caller must precess J2000 solver results first for precision work.
    // Local sidereal angle includes east-positive longitude. Output is east,north,up.
    public static SkyOrientation ToHorizon(SkyOrientation equatorial, double localSiderealDegrees, double latitudeDegrees) {
        double lst=localSiderealDegrees*Math.PI/180,lat=latitudeDegrees*Math.PI/180;
        Vector3d Convert(Vector3d p) => new(-Math.Sin(lst)*p.X+Math.Cos(lst)*p.Y,-Math.Sin(lat)*Math.Cos(lst)*p.X-Math.Sin(lat)*Math.Sin(lst)*p.Y+Math.Cos(lat)*p.Z,Math.Cos(lat)*Math.Cos(lst)*p.X+Math.Cos(lat)*Math.Sin(lst)*p.Y+Math.Sin(lat)*p.Z);
        return new(Convert(equatorial.Center),Convert(equatorial.Right),Convert(equatorial.Up));
    }

    // Carries a calibrated mount axis along with subsequent camera motion while the RA
    // rotation remains locked. Both bases must use the same parity and coordinate frame.
    public static Vector3d TransformDirection(Vector3d direction, SkyOrientation previous, SkyOrientation current) =>
        (current.Right*direction.Dot(previous.Right)+current.Up*direction.Dot(previous.Up)+current.Center*direction.Dot(previous.Center)).Normalized();

    public static AxisEstimate EstimateAxis(SkyOrientation first, SkyOrientation second) {
        Vector3d[] a=[first.Right.Normalized(),first.Up.Normalized(),first.Center.Normalized()];
        Vector3d[] b=[second.Right.Normalized(),second.Up.Normalized(),second.Center.Normalized()];
        foreach(var frame in new[]{a,b}) if(Math.Abs(frame[0].Dot(frame[1]))>1e-5 || Math.Abs(frame[0].Dot(frame[2]))>1e-5 || Math.Abs(frame[1].Dot(frame[2]))>1e-5) throw new ArgumentException("Orientation basis must be orthogonal.");
        if(a[0].Cross(a[1]).Dot(a[2])*b[0].Cross(b[1]).Dot(b[2])<0) throw new ArgumentException("Camera parity changed between calibration frames; plate solve again.");
        double cosine=Math.Clamp((a.Zip(b,(x,y)=>x.Dot(y)).Sum()-1)/2,-1,1);
        double angle=Math.Acos(cosine);
        if(angle<Math.PI/180 || angle>179*Math.PI/180) throw new ArgumentException("Rotate between 1 and 179 degrees for a stable calibration.");
        var axis=a.Zip(b,(x,y)=>x.Cross(y)).Aggregate(new Vector3d(),(x,y)=>x+y).Normalized();
        if(axis.Dot(first.Center)<0) axis=axis*(-1);
        return new(axis,0,Math.Sin(angle/2));
    }
    // Boresights must share a fixed Earth/horizon frame. Plate-solved equatorial coordinates
    // must first be transformed using each exposure's timestamp (sidereal motion matters).
    public static AxisEstimate EstimateAxis(IReadOnlyList<Vector3d> boresights) {
        if (boresights.Count < 3) throw new ArgumentException("At least three separated boresights are required.");
        var points = boresights.Select(p => p.Normalized()).ToArray();
        var mean = points.Aggregate(new Vector3d(), (a,b) => a+b)/points.Length;
        var a = new double[3,3];
        foreach (var point in points) {
            var d=point-mean; double[] v=[d.X,d.Y,d.Z];
            for(int i=0;i<3;i++) for(int j=0;j<3;j++) a[i,j]+=v[i]*v[j]/points.Length;
        }
        var eigen = new double[,] {{1,0,0},{0,1,0},{0,0,1}};
        for(int iter=0;iter<40;iter++) {
            int p=0,q=1;
            for(int i=0;i<3;i++) for(int j=i+1;j<3;j++) if(Math.Abs(a[i,j])>Math.Abs(a[p,q])) { p=i;q=j; }
            if(Math.Abs(a[p,q])<1e-18) break;
            double angle=.5*Math.Atan2(2*a[p,q],a[q,q]-a[p,p]);
            double c=Math.Cos(angle),s=Math.Sin(angle);
            double app=a[p,p],aqq=a[q,q],apq=a[p,q];
            for(int k=0;k<3;k++) if(k!=p && k!=q) { double kp=a[k,p],kq=a[k,q]; a[k,p]=a[p,k]=c*kp-s*kq; a[k,q]=a[q,k]=s*kp+c*kq; }
            a[p,p]=c*c*app-2*s*c*apq+s*s*aqq; a[q,q]=s*s*app+2*s*c*apq+c*c*aqq; a[p,q]=a[q,p]=0;
            for(int k=0;k<3;k++) { double kp=eigen[k,p],kq=eigen[k,q]; eigen[k,p]=c*kp-s*kq; eigen[k,q]=s*kp+c*kq; }
        }
        var order=Enumerable.Range(0,3).OrderBy(i=>a[i,i]).ToArray();
        double middle=a[order[1],order[1]],largest=a[order[2],order[2]];
        if(middle < 1e-10 || largest < 1e-10) throw new ArgumentException("Rotation samples are too close or collinear; rotate farther before calibration.");
        int n=order[0]; var axis=new Vector3d(eigen[0,n],eigen[1,n],eigen[2,n]).Normalized();
        if(axis.Dot(mean)<0) axis=axis*(-1);
        return new(axis,Math.Sqrt(Math.Max(0,a[n,n])),middle/largest);
    }

    // Local Cartesian frame: X east, Y north, Z up. Axis points to the visible pole.
    // Positive altitude means too high. Azimuth is measured clockwise from north;
    // its signed offset is positive east of the north pole and west of the south pole.
    public static PolarError CalculateError(Vector3d axis, double latitudeDegrees) {
        if(!double.IsFinite(latitudeDegrees) || Math.Abs(latitudeDegrees)>90) throw new ArgumentOutOfRangeException(nameof(latitudeDegrees));
        axis=axis.Normalized(); double lat=latitudeDegrees*Math.PI/180;
        var pole=new Vector3d(0,Math.Cos(lat)*Math.Sign(lat==0?1:lat),Math.Abs(Math.Sin(lat)));
        if(axis.Dot(pole)<0) axis=axis*(-1);
        double altitude=Math.Asin(Math.Clamp(axis.Z,-1,1))-Math.Abs(lat);
        double azimuth=Math.Atan2(axis.X,axis.Y)-Math.Atan2(pole.X,pole.Y);
        azimuth=Math.Atan2(Math.Sin(azimuth),Math.Cos(azimuth));
        const double arcminutes=180*60/Math.PI;
        return new(altitude*arcminutes,azimuth*arcminutes,Math.Atan2(axis.Cross(pole).Length,axis.Dot(pole))*arcminutes);
    }

    // Gnomonic projection; image Y increases downward. Returns null outside the front hemisphere.
    public static PixelPoint? ProjectDirection(Vector3d direction, SkyOrientation orientation, double focalLengthPixels, double centerX, double centerY) {
        if(!double.IsFinite(focalLengthPixels) || focalLengthPixels<=0) throw new ArgumentOutOfRangeException(nameof(focalLengthPixels));
        direction=direction.Normalized(); double z=direction.Dot(orientation.Center.Normalized());
        return z<=1e-9 ? null : new(centerX+focalLengthPixels*direction.Dot(orientation.Right.Normalized())/z,centerY-focalLengthPixels*direction.Dot(orientation.Up.Normalized())/z);
    }
}
