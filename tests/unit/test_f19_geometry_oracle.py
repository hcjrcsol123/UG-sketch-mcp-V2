import copy
import importlib.util
import math
from pathlib import Path
import unittest

spec = importlib.util.spec_from_file_location('f19_oracle', Path(__file__).resolve().parents[2] / 'tools/v2/f19-geometry-oracle.py')
o = importlib.util.module_from_spec(spec)
spec.loader.exec_module(o)


def sample():
    # For p=(5,7,11), n=(1,1,0), reflection is (12-y,12-x,z).
    v = [[0,0,1],[2,0,1],[0,3,1],[2,3,1],[0,0,5],[2,0,5],[0,3,5],[2,3,5]]
    e = [[0,1],[2,3],[4,5],[6,7],[0,2],[1,3],[4,6],[5,7],[0,4],[1,5],[2,6],[3,7]]
    f = [[0,1,4,5],[2,3,6,7],[0,2,8,9],[1,3,10,11],[4,6,8,10],[5,7,9,11]]
    reflected = [[12,12,1],[12,10,1],[9,12,1],[9,10,1],[12,12,5],[12,10,5],[9,12,5],[9,10,5]]
    return {'seed': {'vertices_mm':v,'edges':e,'faces':f},
            'actual': {'vertices_mm':reflected,'edges':copy.deepcopy(e),'faces':copy.deepcopy(f),'face_areas_mm2':[6,6,8,8,12,12],'volume_mm3':24},
            'plane_origin_mm':[5,7,11], 'plane_normal':[1,1,0],
            'tolerance_mm':1e-6, 'tolerance_mm2':1e-6, 'tolerance_mm3':1e-6}


class F19IndependentOracle(unittest.TestCase):
    def test_offset_oblique_known_answer(self):
        self.assertTrue(o.check(sample())['ok'])

    def test_three_dimensional_oblique_known_answer(self):
        # Exact affine map for plane p=(40,0,0), n=(2,-3,6)/7:
        # (1/49)*([[41,12,-24],[12,31,36],[-24,36,-23]]*x
        #         + [320,-480,960]). Values below were calculated independently.
        d = sample()
        d['plane_origin_mm'] = [40,0,0]
        d['plane_normal'] = [2/7,-3/7,6/7]
        nums = [[296,-444,937],[378,-420,889],[332,-351,1045],[414,-327,997],
                [200,-300,845],[282,-276,797],[236,-207,953],[318,-183,905]]
        d['actual']['vertices_mm'] = [[x/49 for x in row] for row in nums]
        self.assertTrue(o.check(d)['ok'])
        d['plane_normal'] = [1,1,0]
        self.assertFalse(o.check(d)['ok'])

    def test_unordered_vertices_edges_and_faces(self):
        d = sample(); a = d['actual']
        vp = [6,0,4,3,1,7,2,5]; ep = [6,11,0,8,3,5,2,10,1,9,4,7]; fp = [3,1,5,0,4,2]
        vi = {old:new for new,old in enumerate(vp)}; ei = {old:new for new,old in enumerate(ep)}
        a['vertices_mm'] = [a['vertices_mm'][old] for old in vp]
        a['edges'] = [[vi[v] for v in a['edges'][old]] for old in ep]
        a['face_areas_mm2'] = [a['face_areas_mm2'][old] for old in fp]
        a['faces'] = [[ei[e] for e in a['faces'][old]] for old in fp]
        self.assertTrue(o.check(d)['ok'])

    def test_normal_scale_and_sign_do_not_change_plane(self):
        d = sample(); d['plane_normal'] = [-3,-3,0]
        self.assertTrue(o.check(d)['ok'])

    def test_wrong_reflection_with_same_aabb_fails(self):
        d = sample(); a = d['actual']; a['vertices_mm'][0], a['vertices_mm'][1] = a['vertices_mm'][1], a['vertices_mm'][0]
        result = o.check(d)
        self.assertFalse(result['ok']); self.assertEqual(result['reason'], 'edge endpoints mismatch')

    def test_wrong_plane_origin_fails(self):
        d = sample(); d['plane_origin_mm'] = [0,0,0]
        self.assertFalse(o.check(d)['ok'])

    def test_wrong_area_and_volume_fail_independently(self):
        d = sample(); d['actual']['face_areas_mm2'][2] = 8.01
        self.assertEqual(o.check(d)['reason'], 'face area mismatch')
        d = sample(); d['actual']['volume_mm3'] = 24.01
        self.assertEqual(o.check(d)['reason'], 'body volume mismatch')

    def test_bad_topology_rejected(self):
        d = sample(); d['actual']['faces'][0] = [0,1,4,6]
        with self.assertRaises(ValueError): o.check(d)

    def test_bad_seed_and_zero_normal_rejected(self):
        d = sample(); d['seed']['vertices_mm'][7][2] = 6
        with self.assertRaises(ValueError): o.check(d)
        d = sample(); d['plane_normal'] = [0,0,0]
        with self.assertRaises(ValueError): o.check(d)

    def test_nonfinite_and_boolean_rejected(self):
        for bad in [math.nan, math.inf, True]:
            d = sample(); d['plane_normal'][0] = bad
            with self.assertRaises(ValueError): o.check(d)

    def test_expression_sharing_evidence_requires_exact_transition(self):
        audit_spec = importlib.util.spec_from_file_location('f19_audit', Path(__file__).resolve().parents[2] / 'tools/v2/f19-audit-native.py')
        audit = importlib.util.module_from_spec(audit_spec); audit_spec.loader.exec_module(audit)
        before = {}; after = {}; changes = []
        for tag, rhs in [('1', '0'), ('2', '4')]:
            a = {'tag':tag,'id':tag,'name':'p'+tag,'type':'Number','value':int(rhs),'units':'MilliMeter',
                 'rhs':rhs,'owner_tag':'seed','user_tags':['seed']}
            b = dict(a, rhs=rhs+' // Used By ...', owner_tag=None, user_tags=['seed','output'])
            before[tag], after[tag] = a, b
            changes.append({'tag':tag,'before':a,'after':b,'allowed_shared_seed_expression':True,
                            'output_references_same_expression':True})
        evidence = {'status':'VERIFIED','before':before,'after':after,'changes':changes,
                    'seed_expression_tags':['1','2'],'output_expression_tags':['1','2']}
        result = {'model_preservation':{'expression_sharing':evidence},
                  'native_definition':{'output_feature_tag':'output'}}
        self.assertEqual(audit.audit_expression_sharing(result)['status'], 'PASS')
        for mutation in ('extra_user','wrong_rhs','changed_value','missing_reference'):
            bad = copy.deepcopy(result); e = bad['model_preservation']['expression_sharing']
            if mutation == 'extra_user': e['after']['1']['user_tags'].append('other')
            if mutation == 'wrong_rhs': e['after']['1']['rhs'] = '0 // another comment'
            if mutation == 'changed_value': e['after']['1']['value'] = 1
            if mutation == 'missing_reference': e['output_expression_tags'].remove('1')
            with self.assertRaises(ValueError, msg=mutation): audit.audit_expression_sharing(bad)

    def test_tolerances_are_applied_per_quantity(self):
        d = sample(); d['actual']['vertices_mm'][0][0] += 0.5e-6; d['actual']['face_areas_mm2'][0] += 0.5e-6; d['actual']['volume_mm3'] += 0.5e-6
        self.assertTrue(o.check(d)['ok'])
        d['actual']['volume_mm3'] += 1e-6
        self.assertFalse(o.check(d)['ok'])


if __name__ == '__main__': unittest.main()
