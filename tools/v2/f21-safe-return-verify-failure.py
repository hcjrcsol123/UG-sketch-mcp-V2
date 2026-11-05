"""Safely return a known rolled-back F21 fixture failure to the human owner.

No NX model reads, writes, save, replay, or thaw. Credentials stay in memory and
are never printed or saved. If a status/return outcome is uncertain, stop.
"""
import argparse
import datetime
import importlib.util
import json
import os
from pathlib import Path
import subprocess
import sys

ROOT=Path(__file__).resolve().parents[2];sys.path.insert(0,str(ROOT/'tools/v2'))
from f01_f02_mcp_client import McpStdioClient
spec=importlib.util.spec_from_file_location('f21_return_helpers',ROOT/'tests/nx/v2/smoke-f13-mcp.py')
h=importlib.util.module_from_spec(spec);spec.loader.exec_module(h)


def main():
    parser=argparse.ArgumentParser();parser.add_argument('--failure',type=Path,required=True);parser.add_argument('--launch',type=Path,required=True);parser.add_argument('--output',type=Path,required=True);args=parser.parse_args()
    if args.output.exists():raise FileExistsError('refusing to overwrite')
    failure=json.loads(args.failure.read_text(encoding='utf-8-sig'));launch=json.loads(args.launch.read_text(encoding='utf-8-sig'))
    if failure.get('unknown_events') or failure.get('current_case')!='fixture_preconditions' or not failure.get('calls'):
        raise ValueError('run is not a known fixture failure')
    last=failure['calls'][-1];data=h._decode_payload(last.get('response',{}));result=data.get('result',{})
    required=('undo_called','semantic_restored','bodies_restored','dirty_state_restored','geometry_restored','history_restored','full_part_curve_inventory_restored','full_body_geometry_restored','active_sketch_restored')
    if (data.get('state')!='FAILED' or data.get('error',{}).get('code')!='M3_OPERATION_ROLLED_BACK' or
        data.get('writes_halted') is not False or result.get('mutation_state')!='ROLLED_BACK' or
        result.get('undo_update_error_count')!=0 or any(result.get(k) is not True for k in required)):
        raise ValueError('rollback is not fully proven')
    fixture=Path(failure['fixture']['part_path']).resolve(strict=True);models=(ROOT/'workspace/v2/models').resolve(strict=True)
    if not fixture.is_relative_to(models) or not fixture.name.startswith('F21-mcp-fixture-'):raise ValueError('not a dedicated F21 fixture')
    report={'scope':'known rolled-back dedicated F21 setup; status-return-status only, no model read or save',
            'source_failure':str(args.failure.resolve()),'fixture_path':str(fixture),'started_utc':datetime.datetime.now(datetime.timezone.utc).isoformat(),
            'passed':False,'unknown_or_stopped':False}
    args.output.parent.mkdir(parents=True,exist_ok=True)
    with args.output.open('x',encoding='utf-8') as f:json.dump(report,f)
    def save():args.output.write_text(json.dumps(report,indent=2)+'\n',encoding='utf-8')
    client=None
    try:
        install=Path.home()/'.codex/nx-sketch-mcp';env=os.environ.copy();env.update(PYTHONPATH=str(install/'app/src'),PYTHONUTF8='1',PYTHONDONTWRITEBYTECODE='1')
        proc=subprocess.Popen([sys.executable,'-B','-m','nx_sketch_mcp','--config',str(install/'mcp-config.json')],cwd=str(ROOT),env=env,
                              stdin=subprocess.PIPE,stdout=subprocess.PIPE,stderr=subprocess.PIPE,bufsize=0)
        client=McpStdioClient(proc,60,max_line_bytes=16*1024*1024)
        client.request('initialize',{'protocolVersion':'2025-11-25','capabilities':{},'clientInfo':{'name':'f21-safe-return','version':'1'}})
        client.notify('notifications/initialized')
        def call(name,arguments):
            response=client.request('tools/call',{'name':name,'arguments':arguments})
            if response.get('isError') is True:raise RuntimeError('tool returned error')
            return h._decode_payload(response)
        status=call('nx_bridge_status',{})
        identity=status.get('binding_identity',{});owner=status.get('ownership_id');epoch=status.get('binding_epoch')
        report['status_before']={'pid':status.get('pid'),'ownership_state':status.get('ownership_state'),
                                 'writes_halted':status.get('writes_halted'),'in_flight':status.get('in_flight'),
                                 'active_operations':status.get('active_operations'),'part_path':identity.get('part_path')}
        save()
        if (status.get('pid')!=launch['nx_pid'] or status.get('ownership_state')!='AI_OWNED' or status.get('writes_halted') is not False or
            str(status.get('in_flight'))!='0' or status.get('active_operations')!=0 or Path(identity.get('part_path','')).resolve()!=fixture or
            not isinstance(owner,str) or not owner or not isinstance(epoch,int)):
            raise ValueError('cached status does not establish authorized idle fixture')
        report['return_attempted']=True;save()
        try:returned=call('nx_return',{'ownership_id':owner,'binding_epoch':epoch})
        except Exception:
            report['unknown_or_stopped']=True;raise RuntimeError('return outcome unresolved; stopped')
        if returned.get('ownership_state')!='HUMAN_OWNED' or returned.get('safe_for_human') is not True:
            report['unknown_or_stopped']=True;raise RuntimeError('return did not prove human safe')
        final=call('nx_bridge_status',{})
        report['status_after']={'pid':final.get('pid'),'ownership_state':final.get('ownership_state'),'writes_halted':final.get('writes_halted'),
                                'in_flight':final.get('in_flight'),'active_operations':final.get('active_operations')}
        if final.get('pid')!=launch['nx_pid'] or final.get('ownership_state')!='HUMAN_OWNED' or final.get('writes_halted') is not False or str(final.get('in_flight'))!='0' or final.get('active_operations')!=0:
            raise RuntimeError('final cached status not safely idle')
        report['passed']=True;save();print(json.dumps({'output':str(args.output),'passed':True,'ownership_state':'HUMAN_OWNED'}));return 0
    except Exception as exc:
        report['failure_type']=type(exc).__name__;report['failure']='inspect safe summary; no further calls';save()
        print(json.dumps({'output':str(args.output),'passed':False,'unknown_or_stopped':report['unknown_or_stopped'],'failure_type':type(exc).__name__}));return 1
    finally:
        if client is not None:client.close()
        report['ended_utc']=datetime.datetime.now(datetime.timezone.utc).isoformat();save()


if __name__=='__main__':raise SystemExit(main())
