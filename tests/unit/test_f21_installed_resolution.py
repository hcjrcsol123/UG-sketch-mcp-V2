"""Synthetic current-revision fixture resolution; no installed MCP or NX."""
import copy
import importlib.util
from pathlib import Path
import unittest

spec=importlib.util.spec_from_file_location('f21_mcp',Path(__file__).resolve().parents[1]/'nx/v2/smoke-f21-mcp.py')
m=importlib.util.module_from_spec(spec);spec.loader.exec_module(m)


def line(a,b,id):return {'kind':'LINE','reference_status':'Active','start':a,'end':b,'journal_id':id}

def fixture():
    e={'offset_mm':[80,0,0],'sheet':False,'path':'line_arc',
       'creation_sketch_id_historical':'old-sketch','creation_path_curve_ids_historical':['old-line','old-arc']}
    p={'sketches':[{'origin':['80','0','0'],'journal_id':'current-sketch','geometry':[
        line([80,0,0],[83,0,0],'a'),line([83,0,0],[80,2,0],'b'),line([80,2,0],[80,0,0],'c')]}],
       'spatial_curves':[{'native_type':'Line','detail_status':'SUPPORTED','start':[80,0,0],'end':[80,0,5],'journal_id':'current-line'},
        {'native_type':'Arc','detail_status':'SUPPORTED','center':[90,0,5],'radius_mm':10,'start':[80,0,5],'end':[90,0,15],
         'normal':[0,1,0],'journal_id':'current-arc'}]}
    return p,e


class InstalledFixtureResolutionTests(unittest.TestCase):
    def test_uses_current_geometry_not_historical_ids(self):
        p,e=fixture();self.assertEqual(m.resolve_fixture(p,e),('current-sketch',['current-line','current-arc']))
        p['spatial_curves'][0]['journal_id']='renumbered-line';self.assertEqual(m.resolve_fixture(p,e)[1][0],'renumbered-line')

    def test_ambiguous_path_or_sketch_rejected(self):
        p,e=fixture();p['spatial_curves'].append(copy.deepcopy(p['spatial_curves'][0]))
        with self.assertRaises(AssertionError):m.resolve_fixture(p,e)
        p,e=fixture();p['sketches'].append(copy.deepcopy(p['sketches'][0]))
        with self.assertRaises(AssertionError):m.resolve_fixture(p,e)

    def test_wrong_arc_direction_or_reference_profile_rejected(self):
        p,e=fixture();p['spatial_curves'][1]['normal']=[0,-1,0]
        with self.assertRaises(AssertionError):m.resolve_fixture(p,e)
        p,e=fixture();p['sketches'][0]['geometry'][0]['reference_status']='Reference'
        with self.assertRaises(AssertionError):m.resolve_fixture(p,e)


if __name__=='__main__':unittest.main()
