"""Real JsonlBridge + controlled local subprocess; no NX, credentials or pipes."""
import os
from pathlib import Path
import sys
import tempfile
import unittest
from unittest.mock import patch

sys.path.insert(0,str(Path(__file__).resolve().parents[2]/'src'))
from nx_sketch_mcp.bridge import BridgeFailure,JsonlBridge
from nx_sketch_mcp.config import BridgeConfig


class AsyncSubprocessTests(unittest.TestCase):
    def setUp(self):
        self.temp=tempfile.TemporaryDirectory();self.addCleanup(self.temp.cleanup)
        self.root=Path(self.temp.name);self.marker=self.root/'marker';self.marker.touch()
        self.helper=Path(os.environ['F21_FAKE_HELPER']).resolve(strict=True)

    def invoke(self,mode,timeout=1.0):
        bridge=JsonlBridge(BridgeConfig('fake-pipe',self.marker,self.helper,timeout_seconds=timeout));self.addCleanup(bridge.close)
        with patch.dict(os.environ,{'F21_FAKE_HELPER_MODE':mode}):
            return bridge.call('operation.prepare_sweep',{'operation_id':'fake'})

    def test_real_helper_pending(self):
        result=self.invoke('pending');self.assertEqual(result,{'state':'QUEUED','operation_id':'fake'})

    def test_real_helper_faults_after_send_are_unknown(self):
        for mode,code in [('timeout','BRIDGE_TIMEOUT'),('eof','BRIDGE_UNAVAILABLE'),('badjson','BRIDGE_PROTOCOL_ERROR'),
                          ('badid','BRIDGE_PROTOCOL_ERROR'),('badresult','BRIDGE_PROTOCOL_ERROR'),('baderror','BRIDGE_PROTOCOL_ERROR'),('badenv','BRIDGE_PROTOCOL_ERROR')]:
            with self.subTest(mode=mode):
                with self.assertRaises(BridgeFailure) as cm:self.invoke(mode,timeout=.3)
                self.assertEqual(cm.exception.code,code);self.assertTrue(cm.exception.outcome_unknown)

    def test_real_helper_definitive_backend_error_not_unknown(self):
        with self.assertRaises(BridgeFailure) as cm:self.invoke('backend')
        self.assertEqual(cm.exception.code,'INVALID_REQUEST');self.assertFalse(cm.exception.outcome_unknown)

    def test_real_executable_start_failure_not_unknown(self):
        bridge=JsonlBridge(BridgeConfig('fake-pipe',self.marker,self.root/'missing-helper.exe',timeout_seconds=1));self.addCleanup(bridge.close)
        with self.assertRaises(BridgeFailure) as cm:bridge.call('operation.prepare_sweep',{'operation_id':'fake'})
        self.assertEqual(cm.exception.code,'BRIDGE_UNAVAILABLE');self.assertFalse(cm.exception.outcome_unknown)


if __name__=='__main__':unittest.main()
