"""Candidate F21 software protocol tests. Does not operate NX or claim NX support."""
from dataclasses import replace
from pathlib import Path
from types import SimpleNamespace
from unittest.mock import Mock
import io
import json
import math
import queue
import sys
import tempfile
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / 'src'))
from nx_sketch_mcp.bridge import BridgeFailure, JsonlBridge
from nx_sketch_mcp.config import BridgeConfig, config_from_object
from nx_sketch_mcp.server import McpServer, TOOLS

FIELDS = {'operation_id','ownership_id','binding_epoch','revision_id','sketch_id','path_curve_ids','profile_output','orientation_mode','twist_angle_deg'}


class SweepProtocolTests(unittest.TestCase):
    def setUp(self):
        self.server = McpServer(config_from_object({}))
        self.addCleanup(self.server.close)
        self.bridge = Mock(return_value={'state':'PREPARED'})
        self.server._bridge.call = self.bridge
        self.req = {'operation_id':'op','ownership_id':'owner','binding_epoch':1,'revision_id':'rev',
                    'sketch_id':'SKETCH(1)','path_curve_ids':['CURVE(2)','CURVE(1)'],
                    'profile_output':'solid','orientation_mode':'transport','twist_angle_deg':0}

    def invoke(self, req):
        return self.server._tools_call(1, {'name':'nx_prepare_sweep','arguments':req})

    def test_schema_required_and_ordered_ids(self):
        tools = [t for t in TOOLS if t['name']=='nx_prepare_sweep']
        self.assertEqual(len(tools),1)
        schema = tools[0]['inputSchema']
        self.assertEqual(set(schema['required']),FIELDS)
        self.assertEqual(set(schema['properties']),FIELDS)
        self.assertFalse(schema['additionalProperties'])
        self.assertEqual(set(schema['properties']['orientation_mode']['enum']),{'transport','linear_twist'})
        self.assertTrue(schema['properties']['path_curve_ids']['uniqueItems'])
        self.assertFalse(self.invoke(self.req)['result'].get('isError',False))
        self.bridge.assert_called_once_with('operation.prepare_sweep',self.req)
        self.assertEqual(self.bridge.call_args.args[1]['path_curve_ids'],['CURVE(2)','CURVE(1)'])

    def test_actual_jsonl_allowlist_and_serialization(self):
        with tempfile.TemporaryDirectory() as temp:
            marker = Path(temp)/'marker'; marker.touch()
            bridge = JsonlBridge(BridgeConfig('local-test-pipe',marker,Path(temp)/'unused-helper'))
            messages = queue.Queue()
            messages.put(b'{"id":"1","result":{"state":"PREPARED"}}\n')
            stream = io.BytesIO()
            process = SimpleNamespace(stdin=stream,poll=lambda:None)
            bridge._ensure_process = Mock(return_value=(process,messages))
            self.server._bridge = bridge
            self.assertFalse(self.invoke(self.req)['result'].get('isError',False))
            wire = stream.getvalue()
            self.assertTrue(wire.endswith(b'\n'))
            record=json.loads(wire)
            self.assertEqual(record['command'],'operation.prepare_sweep')
            self.assertEqual(record['params'],self.req)
            self.assertEqual(record['params']['path_curve_ids'],['CURVE(2)','CURVE(1)'])

    def test_missing_extra_and_invalid_fields_rejected_locally(self):
        bad = [{k:v for k,v in self.req.items() if k!=missing} for missing in self.req]
        bad += [{**self.req,'extra':1}]
        for value in ([],['same','same'],[str(i) for i in range(33)],[''],[1],None):
            bad.append({**self.req,'path_curve_ids':value})
        for name,value in [('binding_epoch',True),('sketch_id',' '),('profile_output','wire'),
                           ('orientation_mode','unsupported'),('twist_angle_deg',True),('twist_angle_deg',math.inf),
                           ('twist_angle_deg',math.nan),('twist_angle_deg',91),('twist_angle_deg',-91),('twist_angle_deg',1)]:
            bad.append({**self.req,name:value})
        for request in bad:
            with self.subTest(request=request):
                self.assertEqual(self.invoke(request).get('error',{}).get('code'),-32602)
        self.bridge.assert_not_called()

    def test_accepted_boundaries_and_sheet(self):
        for request in ({**self.req,'path_curve_ids':['only'],'profile_output':'sheet'},
                        {**self.req,'path_curve_ids':[str(i) for i in range(32)],'orientation_mode':'linear_twist','twist_angle_deg':-90},
                        {**self.req,'orientation_mode':'linear_twist','twist_angle_deg':90}):
            with self.subTest(request=request):
                self.assertFalse(self.invoke(request)['result'].get('isError',False))
                self.bridge.assert_called_with('operation.prepare_sweep',request)

    def test_unknown_stops_new_writes(self):
        for outcome in (BridgeFailure('BRIDGE_TIMEOUT','lost',outcome_unknown=True),{'state':'UNKNOWN'}):
            self.server._unknown_operation_ids.clear()
            self.bridge.reset_mock(side_effect=True,return_value=True)
            if isinstance(outcome,Exception): self.bridge.side_effect=outcome
            else: self.bridge.return_value=outcome
            self.invoke(self.req)
            result=self.invoke({**self.req,'operation_id':'next'})
            self.assertEqual(result['result']['structuredContent']['error']['code'],'CLIENT_OUTCOME_UNKNOWN')
            self.assertEqual(self.bridge.call_count,1)

    def test_capability_respects_unknown(self):
        self.assertFalse(self.server._capabilities()['model_sweep_adapter_available'])
        self.server._config=replace(self.server._config,pipe_name='local-test-pipe')
        self.bridge.return_value={'ownership_state':'AI_OWNED','model_read_available':True,'model_write_enabled':True,
                                  'writes_halted':False,'model_sweep_adapter_available':True,'model_sweep_write_enabled':True}
        self.assertTrue(self.server._capabilities()['model_sweep_write_enabled'])
        self.server._unknown_operation_ids.add('op')
        self.assertFalse(self.server._capabilities()['model_sweep_write_enabled'])
        self.assertFalse(self.server._with_local_write_halt(self.bridge.return_value)['model_sweep_write_enabled'])


if __name__=='__main__': unittest.main()
