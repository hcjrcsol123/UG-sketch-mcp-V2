"""Create an F21 evidence index; validates references, never judges test results.

Input: JSON object with candidate, optional installed_version, and records list.
Each record has unique id, layer, status, artifacts, description, optional case_id
and supersedes. Artifacts must be existing local files in the project evidence,
models, F21 probe, or candidates directories. Different runs may repeat a case_id; each remains a separate
record. Counting requires an explicit independent review of the raw records.
This index is not a substitute for test assertions or an acceptance decision.
"""
from __future__ import annotations
import argparse
import datetime
import json
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
LAYERS = {'software','api_probe','native_product','installed_mcp','independent_reopen','byte_comparison','safety'}
STATUSES = {'PASS','FAIL','UNKNOWN','NOT_RUN','INFO'}


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--input', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    allowed = [(ROOT/'workspace/v2/evidence').resolve(), (ROOT/'workspace/v2/models').resolve(),
               (ROOT/'workspace/v2/f21-probe').resolve(), (ROOT/'workspace/v2/candidates').resolve()]
    output = args.output.resolve()
    if output.exists(): raise FileExistsError('Refusing to overwrite evidence index')
    if not output.is_relative_to(allowed[0]): raise ValueError('Index must be in evidence directory')
    source = json.loads(args.input.read_text(encoding='utf-8-sig'))
    if not isinstance(source,dict) or not isinstance(source.get('candidate'),str) or not source['candidate'].strip():
        raise ValueError('A candidate name is required')
    records = source.get('records')
    if not isinstance(records,list) or not records: raise ValueError('At least one record is required')
    ids = set()
    for row in records:
        if not isinstance(row,dict): raise ValueError('Record must be an object')
        rid = row.get('id')
        if not isinstance(rid,str) or not rid.strip() or rid in ids: raise ValueError('Record ids must be unique nonempty strings')
        ids.add(rid)
        if row.get('layer') not in LAYERS or row.get('status') not in STATUSES: raise ValueError('Invalid layer or status: '+rid)
        if not isinstance(row.get('description'),str) or not row['description'].strip(): raise ValueError('Description required: '+rid)
        artifacts = row.get('artifacts')
        if not isinstance(artifacts,list) or not artifacts or any(not isinstance(x,str) for x in artifacts):
            raise ValueError('Existing artifact references required: '+rid)
        for item in artifacts:
            path = (ROOT/item).resolve(strict=True)
            if not path.is_file() or not any(path.is_relative_to(base) for base in allowed):
                raise ValueError('Artifact outside allowed evidence/model files: '+item)
        if 'case_id' in row and (not isinstance(row['case_id'],str) or not row['case_id'].strip()): raise ValueError('Invalid case id: '+rid)
    for row in records:
        if 'supersedes' in row and (row['supersedes'] not in ids or row['supersedes']==row['id']):
            raise ValueError('Invalid supersedes reference: '+row['id'])
    result = {'created_utc':datetime.datetime.now(datetime.timezone.utc).isoformat(),
              'scope':'Evidence index only; status is transcribed, not independently verified; assertions are not NX case counts',
              'candidate':source['candidate'],'installed_version':source.get('installed_version'), 'records':records}
    output.parent.mkdir(parents=True,exist_ok=True)
    with output.open('x',encoding='utf-8') as stream:
        json.dump(result,stream,indent=2,ensure_ascii=False); stream.write('\n')
    print(json.dumps({'output':str(output),'records':len(records),'status':'INDEX_CREATED_NOT_VERIFIED'}))


if __name__=='__main__': main()
