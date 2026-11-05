"""Offline checks for native-evidence auditor; synthetic records are not NX tests."""
import copy
import importlib.util
from pathlib import Path
import unittest

spec=importlib.util.spec_from_file_location('f21_audit_native',Path(__file__).resolve().parents[2]/'tools/v2/f21-audit-native.py')
audit=importlib.util.module_from_spec(spec);spec.loader.exec_module(audit)


class AuditNativeTests(unittest.TestCase):
    def setUp(self):
        self.points=[[0,0,0],[3,0,0],[0,2,0]]
        self.face={'is_planar':True,'edge_tags':['a','b','c'],'origin_mm':[0,0,0],'direction':[0,0,1]}
        self.edges={tag:{'edge_type':'Linear','evaluator_type':'LINE','vertices_mm':[self.points[a],self.points[b]],
                         'limit_points_mm':[self.points[a],self.points[b]]}
                    for tag,a,b in [('a',0,1),('b',1,2),('c',2,0)]}

    def test_cap_complete_edges_and_native_plane(self):
        self.assertTrue(audit.cap_matches(self.face,self.edges,self.points))

    def test_cap_rejects_wrong_type_and_missing_or_duplicated_boundary(self):
        e=copy.deepcopy(self.edges);e['a']['evaluator_type']='ARC'
        self.assertFalse(audit.cap_matches(self.face,e,self.points))
        f=copy.deepcopy(self.face);f['edge_tags']=['a','b','a']
        self.assertFalse(audit.cap_matches(f,self.edges,self.points))
        f=copy.deepcopy(self.face);f['edge_tags'].pop()
        self.assertFalse(audit.cap_matches(f,self.edges,self.points))

    def test_cap_rejects_wrong_plane_even_with_exact_boundary(self):
        f=copy.deepcopy(self.face);f['origin_mm']=[0,0,0.1]
        self.assertFalse(audit.cap_matches(f,self.edges,self.points))
        f=copy.deepcopy(self.face);f['is_planar']=False
        self.assertFalse(audit.cap_matches(f,self.edges,self.points))

    def test_native_spline_straightness_requires_bounded_collinear_poles(self):
        e=copy.deepcopy(self.edges);e['a']['evaluator_type']='SPLINE'
        e['a']['native_definition']={'control_point_count':3,'control_points':[0,0,0,1,1.5,0,0,1,3,0,0,1],
                                     'knot_count':6,'knots':[0,0,0,1,1,1]}
        self.assertTrue(audit.cap_matches(self.face,e,self.points))
        e['a']['native_definition']['control_points'][5]=0.01
        self.assertFalse(audit.cap_matches(self.face,e,self.points))
        e['a']['native_definition']['control_points'][5]=0
        e['a']['native_definition']['control_points'][7]=-1
        self.assertFalse(audit.cap_matches(self.face,e,self.points))

    def test_capture_is_bound_to_predeclared_mode_and_angle(self):
        spec=importlib.util.spec_from_file_location('twist_fixture',Path(__file__).with_name('test_f21_twist_oracle.py'))
        fixture=importlib.util.module_from_spec(spec);spec.loader.exec_module(fixture)
        capture={'oracle_input':fixture.fixture('straight',0),'offset_mm':[0,0,0]}
        with self.assertRaisesRegex(AssertionError,'predeclared'):
            audit.audit_capture(capture,{'path':'straight','mode':'linear_twist','angle':45,'offset_mm':[0,0,0]})


if __name__=='__main__': unittest.main()
