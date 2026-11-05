"""F21 async host transport fault classification using real JsonlBridge code.

Fake byte streams and queues only; no helper or NX process is started.  A sticky
UNKNOWN is never cleared or replayed; tests use synthetic operation identifiers.
"""
import io
import json
from pathlib import Path
import queue
import subprocess
import sys
import tempfile
from types import SimpleNamespace
import unittest
from unittest.mock import Mock,patch

sys.path.insert(0,str(Path(__file__).resolve().parents[2]/'src'))
import nx_sketch_mcp.bridge as bridge_module
from nx_sketch_mcp.bridge import BridgeFailure,JsonlBridge
from nx_sketch_mcp.config import BridgeConfig,config_from_object
from nx_sketch_mcp.server import McpServer


class PartialStream(io.BytesIO):
    def __init__(self,fail_at,exception):super().__init__();self.fail_at=fail_at;self.exception=exception
    def write(self,data):
        if self.fail_at=='write':super().write(data[:5]);raise self.exception('injected write failure')
        return super().write(data)
    def flush(self):
        if self.fail_at=='flush':raise self.exception('injected flush failure')
        return super().flush()


class RealJsonlFaultTests(unittest.TestCase):
    def setUp(self):
        self.temp=tempfile.TemporaryDirectory();self.addCleanup(self.temp.cleanup);self.root=Path(self.temp.name)
        self.marker=self.root/'marker';self.marker.touch();self.messages=queue.Queue();self.stream=io.BytesIO()
        self.process=SimpleNamespace(stdin=self.stream,poll=lambda:None)
        self.bridge=JsonlBridge(BridgeConfig('fake-pipe',self.marker,self.root/'unused-helper',timeout_seconds=.02))
        self.bridge._ensure_process=Mock(return_value=(self.process,self.messages));self.bridge._fail_process=Mock()

    def call(self):return self.bridge.call('operation.prepare_sweep',{'operation_id':'fake'})
    def fault(self,expected_code,unknown):
        with self.assertRaises(BridgeFailure) as cm:self.call()
        self.assertEqual(cm.exception.code,expected_code);self.assertEqual(cm.exception.outcome_unknown,unknown)

    def test_actual_jsonl_pending_envelope(self):
        self.messages.put(b'{"id":"1","result":{"state":"QUEUED","operation_id":"fake"}}\n')
        self.assertEqual(self.call()['state'],'QUEUED')
        sent=json.loads(self.stream.getvalue());self.assertEqual(sent['command'],'operation.prepare_sweep')
        self.assertEqual(sent['params']['operation_id'],'fake')

    def test_after_send_timeout_eof_bad_json_id_and_envelope_are_unknown(self):
        examples=[None,bridge_module._EOF,b'not-json\n',b'{"id":"wrong","result":{}}\n',
                  b'{"id":"1","result":{},"error":{}}\n',b'[]\n',b'{"id":"1","id":"1","result":{}}\n']
        for item in examples:
            with self.subTest(item=item):
                self.setUp()
                if item is not None:self.messages.put(item)
                self.fault('BRIDGE_TIMEOUT' if item is None else ('BRIDGE_UNAVAILABLE' if item is bridge_module._EOF else 'BRIDGE_PROTOCOL_ERROR'),True)
                self.assertGreater(len(self.stream.getvalue()),0)

    def test_after_send_valid_envelope_invalid_payload_is_unknown(self):
        for payload in (b'{"id":"1","result":[]}\n',b'{"id":"1","error":[]}\n',b'{"id":"1","error":{"code":"BAD"}}\n'):
            with self.subTest(payload=payload):
                self.setUp();self.messages.put(payload);self.fault('BRIDGE_PROTOCOL_ERROR',True)

    def test_partial_write_or_flush_failure_is_unknown(self):
        for phase in ('write','flush'):
            for error in (BrokenPipeError,OSError):
                with self.subTest(phase=phase,error=error.__name__):
                    self.setUp();stream=PartialStream(phase,error);self.process.stdin=stream;self.fault('BRIDGE_UNAVAILABLE',True)
                    self.assertGreater(len(stream.getvalue()),0)

    def test_pre_send_failures_and_definitive_backend_error_not_unknown(self):
        self.bridge._ensure_process.side_effect=BridgeFailure('BRIDGE_UNAVAILABLE','startup failed')
        self.fault('BRIDGE_UNAVAILABLE',False)
        self.setUp();self.marker.unlink();self.fault('BRIDGE_NOT_READY',False)
        self.setUp();self.process.poll=lambda:7;self.fault('BRIDGE_UNAVAILABLE',False)
        self.setUp()
        with self.assertRaises(BridgeFailure) as cm:self.bridge.call('operation.prepare_sweep',{'operation_id':'fake'},timeout_seconds=0)
        self.assertEqual(cm.exception.code,'BRIDGE_TIMEOUT');self.assertFalse(cm.exception.outcome_unknown);self.assertEqual(self.stream.getvalue(),b'')
        self.setUp();self.messages.put(b'{"id":"1","error":{"code":"INVALID_REQUEST","message":"rejected"}}\n');self.fault('INVALID_REQUEST',False)
        self.setUp();unconfigured=JsonlBridge(config_from_object({}))
        with self.assertRaises(BridgeFailure) as cm:unconfigured.call('operation.prepare_sweep',{})
        self.assertEqual(cm.exception.code,'BRIDGE_UNCONFIGURED');self.assertFalse(cm.exception.outcome_unknown)
        self.setUp();del self.bridge._ensure_process
        with patch('nx_sketch_mcp.bridge.subprocess.Popen',side_effect=OSError('not started')):
            self.fault('BRIDGE_UNAVAILABLE',False)


