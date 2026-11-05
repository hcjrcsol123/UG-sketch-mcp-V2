"""Compare frozen candidates by direct bytes; never compute file hashes."""
import datetime,json,sys,uuid
from pathlib import Path
ROOT=Path(__file__).resolve().parents[2]
if len(sys.argv) not in (3,4):raise SystemExit('usage: f18-compare-candidates.py r03 r05 [expected-different-path]')
old,new=sys.argv[1:3]
base=ROOT/'workspace/v2/candidates'
oldroot,newroot=base/('f18-'+old),base/('f18-'+new)
rows=[]
for group,pattern in [('bridge','src/bridge/*.cs'),('python','src/nx_sketch_mcp/*.py')]:
    oldfiles={p.relative_to(oldroot).as_posix():p for p in oldroot.glob(pattern)}
    newfiles={p.relative_to(newroot).as_posix():p for p in newroot.glob(pattern)}
    for name in sorted(set(oldfiles)|set(newfiles)):
        a,b=oldfiles.get(name),newfiles.get(name)
        rows.append({'file':name,'kind':group,'equal':a is not None and b is not None and a.read_bytes()==b.read_bytes()})
for name in ('tools/build-m2-bridge.ps1','tools/install-local.ps1'):
    rows.append({'file':name,'kind':'build_or_install','equal':(oldroot/name).read_bytes()==(newroot/name).read_bytes()})
for name in ('M2Bridge.dll','M2Entry.dll','M2JsonlHelper.exe','M2Startup.dll'):
    a=oldroot/'workspace/v2/candidates'/('f18-'+old+'-build')/name
    b=newroot/'workspace/v2/candidates'/('f18-'+new+'-build')/name
    rows.append({'file':name,'kind':'binary','equal':a.read_bytes()==b.read_bytes(),'old_bytes':a.stat().st_size,'new_bytes':b.stat().st_size})
expected={sys.argv[3]} if len(sys.argv)==4 else {'tools/install-local.ps1'}
differences={r['file'] for r in rows if not r['equal']}
report={'from':old,'to':new,'created_utc':datetime.datetime.now(datetime.timezone.utc).isoformat(),
        'method':'direct byte equality, no file hashes','differences':sorted(differences),'expected_differences':sorted(expected),
        'passed':differences==expected,'files':rows}
path=ROOT/'workspace/v2/evidence'/('f18-candidate-comparison-'+uuid.uuid4().hex+'.json')
with path.open('x',encoding='utf-8') as out:json.dump(report,out,indent=2)
print(json.dumps({'report':str(path),'passed':report['passed'],'files':len(rows),'differences':sorted(differences)}))
if not report['passed']:raise SystemExit(1)
