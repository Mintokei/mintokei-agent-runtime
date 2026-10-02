"""Offline bridge contract tests. No Modal SDK, cloud account or provider credentials required."""
import copy
import importlib
from pathlib import Path
import sys
import types
import unittest
from unittest.mock import Mock, patch

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / 'src/Mintokei.Sandbox.Modal/python'))
with patch.dict(sys.modules, {'modal': Mock()}):
    runtime = importlib.import_module('modal_runtime')
    native_module = importlib.import_module('modal_native_claude')


class ModalRuntimeTests(unittest.TestCase):
    def setUp(self):
        self.modal = types.SimpleNamespace(Client=Mock(), App=Mock(), Sandbox=Mock(), Volume=Mock(),
                                          exception=types.SimpleNamespace(NotFoundError=KeyError))
        self.patches = [patch.object(runtime, 'modal', self.modal), patch.object(native_module, 'modal', self.modal)]
        for p in self.patches: p.start(); self.addCleanup(p.stop)
        self.request = {'version': 1, 'operation': 'launch', 'credential': {'tokenId': 'fixture-id', 'tokenSecret': 'fixture-secret'},
            'ownerId': 'owner-one', 'ownerTag': 'mintokei_owner', 'appName': 'agents-one', 'environment': 'testing',
            'resourcePrefix': 'mintokei', 'bundle': '/fixture', 'timeout': 3600,
            'payload': {'spec': {'name': 'job-one', 'env': {}, 'args': [],
                                'limits': {'cpuLimit': 4, 'cpuReserve': 2, 'memoryLimitBytes': 4294967296}}}}
        self.box = self.modal.Sandbox.create.return_value
        self.box.object_id = 'sb-one'

    def call(self):
        return runtime.main(self.request, image_factory=lambda _: Mock())

    def test_launch_maps_resources_and_disables_account_discovery(self):
        self.assertEqual(self.call()['id'], 'sb-one')
        options = self.modal.Sandbox.create.call_args.kwargs
        self.assertEqual(options['cpu'], (1, 2))
        self.assertEqual(options['memory'], (4096, 4096))
        self.assertEqual(options['env']['Runner__DiscoverModels'], 'false')
        self.assertNotIn('fixture-secret', repr(options))
        self.assertEqual(options['tags']['mintokei_owner'], 'owner-one')
        self.box.detach.assert_called_once()

    def test_invalid_policy_or_resources_never_creates_a_sandbox(self):
        for domains in [[], ['*'], ['127.0.0.1'], ['https://example.test'], ['example.test\n']]:
            self.request['payload']['networkDomains'] = domains
            with self.assertRaises(ValueError): self.call()
        self.request['payload'].pop('networkDomains')
        self.request['payload']['spec']['limits']['cpuReserve'] = 0
        with self.assertRaises(ValueError): self.call()
        self.modal.Sandbox.create.assert_not_called()

    def test_policy_sdk_rejection_does_not_retry_without_restrictions(self):
        self.request['payload']['networkDomains'] = ['api.example.test']
        self.modal.Sandbox.create.side_effect = TypeError('SDK does not support policy')
        with self.assertRaises(TypeError): self.call()
        self.modal.Sandbox.create.assert_called_once()
        self.assertEqual(self.modal.Sandbox.create.call_args.kwargs['outbound_cidr_allowlist'], [])

    def test_wrong_owner_cannot_be_stopped(self):
        self.request['operation'] = 'stop'; self.request['payload'] = {'id': 'sb-other', 'name': 'job-one'}
        other = self.modal.Sandbox.from_id.return_value
        other.get_tags.return_value = {'mintokei_owner': 'other'}
        with self.assertRaises(ValueError): self.call()
        other.exec.assert_not_called(); other.terminate.assert_not_called(); other.detach.assert_called_once()

    def test_inventory_uses_explicit_app_and_excludes_login_sandboxes(self):
        self.request['operation'] = 'list'
        self.modal.App.lookup.return_value.app_id = 'ap-testing'
        runner, login = Mock(object_id='sb-runner'), Mock(object_id='sb-login')
        runner.get_tags.return_value = {runtime.NAME_TAG: 'job-one'}; login.get_tags.return_value = {}
        self.modal.Sandbox.list.return_value = [runner, login]
        self.assertEqual(self.call(), {'sandboxes': [{'id': 'sb-runner', 'name': 'job-one'}]})
        self.assertEqual(self.modal.Sandbox.list.call_args.kwargs['app_id'], 'ap-testing')
        self.assertEqual(self.modal.App.lookup.call_args.kwargs['environment_name'], 'testing')
        runner.detach.assert_called_once(); login.detach.assert_called_once()

    def test_native_controller_home_and_workspace_are_separate(self):
        account = '0123456789abcdef0123456789abcdef'
        self.request['payload'].update({'nativeAccount': {'accountId': account, 'home': 'mintokei-native-' + account},
            'isolateWorkspace': True, 'networkDomains': ['api.anthropic.com']})
        self.assertTrue(self.call()['workspaceIsolated'])
        options = self.modal.Sandbox.create.call_args.kwargs
        self.assertEqual(list(options['volumes']), ['/native-private/home'])
        self.assertEqual(options['workdir'], '/control')
        self.assertEqual(options['env']['HOME'], '/native-private/home')
        self.assertEqual(options['env']['TMPDIR'], '/control/.tmp')
        self.assertIn('chown -R 10002:10002 /workspace', self.modal.Sandbox.create.call_args.args[-1])

    def test_delete_checks_account_app_before_deleting_home(self):
        account = '0123456789abcdef0123456789abcdef'
        self.request['operation'] = 'native_delete'
        self.request['payload'] = {'accountId': account, 'home': 'mintokei-native-' + account}
        self.modal.App.lookup.return_value.app_id = 'ap-testing'
        self.modal.Sandbox.list.return_value = [self.box]
        with self.assertRaises(ValueError): self.call()
        self.modal.Volume.objects.delete.assert_not_called()
        self.assertEqual(self.modal.Sandbox.list.call_args.kwargs['app_id'], 'ap-testing')
        self.box.detach.assert_called_once()

    def test_native_sync_failure_keeps_termination_unconfirmed(self):
        native = native_module.NativeClaude()
        self.box.poll.return_value = None
        self.box.get_tags.return_value = {'mintokei_native_account': 'account', 'mintokei_workspace_isolated': 'v1'}
        with patch.object(native, 'execute', return_value=(1, 'private')):
            with self.assertRaises(ValueError): native.before_stop(self.box)
        self.box.terminate.assert_not_called()


if __name__ == '__main__':
    unittest.main()