class StickyUnknownServerTests(unittest.TestCase):
    def setUp(self):
        RealJsonlFaultTests.setUp(self);self.server=McpServer(config_from_object({}));self.server._bridge=self.bridge;self.addCleanup(self.server.close)
        self.prepare={'operation_id':'prep','ownership_id':'owner','binding_epoch':1,'revision_id':'rev',
                      'sketch_id':'SKETCH(1)','path_curve_ids':['CURVE(1)'],'profile_output':'solid','orientation_mode':'transport','twist_angle_deg':0}
        self.verify={'operation_id':'verify','ownership_id':'owner','binding_epoch':1,'execute_operation_id':'execute'}
    def invoke(self,name,args):return self.server._tools_call(1,{'name':name,'arguments':args})
    def put(self,id,state,kind='prepare_m3_sweep'):
        self.messages.put((json.dumps({'id':str(id),'result':{'operation_id':'prep' if kind=='prepare_m3_sweep' else 'verify',
            'state':state,'kind':kind,'writes_halted':False}})+'\n').encode())
    def blocked(self):
        written=self.stream.getvalue()
        result=self.invoke('nx_prepare_sweep',{**self.prepare,'operation_id':'next'})
        self.assertEqual(result['result']['structuredContent']['error']['code'],'CLIENT_OUTCOME_UNKNOWN')
        result=self.invoke('nx_execute_plan',{'operation_id':'next-execute','ownership_id':'owner','binding_epoch':1,'plan_id':'plan'})
        self.assertEqual(result['result']['structuredContent']['error']['code'],'CLIENT_OUTCOME_UNKNOWN')
        self.assertEqual(self.stream.getvalue(),written)

    def test_pending_running_success_does_not_freeze(self):
        self.put(1,'QUEUED');self.invoke('nx_prepare_sweep',self.prepare)
        self.put(2,'RUNNING');self.invoke('nx_get_operation',{'operation_id':'prep'})
        self.put(3,'SUCCEEDED');self.invoke('nx_get_operation',{'operation_id':'prep'})
        self.assertEqual(self.server._unknown_operation_ids,set())

    def test_prepare_and_execute_initial_actual_timeouts_are_sticky(self):
        for tool,args,op in [('nx_prepare_sweep',self.prepare,'prep'),
                             ('nx_execute_plan',{'operation_id':'execute','ownership_id':'owner','binding_epoch':1,'plan_id':'plan'},'execute')]:
            with self.subTest(tool=tool):
                self.setUp();self.invoke(tool,args);self.assertIn(op,self.server._unknown_operation_ids);self.blocked()

    def test_verify_initial_actual_timeout_and_unknown_are_sticky(self):
        for mode in ('timeout','unknown'):
            with self.subTest(mode=mode):
                self.setUp()
                if mode=='unknown':self.put(1,'UNKNOWN','verify_part')
                self.invoke('nx_verify_part',self.verify);self.assertIn('verify',self.server._unknown_operation_ids);self.blocked()
                self.put(2,'SUCCEEDED','verify_part');self.invoke('nx_get_operation',{'operation_id':'verify'})
                self.assertIn('verify',self.server._unknown_operation_ids);self.blocked()

    def test_poll_actual_timeout_and_unknown_are_sticky(self):
        for mode in ('timeout','unknown'):
            with self.subTest(mode=mode):
                self.setUp();self.put(1,'QUEUED');self.invoke('nx_prepare_sweep',self.prepare)
                if mode=='unknown':self.put(2,'UNKNOWN')
                self.invoke('nx_get_operation',{'operation_id':'prep'});self.assertIn('prep',self.server._unknown_operation_ids);self.blocked()
                self.put(3,'SUCCEEDED');self.invoke('nx_get_operation',{'operation_id':'prep'})
                self.assertIn('prep',self.server._unknown_operation_ids);self.blocked()


if __name__=='__main__':unittest.main()
