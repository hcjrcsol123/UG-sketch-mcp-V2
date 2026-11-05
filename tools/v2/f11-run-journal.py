"""Root-only serialized NX journal runner; never kills an NX process."""
import datetime
import json
import os
from pathlib import Path
import subprocess
import sys
import uuid

ROOT = Path(__file__).resolve().parents[2]
EVIDENCE = ROOT / 'workspace/v2/evidence'

def main():
    script = Path(sys.argv[1]).resolve(strict=True)
    if not script.is_relative_to(ROOT):
        raise ValueError('Journal must be in this project')
    # Exclude concurrent GUI, batch journal, and helper sessions.
    check = subprocess.run(['powershell', '-NoProfile', '-Command',
        "@(Get-Process ugraf,run_journal,M2JsonlHelper -ErrorAction SilentlyContinue).Count"],
        capture_output=True, text=True, check=True)
    if check.stdout.strip() != '0':
        raise RuntimeError('An NX/helper process exists; inspect it before launching')
    run = uuid.uuid4().hex
    prefix = EVIDENCE / ('f11-batch-' + run)
    report = {'run_id': run, 'script': str(script), 'operator': '/root',
              'started_utc': datetime.datetime.now(datetime.timezone.utc).isoformat(),
              'forced_termination': False, 'state': 'STARTING'}
    output = prefix.with_suffix('.json')
    def persist():
        output.write_text(json.dumps(report, indent=2) + '\n', encoding='utf-8')
    persist()
    env = os.environ.copy()
    env['PYTHONUTF8'] = '1'
    with prefix.with_suffix('.stdout.txt').open('wb') as stdout, prefix.with_suffix('.stderr.txt').open('wb') as stderr:
        process = subprocess.Popen([r'C:\0sf\ug\NXBIN\run_journal.exe', str(script)],
            cwd=str(ROOT), env=env, stdout=stdout, stderr=stderr,
            creationflags=subprocess.CREATE_NO_WINDOW)
        report.update(pid=process.pid, state='RUNNING')
        persist()
        print(json.dumps({'report': str(output), 'pid': process.pid}), flush=True)
        report['exit_code'] = process.wait()
    report.update(state='EXITED', finished_utc=datetime.datetime.now(datetime.timezone.utc).isoformat())
    persist()
    print(json.dumps({'report': str(output), 'exit_code': report['exit_code']}), flush=True)

if __name__ == '__main__':
    main()
