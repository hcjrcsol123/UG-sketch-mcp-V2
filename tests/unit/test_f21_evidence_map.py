"""Evidence index safety checks; fabricated temporary files, no NX."""
import importlib.util
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

spec=importlib.util.spec_from_file_location('f21_map',Path(__file__).resolve().parents[2]/'tools/v2/f21-evidence-map.py')
m=importlib.util.module_from_spec(spec);spec.loader.exec_module(m)


class EvidenceMapTests(unittest.TestCase):
    def setUp(self):
        self.tmp=tempfile.TemporaryDirectory();self.addCleanup(self.tmp.cleanup)
        self.root=Path(self.tmp.name);self.ev=self.root/'workspace/v2/evidence';self.ev.mkdir(parents=True)
        self.artifact=self.ev/'raw.json';self.artifact.write_text('{}',encoding='utf-8')
        self.old=m.ROOT;m.ROOT=self.root;self.addCleanup(setattr,m,'ROOT',self.old)
        self.source=self.root/'source.json';self.output=self.ev/'index.json'
        self.row={'id':'case-1','layer':'native_product','status':'PASS','artifacts':['workspace/v2/evidence/raw.json'],'description':'transcribed status'}

    def run_map(self,records):
        self.source.write_text(json.dumps({'candidate':'f21-test','records':records}),encoding='utf-8')
        with patch('sys.argv',['map','--input',str(self.source),'--output',str(self.output)]):m.main()

    def test_records_are_indexed_without_claiming_verification(self):
        with patch('builtins.print'):self.run_map([self.row])
        doc=json.loads(self.output.read_text(encoding='utf-8'))
        self.assertEqual(doc['records'][0]['status'],'PASS')
        self.assertIn('not independently verified',doc['scope'])

    def test_duplicate_id_rejected_before_output(self):
        with self.assertRaises(ValueError):self.run_map([self.row,self.row])
        self.assertFalse(self.output.exists())

    def test_missing_artifact_rejected_before_output(self):
        self.row['artifacts']=['workspace/v2/evidence/no-such-file']
        with self.assertRaises(FileNotFoundError):self.run_map([self.row])
        self.assertFalse(self.output.exists())


if __name__=='__main__':unittest.main()
