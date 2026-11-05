using System;

namespace NxSketchMcp.M2
{
    // Pure world-coordinate geometry shared by the protocol validator and NX adapter.
    public sealed class F20SpatialGeometry
    {
        public string Type;
        public double[] Start, End, Through, Center, Normal, X, Y;
        public double Radius, StartRadians, SweepRadians;
        private static double[] Sub(double[] a,double[] b) { return new double[]{a[0]-b[0],a[1]-b[1],a[2]-b[2]}; }
        private static double Dot(double[] a,double[] b) { return a[0]*b[0]+a[1]*b[1]+a[2]*b[2]; }
        private static double[] Cross(double[] a,double[] b) { return new double[]{a[1]*b[2]-a[2]*b[1],a[2]*b[0]-a[0]*b[2],a[0]*b[1]-a[1]*b[0]}; }
        private static double Norm(double[] a) { return Math.Sqrt(Dot(a,a)); }
        private static double[] Unit(double[] a) { double n=Norm(a); return new double[]{a[0]/n,a[1]/n,a[2]/n}; }
        private static void Vector(double[] a,string name,bool coordinate)
        {
            if(a==null || a.Length!=3) throw new ArgumentException(name+" must have three components");
            foreach(double v in a) if(Double.IsNaN(v)||Double.IsInfinity(v)||(coordinate && Math.Abs(v)>1e9)) throw new ArgumentException(name+" is outside the finite coordinate range");
        }
        private static void Empty(double[] a,string name) { if(a!=null) throw new ArgumentException(name+" conflicts with curve mode"); }
        public double[] Point(double angle)
        {
            double c=Math.Cos(angle),s=Math.Sin(angle);
            return new double[]{Center[0]+Radius*(X[0]*c+Y[0]*s),Center[1]+Radius*(X[1]*c+Y[1]*s),Center[2]+Radius*(X[2]*c+Y[2]*s)};
        }
        public static F20SpatialGeometry FromRequest(M3SpatialCurveRequest r)
        {
            if(r==null || String.IsNullOrWhiteSpace(r.RevisionId)) throw new ArgumentException("spatial curve request and revision are required");
            F20SpatialGeometry g=new F20SpatialGeometry(); g.Type=r.CurveType;
            if(r.CurveType=="line" || (r.CurveType=="arc" && r.DefinitionMode=="three_points"))
            {
                if(r.CurveType=="line" && r.DefinitionMode!=null) throw new ArgumentException("line has no definition mode");
                Vector(r.StartMm,"start_mm",true); Vector(r.EndMm,"end_mm",true); Empty(r.CenterMm,"center_mm"); Empty(r.Normal,"normal"); Empty(r.XAxis,"x_axis");
                if(r.RadiusMm!=0 || r.StartAngleDeg!=0 || r.EndAngleDeg!=0) throw new ArgumentException("unexpected circular parameters");
                g.Start=(double[])r.StartMm.Clone(); g.End=(double[])r.EndMm.Clone();
                double[] b=Sub(g.End,g.Start); if(Norm(b)<=1e-6) throw new ArgumentException("endpoints must differ by more than 1e-6 mm");
                if(r.CurveType=="line") { Empty(r.ThroughMm,"through_mm"); return g; }
                Vector(r.ThroughMm,"through_mm",true); g.Through=(double[])r.ThroughMm.Clone();
                double[] a=Sub(g.Through,g.Start), n=Cross(a,b); double na=Norm(a), nb=Norm(b), nn=Norm(n);
                if(na<=1e-6 || Norm(Sub(g.Through,g.End))<=1e-6 || nn<=1e-8*na*nb) throw new ArgumentException("three points are coincident or nearly collinear");
                double[] u=Cross(b,n),v=Cross(n,a); double denom=2*nn*nn;
                g.Center=new double[3]; for(int i=0;i<3;i++)g.Center[i]=g.Start[i]+(Dot(a,a)*u[i]+Dot(b,b)*v[i])/denom;
                Vector(g.Center,"computed center",true); g.Radius=Norm(Sub(g.Start,g.Center));
                if(!(g.Radius>1e-6 && g.Radius<=1e9)) throw new ArgumentException("computed radius is outside (1e-6,1e9] mm");
                g.Normal=Unit(n); g.X=Unit(Sub(g.Start,g.Center)); g.Y=Cross(g.Normal,g.X); g.StartRadians=0;
                double[] end=Sub(g.End,g.Center),middle=Sub(g.Through,g.Center);
                double endAngle=Math.Atan2(Dot(end,g.Y),Dot(end,g.X)); if(endAngle<=0)endAngle+=2*Math.PI;
                double midAngle=Math.Atan2(Dot(middle,g.Y),Dot(middle,g.X)); if(midAngle<=0)midAngle+=2*Math.PI;
                if(!(midAngle<endAngle && endAngle<2*Math.PI && endAngle>1e-7*Math.PI/180)) throw new ArgumentException("three-point arc is numerically degenerate");
                g.SweepRadians=endAngle; return g;
            }
            if(r.CurveType!="circle" && !(r.CurveType=="arc" && r.DefinitionMode=="center")) throw new ArgumentException("unsupported spatial curve mode");
            if(r.CurveType=="circle" && r.DefinitionMode!=null) throw new ArgumentException("circle has no definition mode");
            Empty(r.StartMm,"start_mm"); Empty(r.ThroughMm,"through_mm"); Empty(r.EndMm,"end_mm");
            Vector(r.CenterMm,"center_mm",true); Vector(r.Normal,"normal",false); Vector(r.XAxis,"x_axis",false);
            if(Math.Abs(Norm(r.Normal)-1)>1e-9 || Math.Abs(Norm(r.XAxis)-1)>1e-9 || Math.Abs(Dot(r.Normal,r.XAxis))>1e-9) throw new ArgumentException("normal and x_axis must be unit and perpendicular within 1e-9");
            if(Double.IsNaN(r.RadiusMm)||Double.IsInfinity(r.RadiusMm)||!(r.RadiusMm>1e-6 && r.RadiusMm<=1e9)) throw new ArgumentException("radius must be in (1e-6,1e9] mm");
            g.Center=(double[])r.CenterMm.Clone(); g.Normal=(double[])r.Normal.Clone(); g.X=(double[])r.XAxis.Clone(); g.Y=Cross(g.Normal,g.X); g.Radius=r.RadiusMm;
            if(r.CurveType=="circle")
            {
                if(r.StartAngleDeg!=0 || r.EndAngleDeg!=0) throw new ArgumentException("circle has no angles");
                g.StartRadians=0; g.SweepRadians=2*Math.PI;
            }
            else
            {
                double sweep=r.EndAngleDeg-r.StartAngleDeg;
                if(Double.IsNaN(sweep)||Double.IsInfinity(sweep)||Math.Abs(r.StartAngleDeg)>1e9||Math.Abs(r.EndAngleDeg)>1e9||!(Math.Abs(sweep)>1e-7 && Math.Abs(sweep)<360)) throw new ArgumentException("arc angles or signed sweep are outside the supported range");
                g.StartRadians=(r.StartAngleDeg%360)*Math.PI/180; g.SweepRadians=sweep*Math.PI/180;
            }
            g.Start=g.Point(g.StartRadians);g.End=g.Point(g.StartRadians+g.SweepRadians); return g;
        }
    }
}
