import copy
import importlib.util
import math
from pathlib import Path
import unittest

spec=importlib.util.spec_from_file_location('f21_twist_oracle',Path(__file__).resolve().parents[2]/'tools/v2/f21-twist-oracle.py')
oracle=importlib.util.module_from_spec(spec); spec.loader.exec_module(oracle)


def fixture(path='straight',angle=45):
    lengths=[0,1.25,2.5,3.75,5] if path=='straight' else [0,2.5,5,5+10*math.pi/8,5+10*math.pi/4,5+30*math.pi/8,5+5*math.pi]
    total=5 if path=='straight' else 5+5*math.pi
    rows=[]
    for s in lengths:
        theta=max(0,s-5)/10
        origin=(0,0,s) if s<=5 else (10*(1-math.cos(theta)),0,5+10*math.sin(theta))
        local_x=(math.cos(theta),0,-math.sin(theta)); local_y=(0,1,0)
        a=angle*math.pi/180*s/total
        points=[]
        for u,v in ((0,0),(3,0),(0,2),(.75,0),(1.5,0),(2.25,0),
                    (2.25,.5),(1.5,1),(.75,1.5),(0,1.5),(0,1),(0,.5)):
            h=u*math.cos(a)-v*math.sin(a); k=u*math.sin(a)+v*math.cos(a)
            points.append([origin[i]+h*local_x[i]+k*local_y[i] for i in range(3)])
        rows.append({'s_mm':s,'points_mm':points})
    return {'path':path,'mode':'transport' if angle==0 else 'linear_twist','angle_deg':angle,'samples':rows}


class TwistOracleTests(unittest.TestCase):
    def test_positive_negative_and_transport(self):
        for path in ('straight','line_arc'):
            for angle in (45,-45,0):
                with self.subTest(path=path,angle=angle): self.assertTrue(oracle.check(fixture(path,angle))['passed'])

    def test_hand_calculated_positive_endpoint(self):
        d=fixture(); p=d['samples'][-1]['points_mm']
        self.assertAlmostEqual(p[1][0],3/math.sqrt(2));self.assertAlmostEqual(p[1][1],3/math.sqrt(2))
        self.assertAlmostEqual(p[2][0],-2/math.sqrt(2));self.assertAlmostEqual(p[2][1],2/math.sqrt(2))
        self.assertTrue(oracle.check(d)['passed'])

    def test_wrong_handedness(self):
        d=fixture('straight',-45); d['angle_deg']=45
        with self.assertRaises(AssertionError): oracle.check(d)

    def test_wrong_law_even_with_correct_ends(self):
        d=fixture('line_arc',45)
        d['samples'][2]['points_mm']=fixture('line_arc',0)['samples'][2]['points_mm']
        with self.assertRaises(AssertionError): oracle.check(d)

    def test_missing_station_or_midpoint(self):
        d=fixture();d['samples'].pop(2)
        with self.assertRaises(AssertionError): oracle.check(d)
        d=fixture();d['samples'][2]['points_mm'].pop()
        with self.assertRaises(AssertionError): oracle.check(d)

    def test_perturbed_interior_edge(self):
        d=fixture();d['samples'][2]['points_mm'][4][0]+=0.021
        with self.assertRaises(AssertionError): oracle.check(d)

    def test_zero_mode_rejects_nonzero(self):
        d=fixture();d['mode']='transport'
        with self.assertRaises(ValueError): oracle.check(d)


if __name__=='__main__': unittest.main()
