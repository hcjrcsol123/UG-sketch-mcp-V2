"""Exactly one cached bridge.status after F21 UNKNOWN; no model/op/return calls.
Secrets remain in memory and only selected non-credential status fields are saved.
"""
import argparse,datetime,json,os,subprocess,sys
from pathlib import Path
ROOT=Path(__file__).resolve().parents[2];sys.path.insert(0,str(ROOT/'tools/v2'))
from f01_f02_mcp_client import McpStdioClient


def main():
 p=argparse.ArgumentParser();p.add_argument('--output',type=Path,required=True);a=p.parse_args()
 if a.output.exists():raise FileExistsError('refusing to overwrite')
 report={'scope':'one cached bridge.status only; no operation, model, return, replay or thaw',
         'captured_utc':datetime.datetime.now(datetime.timezone.utc).isoformat(),'status_call_count':0,'observed':False}
 with a.output.open('x',encoding='utf-8') as f:json.dump(report,f)
 def save():a.output.write_text(json.dumps(report,indent=2)+'\n',encoding='utf-8')
 client=None
 try:
  install=Path.home()/'.codex/nx-sketch-mcp';env=os.environ.copy();env.update(PYTHONPATH=str(install/'app/src'),PYTHONUTF8='1',PYTHONDONTWRITEBYTECODE='1')
  proc=subprocess.Popen([sys.executable,'-B','-m','nx_sketch_mcp','--config',str(install/'mcp-config.json')],cwd=str(ROOT),env=env,stdin=subprocess.PIPE,stdout=subprocess.PIPE,stderr=subprocess.PIPE,bufsize=0)
  client=McpStdioClient(proc,15,max_line_bytes=16*1024*1024)
  client.request('initialize',{'protocolVersion':'2025-11-25','capabilities':{},'clientInfo':{'name':'f21-single-status','version':'1'}});client.notify('notifications/initialized')
  report['status_call_count']=1;save()
  response=client.request('tools/call',{'name':'nx_bridge_status','arguments':{}})
  value=response.get('structuredContent') or {}
  if response.get('isError') is True:
   report['error_code']=(value.get('error') or {}).get('code');save();return 1
  fields=('state','pid','ownership_state','active_operations','in_flight','writes_halted','write_halt_reason','model_read_available','model_write_enabled')
  report['status']={k:value.get(k) for k in fields};report['observed']=True;save()
  print(json.dumps({'output':str(a.output),'observed':True,**report['status']}));return 0
 except Exception as exc:
  report['failure_type']=type(exc).__name__;report['observed']=False;save()
  print(json.dumps({'output':str(a.output),'observed':False,'failure_type':type(exc).__name__}));return 1
 finally:
  if client is not None:client.close()


if __name__=='__main__':raise SystemExit(main())
