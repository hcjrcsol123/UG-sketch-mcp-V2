"""Synthetic plane inventory controls. These are not NX measurements."""
import copy, importlib.util, json, math, os, subprocess, unittest
from pathlib import Path
ROOT=Path(__file__).resolve().parents[2]
spec=importlib.util.spec_from_file_location('inventory_audit',ROOT/'tools/v2/f21-inventory-audit.py');audit=importlib.util.module_from_spec(spec);spec.loader.exec_module(audit)
EXE=Path(os.environ['F21_INVENTORY_SYNTHETIC_EXE'])

def make_map(lim,scale=1000,shift=0):
 a,b,c,d=lim;uvs=[[a,c],[a,d],[b,c],[b,d],[a/2+b/2,c/2+d/2]]
 return {'face_tag':'f1','uv_limits':list(lim),'samples':[{'uv':uv,'point_mm':[uv[0]*scale+shift,uv[1]*scale,0.0],'du':[scale,0.0,0.0],'dv':[0.0,scale,0.0]} for uv in uvs]}

def build(steps=1,scale=1000,shift=0):
 a=[0.001,0.002,0.003,0.004];b=a.copy()
 for _ in range(steps):b[1]=math.nextafter(b[1],math.inf)
 face={'face_tag':'f1','is_planar':True,'native_type':22,'face_id':'face','direction':[0,0,1],'origin_mm':[0,0,0],'uv_limits':a,'edge_tags':['e1'],'bounds_mm':[0,0,0,1,1,0]}
 old={'body_tag':'b1','faces':[face],'edges':[{'edge_tag':'e1'}]};new=copy.deepcopy(old);new['faces'][0]['uv_limits']=b
 wrap=lambda body:json.dumps([['B|b1|body|True|'+json.dumps(body,separators=(',',':'))],False,True],separators=(',',':'))
 return {'mode':'compare','before':wrap(old),'after':wrap(new),'maps_before':{'f1':make_map(a,scale)},'maps_after':{'f1':make_map(b,scale,shift)}}

def change_after(r,fn):
 z=json.loads(r['after']);pieces=z[0][0].split('|',4);body=json.loads(pieces[4]);fn(body);z[0][0]='|'.join(pieces[:4])+'|'+json.dumps(body,separators=(',',':'));r['after']=json.dumps(z,separators=(',',':'))

def run_cs(r):
 p=subprocess.run([str(EXE)],input=json.dumps(r,allow_nan=False)+'\n',text=True,capture_output=True,check=True);return json.loads(p.stdout)

