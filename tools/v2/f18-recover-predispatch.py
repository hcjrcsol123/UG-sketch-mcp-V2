"""Recover only the F18 incident with proved pre-dispatch host failure.

Only cached bridge.status and ownership.return are used. In particular,
operation.get is not called: a missing ID would itself freeze the bridge.
Credentials remain in process memory and are never written or printed.
"""
import datetime,json,os,sys,traceback
from pathlib import Path
ROOT=Path(__file__).resolve().parents[2]
install=Path(os.environ.get('CODEX_HOME',str(Path.home()/'.codex')))/'nx-sketch-mcp'
sys.path.insert(0,str(install/'app/src'))
from nx_sketch_mcp.bridge import JsonlBridge
from nx_sketch_mcp.config import load_config
launch=ROOT/'workspace/v2/evidence/f18-launch-20261103-1.json'
incident=ROOT/'workspace/v2/evidence/f18-mcp-installed-20261103-1.json'
proof=ROOT/'workspace/v2/evidence/f18-offline-transport-diagnostic-f4373a573c0648b398fd23f3f333d3bd.json'
output=ROOT/'workspace/v2/evidence/f18-predispatch-recovery-20261103-1.json'
if output.exists():raise FileExistsError('refusing to overwrite recovery evidence')
l=json.loads(launch.read_text(encoding='utf-8'));r=json.loads(incident.read_text(encoding='utf-8'));p=json.loads(proof.read_text(encoding='utf-8'))
if p.get('passed') is not True or r.get('unknown_events',[{}])[0].get('operation_id')!='f18-reject-106af800790c47749b7a56afdf34cf70':
    raise RuntimeError('the incident or offline pre-dispatch proof is not the expected one')
expected_path=Path(r['fixture']['part_path']).resolve()
expected_epoch=str(l['nx_pid'])+':'+str(l['process_start_ticks'])
report={'scope':'cached status and ownership return after independently proved pre-dispatch failure',
        'incident':str(incident),'offline_proof':str(proof),'operation_get_called':False,
        'operation_get_reason':'missing operation ID freezes bridge writes; no server NOT_FOUND claim',
        'started_utc':datetime.datetime.now(datetime.timezone.utc).isoformat(),'stage':'start','passed':False}
def redact(value):
    if isinstance(value,dict):return {k:('[REDACTED]' if k in ('ownership_id','new_ownership_id','token','token_file') else redact(v)) for k,v in value.items()}
    if isinstance(value,list):return [redact(v) for v in value]
    return value
def save():
    with output.open('w',encoding='utf-8') as out:json.dump(report,out,ensure_ascii=False,indent=2,allow_nan=False)
with output.open('x',encoding='utf-8') as out:json.dump(report,out)
bridge=JsonlBridge(load_config(install/'mcp-config.json'))
try:
    status=bridge.call('bridge.status',{});report['status_before']=redact(status);report['stage']='status_read';save()
    identity=status.get('binding_identity') or {}
    if (status.get('pid')!=l['nx_pid'] or status.get('session_epoch')!=expected_epoch or
        status.get('ownership_state')!='AI_OWNED' or status.get('writes_halted') is not False or
        status.get('active_operations')!=0 or str(status.get('in_flight'))!='0' or status.get('binding_epoch')!=1 or
        identity.get('session_epoch')!=expected_epoch or Path(identity.get('part_path','')).resolve()!=expected_path or
        identity.get('artifact_scope')!='saved_independent_copy' or not isinstance(status.get('ownership_id'),str)):
        raise RuntimeError('cached status differs from the expected idle, unhalted F18 session; no return sent')
    report['stage']='return_started';save()
    returned=bridge.call('ownership.return',{'ownership_id':status['ownership_id'],'binding_epoch':status['binding_epoch']})
    report['return']=redact(returned);report['stage']='returned';save()
    after=bridge.call('bridge.status',{});report['status_after']=redact(after)
    if (after.get('pid')!=l['nx_pid'] or after.get('session_epoch')!=expected_epoch or
        after.get('ownership_state')!='HUMAN_OWNED' or after.get('writes_halted') is not False or
        after.get('active_operations')!=0 or str(after.get('in_flight'))!='0'):
        raise RuntimeError('post-return status did not prove an idle HUMAN_OWNED session')
    report['passed']=True;report['stage']='complete'
except Exception as exc:
    report['error_type']=type(exc).__name__;report['error_message']=str(exc);report['stage']='stopped'
finally:
    bridge.close();report['completed_utc']=datetime.datetime.now(datetime.timezone.utc).isoformat();save()
print(json.dumps({'report':str(output),'passed':report['passed'],'stage':report['stage']}))
if not report['passed']:raise SystemExit(1)
