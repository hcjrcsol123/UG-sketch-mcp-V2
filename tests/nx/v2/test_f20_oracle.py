"""Offline oracle checks; no NX test results are implied."""
import importlib.util
import math
from pathlib import Path
import unittest

ROOT=Path(__file__).resolve().parents[3]
spec=importlib.util.spec_from_file_location('f20_oracle',ROOT/'tools/v2/f20-geometry-oracle.py')
o=importlib.util.module_from_spec(spec); spec.loader.exec_module(o)

class SpatialOracleTests(unittest.TestCase):
    def setUp(self):
        self.q={'curve_type':'arc','definition_mode':'center','center_mm':[7,8,9],
                'normal':[0,0,1],'x_axis':[1,0,0],'radius_mm':2,'start_angle_deg':30,'end_angle_deg':-210}
        self.a={'kind':'ARC','center_mm':[7,8,9],'axis_x':[1,0,0],'axis_y':[0,1,0],
                'radius_mm':2,'start_angle_rad':math.radians(30),'sweep_rad':math.radians(-240),
                'length_mm':2*math.radians(240),'points_mm':[o.point((7,8,9),(1,0,0),(0,1,0),2,math.radians(30-240*f)) for f in o.FRACTIONS]}
    def test_signed_major_and_wrong_minor(self):
        self.assertTrue(o.check(self.q,self.a)['passed'])
        self.a['sweep_rad']=math.radians(120); self.a['length_mm']=2*math.radians(120)
        self.assertFalse(o.check(self.q,self.a)['passed'])
    def test_wrong_plane_or_radius(self):
        self.a['axis_y']=[0,0,1]
        self.assertFalse(o.check(self.q,self.a)['passed'])
        self.a['axis_y']=[0,1,0]; self.a['radius_mm']=2.01
        self.assertFalse(o.check(self.q,self.a)['passed'])
    def test_wrong_direction_even_if_endpoints_same(self):
        self.a['start_angle_rad']=math.radians(-210); self.a['sweep_rad']=math.radians(240)
        self.a['points_mm'].reverse()
        self.assertFalse(o.check(self.q,self.a)['passed'])
    def test_three_points_select_major(self):
        q={'curve_type':'arc','definition_mode':'three_points','start_mm':[1,0,2],
           'through_mm':[-1,0,2],'end_mm':[0,1,2]}
        a={'kind':'ARC','center_mm':[0,0,2],'axis_x':[1,0,0],'axis_y':[0,-1,0],
           'radius_mm':1,'start_angle_rad':0,'sweep_rad':3*math.pi/2,'length_mm':3*math.pi/2,
           'points_mm':[o.point((0,0,2),(1,0,0),(0,-1,0),1,3*math.pi*f/2) for f in o.FRACTIONS]}
        self.assertTrue(o.check(q,a)['passed'])
        a['axis_y']=[0,1,0]; a['sweep_rad']=math.pi/2; a['length_mm']=math.pi/2
        self.assertFalse(o.check(q,a)['passed'])
    def test_circle_rejects_partial(self):
        self.q={'curve_type':'circle','center_mm':[7,8,9],'normal':[0,0,1],'x_axis':[1,0,0],'radius_mm':2}
        self.a.update(start_angle_rad=0,sweep_rad=2*math.pi,length_mm=4*math.pi)
        self.a['points_mm']=[o.point((7,8,9),(1,0,0),(0,1,0),2,2*math.pi*f) for f in o.FRACTIONS]
        self.assertTrue(o.check(self.q,self.a)['passed'])
        self.a.update(sweep_rad=math.pi,length_mm=2*math.pi)
        self.assertFalse(o.check(self.q,self.a)['passed'])
    def test_line_and_malformed(self):
        q={'curve_type':'line','start_mm':[1,2,3],'end_mm':[4,6,15]}
        a={'kind':'LINE','length_mm':13,'points_mm':[[1+3*f,2+4*f,3+12*f] for f in o.FRACTIONS]}
        self.assertTrue(o.check(q,a)['passed'])
        a['points_mm'][2][2]+=0.1
        self.assertFalse(o.check(q,a)['passed'])
        a['points_mm'][0][0]=float('nan')
        with self.assertRaises(ValueError): o.check(q,a)

if __name__=='__main__': unittest.main()
