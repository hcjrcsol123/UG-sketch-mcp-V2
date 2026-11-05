"""Audit F19 native acceptance geometry with the independent reflection oracle.

No NX/product implementation imports; source report must contain actual native
face areas, volume, endpoints and adjacency. This does not prove feature identity,
straight edge type or plane association independently of the source collector.
Run: python tools/v2/f19-audit-native.py native-report.json new-output.json
"""
import importlib.util
import json
import math
from pathlib import Path
import sys
import traceback

ROOT = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location('f19_independent_oracle', ROOT / 'tools/v2/f19-geometry-oracle.py')
oracle = importlib.util.module_from_spec(spec)
spec.loader.exec_module(oracle)
CASES = {
    'explicit_oblique_plane': {'origin':[40,0,0], 'normal':[2/7,-3/7,6/7], 'seed_box':[0,0,1,2,3,5]},
    'datum_plane_with_old_mirror_and_pattern': {'origin':[10,50,0], 'normal':[2**-0.5,2**-0.5,0], 'seed_box':[0,40,1,2,43,5]},
}


def audit_expression_sharing(result):
    evidence = result['model_preservation']['expression_sharing']
    before, after = evidence['before'], evidence['after']
    changed = {k for k in before.keys() | after.keys() if before.get(k) != after.get(k)}
    changes = evidence['changes']
    if evidence['status'] != 'VERIFIED' or len(changes) != 2 or len(changed) != 2:
        raise ValueError('fixture must have exactly two evidenced seed expression changes')
    if {item['tag'] for item in changes} != changed:
        raise ValueError('expression change list does not match raw snapshots')
    seed_expr = set(evidence['seed_expression_tags']); output_expr = set(evidence['output_expression_tags'])
    output = result['native_definition']['output_feature_tag']
    for item in changes:
        tag = item['tag']; a=before[tag]; b=after[tag]; seed=a['owner_tag']
        if item['before'] != a or item['after'] != b or not seed or seed==output:
            raise ValueError('changed expression raw records differ')
        if not item['allowed_shared_seed_expression'] or not item['output_references_same_expression']:
            raise ValueError('changed expression not reported as shared')
        if tag not in seed_expr or tag not in output_expr:
            raise ValueError('seed/output expression references absent')
        if any(a[k] != b[k] for k in ('tag','id','name','type','value','units')):
            raise ValueError('expression core fields changed')
        if b['owner_tag'] is not None or a['user_tags'] != [seed] or set(b['user_tags']) != {seed,output} or len(b['user_tags']) != 2:
            raise ValueError('expression owner/user transition differs')
        if b['rhs'] != a['rhs'] + ' // Used By ...':
            raise ValueError('expression RHS transition differs')
    return {'status':'PASS','changes':changes,'seed_expression_tags':sorted(seed_expr),
            'output_expression_tags':sorted(output_expr)}

def audit(row, config, allow_failed_applied=False):
    if (row.get('status') != 'PASS' and not allow_failed_applied) or row.get('execution_state') != 'APPLIED':
        raise ValueError('native case was not applied and passed')
    result = row['result']
    if result.get('kind') != 'create_feature_mirror':
        raise ValueError('native result is not a mirror')
    geometry = result['geometry']
    if geometry.get('status') != 'VERIFIED' or len(geometry.get('instances',[])) != 1:
        raise ValueError('native geometry unavailable')
    seed = geometry['seed']; box=config['seed_box']
    expected = [[box[3 if i & 1 else 0], box[4 if i & 2 else 1], box[5 if i & 4 else 2]] for i in range(8)]
    if len(seed['vertices_mm']) != 8 or any(sum(math.dist(v,q) <= 1e-6 for q in seed['vertices_mm']) != 1 for v in expected):
        raise ValueError('seed vertices differ from fixture')
    instance = geometry['instances'][0]; native = instance['native_geometry']; faces=native['faces']
    if len(faces) != 6 or any(f['native_type'] != 22 for f in faces):
        raise ValueError('native plane type or face measurements absent')
    actual = dict(instance['shape'])
    actual['face_areas_mm2'] = [f['area_mm2'] for f in faces]
    actual['volume_mm3'] = native['volume_mm3']
    data = {'seed':seed,'actual':actual,'plane_origin_mm':config['origin'],'plane_normal':config['normal'],
            'tolerance_mm':1e-6,'tolerance_mm2':1e-4,'tolerance_mm3':1e-4}
    result = oracle.check(data)
    if not result.get('ok'):
        raise ValueError('oracle rejected native geometry: '+str(result))
    sharing = audit_expression_sharing(row['result'])
    return {'case':row['case'], 'oracle_input':data, 'oracle_result':result, 'expression_sharing':sharing}


def main():
    if len(sys.argv) != 3:
        raise SystemExit('usage: f19-audit-native.py native-report.json new-output.json')
    source, output = Path(sys.argv[1]).resolve(strict=True), Path(sys.argv[2])
    report = {'source':str(source),'scope':'offline native geometry only; not installed or reopen evidence',
              'passed':False,'cases':[]}
    with output.open('x',encoding='utf-8') as f: json.dump(report,f)
    try:
        native=json.loads(source.read_text(encoding='utf-8-sig'))
        for name, config in CASES.items():
            rows=[r for r in native['cases'] if r.get('case')==name]
            if len(rows)!=1: raise ValueError('missing or duplicated native case: '+name)
            report['cases'].append(audit(rows[0],config))
        report['passed']=True
    except Exception:
        report['error']=traceback.format_exc()
    finally:
        output.write_text(json.dumps(report,indent=2,allow_nan=False)+'\n',encoding='utf-8')
    print(json.dumps({'report':str(output),'passed':report['passed']}))
    return 0 if report['passed'] else 1


if __name__=='__main__': sys.exit(main())
