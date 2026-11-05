import copy
import importlib.util
from pathlib import Path
import unittest

spec = importlib.util.spec_from_file_location('f21_oracle', Path(__file__).resolve().parents[2] / 'tools/v2/f21-geometry-oracle.py')
oracle = importlib.util.module_from_spec(spec)
spec.loader.exec_module(oracle)


def sample(closed=True):
    # Known 3 x 2 rectangle / open L, swept 5 mm along Z; explicit topology.
    profile = [[0,0,0],[3,0,0],[3,2,0],[0,2,0]] if closed else [[0,0,0],[3,0,0],[3,2,0]]
    vertices = profile + [[p[0],p[1],5] for p in profile]
    if closed:
        pairs = [[0,1],[1,2],[2,3],[3,0],[4,5],[5,6],[6,7],[7,4],[0,4],[1,5],[2,6],[3,7]]
        faces = [([0,4,8,9],15,[0,0,0],[0,1,0]),([1,5,9,10],10,[3,0,0],[1,0,0]),([2,6,10,11],15,[0,2,0],[0,1,0]),([3,7,11,8],10,[0,0,0],[1,0,0]),([0,1,2,3],6,[0,0,0],[0,0,1]),([4,5,6,7],6,[0,0,5],[0,0,1])]
    else:
        pairs = [[0,1],[1,2],[3,4],[4,5],[0,3],[1,4],[2,5]]
        faces = [([0,2,4,5],15,[0,0,0],[0,1,0]),([1,3,5,6],10,[3,0,0],[1,0,0])]
    return {'profile_mm':profile,'closed':closed,'translation_mm':[0,0,5], 'tolerance_mm':1e-6,'tolerance_mm2':1e-6,'tolerance_mm3':1e-6,
            'actual':{'body_type':'solid' if closed else 'sheet','vertices_mm':vertices,
                      'edges':[{'vertices':p,'curve_type':'LINE'} for p in pairs],
                      'faces':[{'edges':e,'surface_type':'PLANE','area_mm2':a,'plane_origin_mm':p,'plane_normal':n} for e,a,p,n in faces],
                      'volume_mm3':30 if closed else None}}


class SweepOracleTests(unittest.TestCase):
    def test_planned_scalene_triangle(self):
        p=[[0,0,0],[3,0,0],[0,2,0]]
        edges=[[0,1],[1,2],[2,0],[3,4],[4,5],[5,3],[0,3],[1,4],[2,5]]
        faces=[([0,3,6,7],15,[0,0,0],[0,1,0]),([1,4,7,8],5*13**.5,[3,0,0],[2,3,0]),
               ([2,5,8,6],10,[0,0,0],[1,0,0]),([0,1,2],3,[0,0,0],[0,0,1]),([3,4,5],3,[0,0,5],[0,0,1])]
        d={'profile_mm':p,'closed':True,'translation_mm':[0,0,5],'tolerance_mm':1e-6,'tolerance_mm2':1e-4,'tolerance_mm3':1e-4,
           'actual':{'body_type':'solid','vertices_mm':p+[[0,0,5],[3,0,5],[0,2,5]],
                     'edges':[{'vertices':e,'curve_type':'LINE'} for e in edges],
                     'faces':[{'edges':e,'surface_type':'PLANE','area_mm2':a,'plane_origin_mm':o,'plane_normal':n} for e,a,o,n in faces],
                     'volume_mm3':15}}
        self.assertTrue(oracle.check(d)['passed'])

    def test_solid_and_sheet(self):
        self.assertTrue(oracle.check(sample())['passed'])
        self.assertTrue(oracle.check(sample(False))['passed'])

    def test_order_and_direction_irrelevant(self):
        data = sample(); a = data['actual']
        # Reverse edge storage and each edge's orientation; reindex face loops.
        a['edges'] = [dict(e,vertices=e['vertices'][::-1]) for e in a['edges'][::-1]]
        for face in a['faces']: face['edges'] = [len(a['edges'])-1-i for i in face['edges']]
        a['faces'].reverse()
        self.assertTrue(oracle.check(data)['passed'])

    def test_detect_wrong_shape_even_if_volume_and_counts_match(self):
        data = sample(); data['actual']['vertices_mm'][1][0] += 0.1
        with self.assertRaises(AssertionError): oracle.check(data)

    def test_detect_wrong_topology_even_if_coordinates_match(self):
        data = sample(); data['actual']['faces'][0]['edges'] = [0,4,8,10]
        with self.assertRaises(AssertionError): oracle.check(data)

    def test_detect_curved_edge_or_face(self):
        for key, field, bad in [('edges','curve_type','ARC'),('faces','surface_type','CYLINDER')]:
            data = sample(); data['actual'][key][0][field] = bad
            with self.assertRaises(AssertionError): oracle.check(data)

    def test_detect_wrong_native_plane(self):
        data = sample(); data['actual']['faces'][0]['plane_origin_mm'] = [0,0.1,0]
        with self.assertRaises(AssertionError): oracle.check(data)

    def test_detect_area_volume_and_type(self):
        for action in (lambda a:a['faces'][0].update(area_mm2=14),lambda a:a.update(volume_mm3=29),lambda a:a.update(body_type='sheet')):
            data=sample(); action(data['actual'])
            with self.assertRaises(AssertionError): oracle.check(data)

    def test_nonfinite_duplicate_and_unsupported_polygon_fail(self):
        for action in (lambda d:d.update(translation_mm=[0,0,float('nan')]),lambda d:d['profile_mm'].__setitem__(1,[0,0,0]),lambda d:d['profile_mm'].__setitem__(2,[1,0.5,0])):
            data=sample(); action(data)
            with self.assertRaises(ValueError): oracle.check(data)

    def test_sheet_requires_null_volume(self):
        data=sample(False); data['actual']['volume_mm3']=0
        with self.assertRaises(ValueError): oracle.check(data)


if __name__=='__main__': unittest.main()
