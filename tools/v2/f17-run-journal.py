"""Serialized F17 journal runner; observes existing sessions and never kills processes."""
from __future__ import annotations
import datetime, json, os, subprocess, sys, uuid
from pathlib import Path
ROOT=Path(__file__).resolve().parents[2]
EVIDENCE=ROOT / "workspace" / "v2" / "evidence"
NX_JOURNAL=Path(r"C:\0sf\ug\NXBIN\run_journal.exe")
def main():
    if len(sys.argv)!=2: raise SystemExit("usage: python tools/v2/f17-run-journal.py <journal>")
    script=Path(sys.argv[1]).resolve(strict=True)
    if not script.is_relative_to(ROOT): raise ValueError("Journal must be inside project")
    check=subprocess.run(["powershell","-NoProfile","-Command","@(Get-Process ugraf,run_journal,M2JsonlHelper -ErrorAction SilentlyContinue).Count"],capture_output=True,text=True,check=True)
    if check.stdout.strip()!="0": raise RuntimeError("Existing NX/helper process; inspect before launching")
    EVIDENCE.mkdir(parents=True,exist_ok=True)
    run_id=uuid.uuid4().hex; prefix=EVIDENCE / ("f17-batch-"+run_id)
    execution=prefix.with_suffix(".journal.py"); execution.write_bytes(script.read_bytes())
    path=prefix.with_suffix(".json")
    report={"run_id":run_id,"script":str(script),"executed_script_snapshot":str(execution),"operator":"/root/f17_validation","started_utc":datetime.datetime.now(datetime.timezone.utc).isoformat(),"forced_termination":False,"state":"STARTING"}
    def persist(): path.write_text(json.dumps(report,indent=2)+"\n",encoding="utf-8")
    persist(); env=os.environ.copy(); env["PYTHONUTF8"]="1"
    with prefix.with_suffix(".stdout.txt").open("wb") as out, prefix.with_suffix(".stderr.txt").open("wb") as err:
        proc=subprocess.Popen([str(NX_JOURNAL),str(execution)],cwd=str(ROOT),env=env,stdout=out,stderr=err,creationflags=subprocess.CREATE_NO_WINDOW)
        report.update(pid=proc.pid,state="RUNNING"); persist(); print(json.dumps({"report":str(path),"pid":proc.pid}),flush=True)
        report["exit_code"]=proc.wait()
    report.update(state="EXITED",finished_utc=datetime.datetime.now(datetime.timezone.utc).isoformat()); persist()
    print(json.dumps({"report":str(path),"exit_code":report["exit_code"]}),flush=True)
    return 0 if report["exit_code"]==0 else 1
if __name__=="__main__": raise SystemExit(main())
