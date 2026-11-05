"""Directly compare current binaries with an F18 candidate build, without hashes."""
import json,sys,uuid
from pathlib import Path
ROOT=Path(__file__).resolve().parents[2]
if len(sys.argv)!=2:raise SystemExit('usage: f18-compare-current.py r05')
candidate=ROOT/'workspace/v2/candidates'/('f18-'+sys.argv[1]+'-build')
current=ROOT/'workspace/build/current/M2'
rows=[]
for name in ('M2Bridge.dll','M2Entry.dll','M2JsonlHelper.exe','M2Startup.dll'):
    a=(candidate/name).read_bytes();b=(current/name).read_bytes()
    rows.append({'name':name,'candidate_bytes':len(a),'current_bytes':len(b),'equal':a==b})
result={'candidate':str(candidate),'current':str(current),'method':'direct byte equality, no hashes',
        'files':rows,'passed':all(row['equal'] for row in rows)}
path=ROOT/'workspace/v2/evidence'/('f18-current-comparison-'+uuid.uuid4().hex+'.json')
with path.open('x',encoding='utf-8') as f:json.dump(result,f,indent=2)
print(json.dumps({'report':str(path),'passed':result['passed']}))
if not result['passed']:raise SystemExit(1)
