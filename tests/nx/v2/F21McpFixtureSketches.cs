// F21 installed fixture-only native Sketch creation. No product adapter calls.
// The sole authorized operator invokes this in a new batch part after all paths.
using System;
using NXOpen;

public static class F21McpFixtureSketches
{
    static Point3d P(double x,double y,double z){return new Point3d(x,y,z);}
    public static int Run()
    {
        Session session=Session.GetSession();Part part=session.Parts.Work;
        if(part==null || session.ActiveSketch!=null)throw new InvalidOperationException("work part and no active Sketch required");
        for(int i=0;i<5;i++)
        {
            double x=i*40;Point3d origin=P(x,0,0);Vector3d normal=new Vector3d(0,0,1),axis=new Vector3d(1,0,0);
            Plane plane=part.Planes.CreatePlane(origin,normal,SmartObject.UpdateOption.WithinModeling);
            Direction direction=part.Directions.CreateDirection(origin,axis,SmartObject.UpdateOption.WithinModeling);
            NXOpen.Point point=part.Points.CreatePoint(origin);SketchInPlaceBuilder builder=part.Sketches.CreateSketchInPlaceBuilder2(null);
            Sketch sketch;
            try{builder.PlaneReference=plane;builder.AxisReference=direction;builder.SketchOrigin=point;
                builder.PlaneOption=Sketch.PlaneOption.ExistingPlane;builder.OriginOption=OriginMethod.SpecifyPoint;sketch=(Sketch)builder.Commit();}
            finally{builder.Destroy();}
            sketch.Activate(Sketch.ViewReorient.False);
            Point3d[] vertices={P(x,0,0),P(x+3,0,0),P(x,2,0)};int edges=i==4?2:3;
            for(int j=0;j<edges;j++){Line line=part.Curves.CreateLine(vertices[j],vertices[(j+1)%3]);sketch.AddGeometry(line,Sketch.InferConstraintsOption.InferNoConstraints);}
            sketch.Update();sketch.Deactivate(Sketch.ViewReorient.False,Sketch.UpdateLevel.Model);
            if(session.ActiveSketch!=null)throw new InvalidOperationException("fixture Sketch remained active");
            if(sketch.GetAllGeometry().Length!=edges)throw new InvalidOperationException("fixture Sketch member count differs");
        }
        return 0;
    }
    public static int GetUnloadOption(string dummy){return (int)Session.LibraryUnloadOption.Explicitly;}
}