class InventoryControls(unittest.TestCase):
 def check_both(self,r,expected):
  got=run_cs(r);self.assertEqual(got.get('passed'),expected,got)
  try:
   audit.validate_snapshot(r['before'],r['maps_before']);audit.validate_snapshot(r['after'],r['maps_after']);audit.compare(r['before'],r['after'],r['maps_before'],r['maps_after']);py=True
  except (ValueError,KeyError,TypeError,OverflowError):py=False
  self.assertEqual(py,expected)
 def test_exact_and_one_ulp(self):
  r=build(0);self.check_both(r,True)
  r=build(1);self.check_both(r,True)
  r=build(8);self.check_both(r,True)
 def test_more_than_eight(self):self.check_both(build(9),False)
 def test_large_jacobian(self):self.check_both(build(1,1e12),False)
 def test_world_shift(self):self.check_both(build(1,1000,1e-6),False)
 def test_opposing_derivatives_do_not_cancel_bound(self):
  r=build();hi=0.125
  for _ in range(64):hi=math.nextafter(hi,math.inf)
  a=[0.125,hi,0.125,hi];b=[math.nextafter(a[0],math.inf),math.nextafter(a[1],math.inf),math.nextafter(a[2],-math.inf),math.nextafter(a[3],-math.inf)]
  z=json.loads(r['before']);parts=z[0][0].split('|',4);body=json.loads(parts[4]);body['faces'][0]['uv_limits']=a
  def wrap(obj):return json.dumps([['B|b1|body|True|'+json.dumps(obj,separators=(',',':'))],False,True],separators=(',',':'))
  r['before']=wrap(body);other=copy.deepcopy(body);other['faces'][0]['uv_limits']=b;r['after']=wrap(other)
  def m(lim):
   x,y,z,w=lim;uvs=[[x,z],[x,w],[y,z],[y,w],[x/2+y/2,z/2+w/2]]
   return {'face_tag':'f1','uv_limits':lim,'samples':[{'uv':uv,'point_mm':[(2**26)*(uv[0]-0.125)+(2**26)*(uv[1]-0.125),uv[1]-0.125,0.0],'du':[2**26,0,0],'dv':[2**26,1,0]} for uv in uvs]}
  r['maps_before']={'f1':m(a)};r['maps_after']={'f1':m(b)}
  got=run_cs(r);self.assertFalse(got['passed']);self.assertIn('physical UV/world bound',got.get('first_difference',''))
  with self.assertRaisesRegex(ValueError,'physical bound'):audit.compare(r['before'],r['after'],r['maps_before'],r['maps_after'])
 def test_exact_still_requires_mapping(self):
  r=build(0);r['maps_after'].clear();self.check_both(r,False)
 def test_exact_uv_world_drift_rejected(self):
  r=build(0,1000,1e-6);self.check_both(r,False)
  self.assertIn('physical world drift',run_cs(r).get('first_difference',''))
 def test_native_plane_origin_anchor_rejected(self):
  r=build(1)
  for key in ('maps_before','maps_after'):
   for sample in r[key]['f1']['samples']:sample['point_mm'][2]=1.0
  self.check_both(r,False)
  self.assertIn('native plane',run_cs(r).get('first_difference',''))
 def test_missing_mapping(self):
  r=build();r['maps_after'].clear();self.check_both(r,False)
 def test_nonplane(self):
  r=build();change_after(r,lambda b:b['faces'][0].update(is_planar=False));self.check_both(r,False)
 def test_degenerate_derivative(self):
  r=build();r['maps_after']['f1']['samples'][0]['dv']=[0,0,0];self.check_both(r,False)
 def test_affine_derivative_and_point_mismatch(self):
  for field,value in [('du',[2000,0,0]),('point_mm',[10,10,0])]:
   r=build();r['maps_after']['f1']['samples'][0][field]=value;self.check_both(r,False)
 def test_face_identity_edge_world_and_other_field_changes(self):
  for fn in [lambda b:b['faces'][0].update(face_tag='f2'),lambda b:b['faces'][0]['edge_tags'].append('e2'),lambda b:b['faces'][0]['bounds_mm'].__setitem__(0,1),lambda b:b['edges'][0].update(edge_tag='e2')]:
   r=build();change_after(r,fn);self.check_both(r,False)
 def test_dirty_or_active_changed(self):
  for index,value in [(1,True),(2,False)]:
   r=build();z=json.loads(r['after']);z[index]=value;r['after']=json.dumps(z);self.check_both(r,False)
 def test_missing_and_nonfinite_sample(self):
  for value in ['nan','inf']:
   r=build();m=r['maps_after']['f1'];result=run_cs({'mode':'nonfinite','mapping':m,'normal':[0,0,1],'origin':[0,0,0],'value':value});self.assertFalse(result['passed'])
   m=copy.deepcopy(m);m['samples'][0]['point_mm'][0]=math.nan if value=='nan' else math.inf
   with self.assertRaises(ValueError):audit.mapping(m,'f1',m['uv_limits'],[0,0,1],[0,0,0])
 def test_sign_and_zero_steps(self):
  tiny=math.nextafter(0.0,1.0);self.assertEqual(audit.steps(-0.0,0.0),0);self.assertEqual(audit.steps(-tiny,tiny),2)
  for a,b,n in [('-0','0',0),('-4.9406564584124654E-324','4.9406564584124654E-324',2)]:self.assertEqual(run_cs({'mode':'steps','a':a,'b':b})['steps'],n)
 def test_missing_fifth_point(self):
  r=build();r['maps_after']['f1']['samples'].pop();self.check_both(r,False)
 def test_legacy_unknown_cannot_be_audited(self):
  old=json.loads((ROOT/'workspace/v2/evidence/f21-roundtrip-860067b9abe546b2a7b2f8cecf9b3b51-raw.json').read_text(encoding='utf-8-sig'))
  with self.assertRaises(ValueError):audit.audit(old)

if __name__=='__main__':unittest.main()
