"""Native Claude lifecycle. No provider token is read, returned or proxied."""
import json
import re
import shlex
import time
from urllib.parse import quote

import modal

USER = ['/usr/bin/setpriv', '--reuid=10001', '--regid=10001', '--init-groups', '--no-new-privs']
NATIVE_ENV = {'HOME': '/native-home', 'CLAUDE_CONFIG_DIR': '/native-home/.claude',
              'CLAUDE_CODE_DISABLE_AUTO_MEMORY': '1'}
LOGIN = r'''
printf '\nKeep this terminal in one tab. Open the Claude authorization page separately.\n'
while true; do
  claude auth login --claudeai
  code=$?
  if [ "$code" -eq 0 ]; then
    sync "$HOME" || exit 1
    printf ready > /tmp/{prefix}-login-ready
    printf '\nSign-in complete. Return to the application; this terminal will close shortly.\n'
    sleep 1500
    exit 0
  fi
  printf '\nSign-in ended (exit %s). Press Enter to try again.\n' "$code"
  read -r retry || exit 1
done
'''



class NativeClaude:
    NATIVE_ENV = NATIVE_ENV

    def __init__(self, prefix='mintokei', owner_tag='mintokei_owner'):
        if not re.fullmatch(r'[a-z][a-z0-9-]{0,19}', prefix):
            raise ValueError('resource_prefix')
        self.prefix = prefix
        self.owner_tag = owner_tag

    def validate(self, payload):
        account = payload['accountId'].replace('-', '')
        if not re.fullmatch(r'[0-9a-f]{32}', account):
            raise ValueError('native_account')
        if payload['home'] != self.prefix + '-native-' + account:
            raise ValueError('native_home')

    def prepare_run(self, request, client, options):
        payload = request['payload']
        isolated = payload.get('isolateWorkspace') is True
        if isolated and (not payload.get('nativeAccount') or not payload.get('networkDomains')):
            raise ValueError('workspace_isolation')
        if not payload.get('nativeAccount'):
            return
        if payload.get('networkDomains') is not None and not isolated:
            raise ValueError('native_network_policy')
        native = payload['nativeAccount']
        home = '/native-private/home' if isolated else '/native-home'
        options['volumes'] = {home: self.volume(native, request['environment'], client)}
        options['env'].update({**NATIVE_ENV, 'HOME': home, 'CLAUDE_CONFIG_DIR': home + '/.claude'})
        options['tags'][self.prefix + '_native_account'] = native['accountId']
        options['args'] = self.prepare_command(options['args'], home)
        if isolated:
            options['tags'][self.prefix + '_workspace_isolated'] = 'v1'
            options['env'].update({'TMPDIR': '/control/.tmp', 'TMP': '/control/.tmp', 'TEMP': '/control/.tmp'})
            options['args'] = ['sh', '-c', 'chown -R 10002:10002 /workspace && chmod 700 /workspace /home/workspace && exec ' + shlex.join(options['args'])]
            options['workdir'] = '/control'
        options['workspaceIsolated'] = isolated

    def before_stop(self, box):
        tags = box.get_tags()
        if box.poll() is None and tags.get(self.prefix + '_native_account'):
            home = '/native-private/home' if tags.get(self.prefix + '_workspace_isolated') == 'v1' else '/native-home'
            code, _ = self.execute(box, 'sync', home)
            if code:
                raise ValueError('native_sync')


    def volume(self, payload, environment, client, *, create=False):
        self.validate(payload)
        return modal.Volume.from_name(payload['home'], create_if_missing=create, version=2,
                                      environment_name=environment, client=client)


    @staticmethod
    def prepare_command(args, home="/native-home"):
        # Empty mountpoint required; never mount over the image's populated OS home.
        if home not in ('/native-home', '/native-private/home'):
            raise ValueError('native_mount')
        return ['sh', '-c', 'chown 10001:10001 ' + home + ' && chmod 700 ' + home + ' && exec ' + shlex.join(args)]


    @staticmethod
    def terminal_image(base):
        return (base.run_commands(
            'curl -fsSL --proto "=https" --tlsv1.2 https://github.com/tsl0922/ttyd/releases/download/1.7.7/ttyd.x86_64 -o /usr/local/bin/ttyd',
            "echo '8a217c968aba172e0dbf3f34447218dc015bc4d5e59bf51db2f2cd12b7be4f55  /usr/local/bin/ttyd' | sha256sum -c -",
            'chmod 755 /usr/local/bin/ttyd').env(NATIVE_ENV))


    @staticmethod
    def execute(box, *args):
        p = box.exec(*USER, *args, timeout=15)
        p.stdin.write_eof()
        p.stdin.drain()
        output = p.stdout.read(); p.stderr.read()
        return p.wait(), output


    def handle(self, request, client, app_name, base_image):
        payload = request['payload']; self.validate(payload)
        environment = request['environment']; connection = request['ownerId']
        operation = request['operation']
        tags = {self.owner_tag: connection, self.prefix + '_native_account': payload['accountId']}
        if operation == 'native_delete':
            # Bind inventory to the same app/environment that owns this account's sessions.
            try:
                app = modal.App.lookup(app_name, client=client, environment_name=environment)
            except modal.exception.NotFoundError:
                app = None
            busy = False
            if app:
                for box in modal.Sandbox.list(app_id=app.app_id, tags=tags, client=client):
                    busy = True
                    box.detach()
            if busy: raise ValueError('native_home_busy')
            modal.Volume.objects.delete(payload['home'], allow_missing=True, environment_name=environment, client=client)
            return {'state': 'deleted'}
        if not re.fullmatch(self.prefix + r'-login-[0-9a-f]{32}', payload.get('name', '')):
            raise ValueError('native_login')
        if operation == 'native_start':
            seconds = min(1200, int(payload['expiresAt'] - time.time()))
            if seconds < 1:
                raise ValueError('native_login_expired')
            app = modal.App.lookup(app_name, client=client, environment_name=environment, create_if_missing=True)
            box = modal.Sandbox.create(*self.prepare_command(USER + ['ttyd', '-p', '7681', '-W', '-O', '-m', '1', '-d', '1',
                '-t', 'disableReconnect=true', '-t', 'titleFixed=Claude Code sign-in', 'sh', '-c', LOGIN.replace('{prefix}', self.prefix)]),
                app=app, client=client, image=self.terminal_image(base_image()), name=payload['name'],
                tags={**tags, self.prefix + '_native_login': payload['name']},
                volumes={'/native-home': self.volume(payload, environment, client, create=True)},
                timeout=seconds, workdir='/workspace', cpu=(1.0, 1.0), memory=(2048, 2048))
            result = {'id': box.object_id, 'state': 'running'}; box.detach(); return result
        try:
            box = modal.Sandbox.from_id(payload['id'], client=client) if payload.get('id') else modal.Sandbox.from_name(app_name, payload['name'], environment_name=environment, client=client)
        except modal.exception.NotFoundError:
            return {'state': 'not_found'}
        actual = box.get_tags()
        if any(actual.get(key) != value for key, value in {**tags, self.prefix + '_native_login': payload['name']}.items()):
            box.detach()
            raise ValueError('native_ownership')
        try:
            if operation == 'native_open':
                if time.time() >= payload['expiresAt'] or box.poll() is not None:
                    raise ValueError('native_login_expired')
                token = box.create_connect_token(port=7681)
                return {'url': token.url + '/?_modal_connect_token=' + quote(token.token, safe='')}
            if operation == 'native_close':
                if box.poll() is None:
                    # Volume v2 flushes native credential changes without reading them.
                    code, _ = self.execute(box, 'sync', '/native-home')
                    if code: raise ValueError('native_sync')
                    box.terminate(wait=True)
                return {'id': box.object_id, 'state': 'exited' if box.poll() is not None else 'unknown'}
            if operation == 'native_status':
                if box.poll() is not None: return {'id': box.object_id, 'state': 'exited'}
                code, _ = self.execute(box, 'test', '-f', '/tmp/' + self.prefix + '-login-ready')
                if code == 0:
                    code, output = self.execute(box, 'claude', 'auth', 'status')
                    auth = json.loads(output)
                    if code == 0 and auth.get('loggedIn') is True and auth.get('authMethod') == 'claude.ai':
                        return {'id': box.object_id, 'state': 'ready'}
                return {'id': box.object_id, 'state': 'running'}
            raise ValueError('native_operation')
        finally:
            box.detach()
