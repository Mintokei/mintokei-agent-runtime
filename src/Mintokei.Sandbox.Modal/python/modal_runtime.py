#!/usr/bin/env python3
"""Modal SDK bridge. Trusted host inputs on stdin; bounded, sanitized results on stdout.

Embedders may supply an image factory and prepare/before_stop hooks. Hooks run on the
trusted coordinator and cannot replace the ownership, network or resource checks.
"""
import json
import math
from pathlib import Path
import re
import sys
import modal
from modal_native_claude import NativeClaude

NAME_TAG = 'mintokei_sandbox_name'
USER = ['/usr/bin/setpriv', '--reuid=10001', '--regid=10001', '--init-groups', '--no-new-privs']


def image(bundle):
    root = Path(bundle).resolve()
    if not (root / 'runner' / 'Mintokei.Runner').is_file():
        raise ValueError('missing_bundle')
    return (base_image().add_local_dir(str(root), '/opt/mintokei', copy=True)
            .run_commands('chmod +x /opt/mintokei/runner/Mintokei.Runner'))


def base_image():
    return (modal.Image.from_registry('node:22-bookworm-slim', add_python='3.12')
            .apt_install('git', 'curl', 'ca-certificates', 'libicu72', 'libgssapi-krb5-2', 'util-linux', 'python3')
            .run_commands('npm install -g @openai/codex@0.157.1 @anthropic-ai/claude-code@2.1.283',
                'useradd -u 10001 -m -s /bin/bash agent && useradd -u 10002 -m -s /bin/bash workspace && '
                'mkdir -p /workspace/.tmp /data /native-home /native-private/home /control/.tmp && '
                'chown -R 10001:10001 /workspace /data /native-home /native-private /control/.tmp && '
                'chmod 700 /data /home/agent /native-private /control/.tmp')
            .env({'HOME': '/home/agent', 'PYTHONUNBUFFERED': '1', 'TMPDIR': '/workspace/.tmp',
                  'TMP': '/workspace/.tmp', 'TEMP': '/workspace/.tmp'}).entrypoint([]))


def network_policy(payload):
    domains = payload.get('networkDomains')
    if domains is None:
        return {}
    if not isinstance(domains, list) or not 1 <= len(domains) <= 36:
        raise ValueError('network_policy')
    if any(not isinstance(d, str) or len(d) > 253 or not re.fullmatch(
            r'(?:[a-zA-Z0-9](?:[a-zA-Z0-9-]{0,61}[a-zA-Z0-9])?\.)+[a-zA-Z]{2,63}', d) for d in domains):
        raise ValueError('network_policy')
    return {'outbound_domain_allowlist': domains, 'outbound_cidr_allowlist': []}


def resources(spec):
    limits = spec['limits']
    cpu = limits['cpuLimit']
    memory = limits['memoryLimitBytes']
    reserve_cpu = cpu if limits.get('cpuReserve') is None else limits['cpuReserve']
    reserve_memory = memory if limits.get('memoryReserveBytes') is None else limits['memoryReserveBytes']
    if any(type(v) not in (float, int) or not math.isfinite(v) or v <= 0
           for v in (cpu, memory, reserve_cpu, reserve_memory)) or reserve_cpu > cpu or reserve_memory > memory:
        raise ValueError('resource_limits')
    # Modal physical cores = two vCPU. Memory is an integral number of MiB.
    return {'cpu': (reserve_cpu / 2, cpu / 2),
            'memory': (math.ceil(reserve_memory / 1048576), math.ceil(memory / 1048576))}


def resolve(request, client):
    payload = request['payload']
    try:
        box = (modal.Sandbox.from_id(payload['id'], client=client) if payload.get('id') else
               modal.Sandbox.from_name(request['appName'], payload['name'],
                   environment_name=request['environment'], client=client))
    except modal.exception.NotFoundError:
        return None
    if box.get_tags().get(request['ownerTag']) != request['ownerId']:
        box.detach()
        raise ValueError('ownership')
    return box


def main(request, image_factory=image, prepare=None, before_stop=None):
    if request.get('version') != 1:
        raise ValueError('protocol_version')
    credential = request['credential']
    client = modal.Client.from_credentials(credential['tokenId'], credential['tokenSecret'])
    payload = request.get('payload') or {}
    operation = request['operation']
    native = NativeClaude(request.get('resourcePrefix', 'mintokei'), request['ownerTag'])
    if operation.startswith('native_'):
        return native.handle(request, client, request['appName'], lambda: image_factory(request['bundle']))
    if operation == 'launch':
        spec = payload['spec']
        network = network_policy(payload)
        limits = resources(spec)
        options = {'args': USER + ['/opt/mintokei/runner/Mintokei.Runner', '--data-dir', '/data', *spec['args']],
                   'env': {**spec['env'], 'Runner__DiscoverModels': 'false'}, 'volumes': {},
                   'tags': {}, 'workdir': '/workspace'}
        if prepare:
            prepare(request, client, options)
        else:
            native.prepare_run(request, client, options)
        # Hooks can add application tags, but cannot replace infrastructure ownership or naming.
        options['tags'].update({request['ownerTag']: request['ownerId'], NAME_TAG: spec['name']})
        app = modal.App.lookup(request['appName'], client=client,
            environment_name=request['environment'], create_if_missing=True)
        box = modal.Sandbox.create(*options['args'], app=app, client=client, image=image_factory(request['bundle']),
            name=spec['name'], tags=options['tags'], env=options['env'], volumes=options['volumes'],
            timeout=request['timeout'], workdir=options['workdir'], **limits, **network)
        try:
            return {'id': box.object_id, 'state': 'running', 'networkRestricted': bool(network),
                    'workspaceIsolated': options.get('workspaceIsolated', False)}
        finally:
            box.detach()
    if operation == 'list':
        try:
            app = modal.App.lookup(request['appName'], client=client, environment_name=request['environment'])
        except modal.exception.NotFoundError:
            return {'sandboxes': []}
        result = []
        for box in modal.Sandbox.list(app_id=app.app_id, tags={request['ownerTag']: request['ownerId']}, client=client):
            try:
                name = box.get_tags().get(NAME_TAG)
                if name:  # Sign-in sandboxes have their own lifecycle, outside the runner inventory.
                    result.append({'id': box.object_id, 'name': name})
            finally:
                box.detach()
        return {'sandboxes': result}
    if operation not in ('status', 'stop'):
        raise ValueError('operation')
    box = resolve(request, client)
    if box is None:
        return {'state': 'not_found'}
    try:
        if operation == 'stop':
            if before_stop:
                before_stop(box)
            else:
                native.before_stop(box)
            box.terminate(wait=True)
        code = box.poll()
        return {'id': box.object_id, 'state': 'running' if code is None else 'exited', 'exitCode': code}
    finally:
        box.detach()


def serve(handler=main):
    try:
        raw = sys.stdin.buffer.read(65537)
        if len(raw) > 65536:
            raise ValueError('request_size')
        result = json.dumps(handler(json.loads(raw)))
        if len(result) > 65536:
            raise ValueError('response_size')
        print(result)
    except Exception as error:
        name = type(error).__name__
        category = ('authentication' if name in ('AuthError', 'PermissionDeniedError', 'UnauthenticatedError')
                    else 'configuration' if isinstance(error, ValueError) else 'provider')
        print(json.dumps({'error': category}))
        sys.exit(1)


if __name__ == '__main__':
    serve()
