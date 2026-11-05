"""Synthetic tests for reopened source association audit; no NX claims."""
import copy
import importlib.util
import json
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest

spec=importlib.util.spec_from_file_location('f21_roundtrip_audit',Path(__file__).resolve().parents[2]/'tools/v2/f21-audit-roundtrip.py')
audit=importlib.util.module_from_spec(spec);spec.loader.exec_module(audit)


def records():
    sketch={'origin_mm':[0,0,0],'feature_tag':'s','lines':[
        {'start_mm':[0,0,0],'end_mm':[3,0,0],'reference_status':'Active'},
        {'start_mm':[3,0,0],'end_mm':[0,2,0],'reference_status':'Active'},
        {'start_mm':[0,2,0],'end_mm':[0,0,0],'reference_status':'Active'}]}
    guide={'tag':'c','start_mm':[0,0,0],'end_mm':[0,0,5],'rule_member_tags':['c']}
    definition={'feature_type':'SKIN','profile_rules':[{'rule_type':'CurveFeatureRule','feature_tag':'s'}],
                'profile_output':copy.deepcopy(sketch['lines']),'parent_feature_tags':['s'],'preserve_shape':True,'preserve_guide_shape':False,
                'g0':.001,'g1':.5,'native_orientation':'ByAngularLaw','native_law':'Linear','native_start_deg':0,'native_end_deg':-45,
                'start_unit':'Degrees','end_unit':'Degrees','guide_elements':[guide]}
    curves=[{'tag':'c','kind':'LINE','start_mm':[0,0,0],'end_mm':[0,0,5]}]
    entry={'mode':'linear_twist','angle':45,'offset_mm':[0,0,0],'path':'straight'}
    return sketch,definition,curves,entry


class RoundtripAuditTests(unittest.TestCase):
    def test_missing_raw_file_generates_failure_evidence(self):
        with tempfile.TemporaryDirectory() as temp:
            root=Path(temp);capture=root/'capture.json';output=root/'audit.json'
            capture.write_text(json.dumps({'capture_completed':True,'closed_without_save':True,'dirty_before':False,
                'dirty_after':False,'manifest_path':str(root/'missing.json')}),encoding='utf-8')
            result=subprocess.run([sys.executable,str(Path(audit.__file__)), '--capture',str(capture),'--output',str(output)],capture_output=True,text=True)
            self.assertEqual(result.returncode,1)
            self.assertFalse(json.loads(output.read_text(encoding='utf-8'))['passed'])

    def test_valid_profile_path_and_right_hand_law(self):
        sketch,d,c,e=records();self.assertEqual(audit.sketch_check([sketch],0,False),sketch)
        self.assertEqual(audit.definition_check({'definition':d},sketch,c,e)['guide_count'],1)

    def test_reverse_or_wrong_source_rejected(self):
        sketch,d,c,e=records();d['guide_elements'][0]['start_mm'],d['guide_elements'][0]['end_mm']=d['guide_elements'][0]['end_mm'],d['guide_elements'][0]['start_mm']
        with self.assertRaises(AssertionError):audit.definition_check({'definition':d},sketch,c,e)
        sketch,d,c,e=records();d['profile_rules'][0]['feature_tag']='other'
        with self.assertRaises(AssertionError):audit.definition_check({'definition':d},sketch,c,e)

    def test_missing_profile_or_wrong_sign_rejected(self):
        sketch,d,c,e=records();d['profile_output'].pop()
        with self.assertRaises(AssertionError):audit.definition_check({'definition':d},sketch,c,e)
        sketch,d,c,e=records();d['native_end_deg']=45
        with self.assertRaises(AssertionError):audit.definition_check({'definition':d},sketch,c,e)

    def test_reference_and_duplicate_sketch_rejected(self):
        sketch,d,c,e=records();sketch['lines'][0]['reference_status']='Reference'
        with self.assertRaises(AssertionError):audit.sketch_check([sketch],0,False)
        sketch,d,c,e=records()
        with self.assertRaises(AssertionError):audit.sketch_check([sketch,copy.deepcopy(sketch)],0,False)


if __name__=='__main__':unittest.main()
