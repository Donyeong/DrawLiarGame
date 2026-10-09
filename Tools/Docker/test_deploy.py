import importlib.util
import io
import json
import hashlib
import os
import pathlib
import sys
import tarfile
import tempfile
import types
import unittest
from unittest import mock

if sys.platform == 'win32':
    sys.modules.setdefault('fcntl', types.SimpleNamespace())
    sys.modules.setdefault('pwd', types.SimpleNamespace())
spec = importlib.util.spec_from_file_location('drawliar_deploy', pathlib.Path(__file__).with_name('deploy.py'))
deploy = importlib.util.module_from_spec(spec)
spec.loader.exec_module(deploy)


class DeploymentTests(unittest.TestCase):
    def package(self, root, entries):
        path = root / 'upload.tar.gz'
        with tarfile.open(path, 'w:gz') as archive:
            for name, value in entries:
                member = tarfile.TarInfo(name)
                if isinstance(value, bytes):
                    member.size = len(value)
                    archive.addfile(member, io.BytesIO(value))
                else:
                    member.type = value[0]
                    member.linkname = '/etc/passwd'
                    archive.addfile(member)
        return path

    def test_delivery_metadata_is_never_extracted_or_used(self):
        with tempfile.TemporaryDirectory() as directory:
            root = pathlib.Path(directory)
            package = self.package(root, [('./Dockerfile', b'RUN dangerous'), ('compose.yaml', b'privileged: true'), ('Apps/MainServer/DrawLiar.MainServer.dll', b'app')])
            context = root / 'context'
            deploy.extract_apps(package, context)
            self.assertEqual((context / 'Apps/MainServer/DrawLiar.MainServer.dll').read_bytes(), b'app')
            self.assertEqual({path.name for path in context.iterdir()}, {'Apps'})

    def test_traversal_links_and_unexpected_root_files_are_rejected(self):
        for name, value in [('Apps/../../outside', b'bad'), ('/etc/passwd', b'bad'), ('payload.tar.gz', b'bad'), ('Apps/link', (tarfile.SYMTYPE,)), ('Apps/hard', (tarfile.LNKTYPE,))]:
            with self.subTest(name=name), tempfile.TemporaryDirectory() as directory:
                root = pathlib.Path(directory)
                with self.assertRaises(RuntimeError):
                    deploy.extract_apps(self.package(root, [(name, value)]), root / 'context')
                self.assertFalse((root / 'outside').exists())

    def test_duplicate_file_and_extraction_size_limit_are_rejected(self):
        with tempfile.TemporaryDirectory() as directory:
            root = pathlib.Path(directory)
            package = self.package(root, [('Apps/file', b'a'), ('Apps/file', b'b')])
            with self.assertRaises(RuntimeError):
                deploy.extract_apps(package, root / 'duplicate')
            with mock.patch.object(deploy, 'MAX_PAYLOAD_BYTES', 1):
                package = self.package(root, [('Apps/file', b'aa')])
                with self.assertRaises(RuntimeError):
                    deploy.extract_apps(package, root / 'oversized')

    def test_compose_supports_array_and_ndjson(self):
        rows = [{'Service': 'main', 'Health': 'healthy'}, {'Service': 'db', 'Health': 'healthy'}]
        self.assertEqual(deploy.compose_rows(json.dumps(rows)), rows)
        self.assertEqual(deploy.compose_rows('\n'.join(json.dumps(row) for row in rows)), rows)

    def test_admin_delivery_rejects_other_services(self):
        with tempfile.TemporaryDirectory() as directory:
            root = pathlib.Path(directory)
            package = self.package(root, [('Apps/AdminServer/DrawLiar.AdminServer.dll', b'admin'), ('Apps/GameServer/DrawLiar.GameServer.dll', b'game')])
            with self.assertRaisesRegex(RuntimeError, '범위'):
                deploy.extract_apps(package, root / 'context', ('AdminServer',))
            self.assertFalse((root / 'context/Apps/GameServer').exists())

    def test_admin_delivery_requires_binaries_and_ui_assets(self):
        with tempfile.TemporaryDirectory() as directory:
            root = pathlib.Path(directory)
            admin = root / 'Apps/AdminServer'
            admin.mkdir(parents=True)
            for name in ('DrawLiar.AdminServer.dll', 'DrawLiar.AdminServer.deps.json', 'DrawLiar.AdminServer.runtimeconfig.json', 'DrawLiar.Shared.dll'):
                (admin / name).write_bytes(b'app')
            with self.assertRaisesRegex(RuntimeError, 'UI'):
                deploy.validate_apps(root, ('AdminServer',))
            (admin / 'wwwroot').mkdir()
            for name in ('index.html', 'console.js', 'console.css'):
                (admin / 'wwwroot' / name).write_bytes(b'asset')
            deploy.validate_apps(root, ('AdminServer',))
            (admin / 'DrawLiar.Shared.dll').unlink()
            with self.assertRaisesRegex(RuntimeError, '서버'):
                deploy.validate_apps(root, ('AdminServer',))

    def test_compose_keeps_game_image_separate_from_admin_override(self):
        with mock.patch.object(deploy, 'run') as run:
            deploy.compose(pathlib.Path('/compose.yaml'), 'game-image', 'up', '-d', '--no-deps', 'admin', admin_image='admin-image')
            environment = run.call_args.kwargs['env']
            self.assertEqual(environment['DRAWLIAR_IMAGE'], 'game-image')
            self.assertEqual(environment['DRAWLIAR_ADMIN_IMAGE'], 'admin-image')
            self.assertEqual(run.call_args.args[-4:], ('up', '-d', '--no-deps', 'admin'))
            deploy.compose(pathlib.Path('/compose.yaml'), 'new-full-image', 'up', '-d')
            self.assertEqual(run.call_args.kwargs['env']['DRAWLIAR_ADMIN_IMAGE'], 'new-full-image')

    def test_dedicated_delivery_rejects_other_services(self):
        for service in ('MainServer', 'GameServer', 'AdminServer'):
            with self.subTest(service=service), tempfile.TemporaryDirectory() as directory:
                root = pathlib.Path(directory)
                package = self.package(root, [('Apps/DedicatedServer/DrawLiar.DedicatedServer.dll', b'app'), ('Apps/'+service+'/outside.dll', b'bad')])
                with self.assertRaisesRegex(RuntimeError, '범위'):
                    deploy.extract_apps(package, root / 'context', ('DedicatedServer',))
                self.assertFalse((root / 'context/Apps' / service).exists())

    def test_compose_preserves_dedicated_override_during_admin_rollout(self):
        with tempfile.TemporaryDirectory() as directory:
            root = pathlib.Path(directory)
            (root/'current-dedicated-image').write_text('live-dedicated\n')
            with mock.patch.object(deploy, 'BASE', root), mock.patch.object(deploy, 'run') as run:
                deploy.compose(root/'compose.yaml', 'live-core', 'up', '-d', '--no-deps', 'admin', admin_image='new-admin')
                self.assertEqual(run.call_args.kwargs['env']['DRAWLIAR_DEDICATED_IMAGE'], 'live-dedicated')
                self.assertEqual(run.call_args.kwargs['env']['DRAWLIAR_ADMIN_IMAGE'], 'new-admin')
                deploy.compose(root/'compose.yaml', 'new-core', 'up', '-d', dedicated_image='new-core')
                self.assertEqual(run.call_args.kwargs['env']['DRAWLIAR_DEDICATED_IMAGE'], 'new-core')

    def test_dedicated_rollout_only_recreates_dedicated_and_commits_its_state(self):
        with tempfile.TemporaryDirectory() as directory:
            root = pathlib.Path(directory)
            (root/'current-image').write_text('live-core\n')
            (root/'current-admin-image').write_text('live-admin\n')
            with mock.patch.object(deploy, 'BASE', root), mock.patch.object(deploy, 'compose') as compose, mock.patch.object(deploy, 'wait_healthy') as wait:
                deploy.deploy_dedicated(root/'compose.yaml', 'new-dedicated', 'live-core', 'live-admin', 'old-dedicated')
                compose.assert_called_once_with(root/'compose.yaml', 'live-core', 'up', '-d', '--no-deps', 'dedicated', admin_image='live-admin', dedicated_image='new-dedicated')
                wait.assert_called_once_with(root/'compose.yaml', 'live-core', ('dedicated',), admin_image='live-admin', dedicated_image='new-dedicated')
                self.assertEqual((root/'current-image').read_text(), 'live-core\n')
                self.assertEqual((root/'current-admin-image').read_text(), 'live-admin\n')
                self.assertEqual((root/'current-dedicated-image').read_text(), 'new-dedicated\n')

    def test_failed_dedicated_rollout_restores_only_dedicated_without_state_changes(self):
        with tempfile.TemporaryDirectory() as directory:
            root = pathlib.Path(directory)
            for file, image in (('current-image','live-core'), ('current-admin-image','live-admin'), ('current-dedicated-image','old-dedicated')):
                (root/file).write_text(image+'\n')
            with mock.patch.object(deploy, 'BASE', root), mock.patch.object(deploy, 'compose') as compose, mock.patch.object(deploy, 'wait_healthy', side_effect=[RuntimeError('not ready'), None]):
                with self.assertRaisesRegex(RuntimeError, 'not ready'):
                    deploy.deploy_dedicated(root/'compose.yaml', 'new-dedicated', 'live-core', 'live-admin', 'old-dedicated')
                self.assertEqual(compose.call_args_list, [
                    mock.call(root/'compose.yaml', 'live-core', 'up', '-d', '--no-deps', 'dedicated', admin_image='live-admin', dedicated_image='new-dedicated'),
                    mock.call(root/'compose.yaml', 'live-core', 'up', '-d', '--no-deps', 'dedicated', admin_image='live-admin', dedicated_image='old-dedicated')])
                self.assertEqual((root/'current-image').read_text(), 'live-core\n')
                self.assertEqual((root/'current-admin-image').read_text(), 'live-admin\n')
                self.assertEqual((root/'current-dedicated-image').read_text(), 'old-dedicated\n')

    def test_dedicated_main_never_builds_or_restarts_other_apps_or_database(self):
        with tempfile.TemporaryDirectory() as directory:
            root = pathlib.Path(directory)
            base, releases, templates = root/'base', root/'releases', root/'templates'
            base.mkdir(); templates.mkdir()
            (base/'compose.yaml').write_text('old-compose'); (templates/'compose.yaml').write_text('new-compose')
            (base/'current-image').write_text('live-core\n'); (base/'current-admin-image').write_text('live-admin\n')
            (base/'.env').write_text('protected-environment\n')
            entries = [('Apps/DedicatedServer/'+name,b'app') for name in ('DrawLiar.DedicatedServer.dll','DrawLiar.DedicatedServer.deps.json','DrawLiar.DedicatedServer.runtimeconfig.json','DrawLiar.Shared.dll')]
            payload = self.package(root, entries).read_bytes()
            release='release-20261009000000-1234abcd'
            real_open=open
            def open_lock(path,*args,**kwargs):
                return io.StringIO() if path=='/var/lock/drawliar-deploy.lock' else real_open(path,*args,**kwargs)
            def upload(incoming,archive,owner,checksum): archive.write_bytes(payload)
            def compose_result(path,image,*args,**kwargs): return types.SimpleNamespace(stdout=json.dumps([{'Service':'dedicated','Health':'healthy'}]))
            with mock.patch.object(deploy,'BASE',base), mock.patch.object(deploy,'RELEASES',releases), mock.patch.object(deploy,'TEMPLATES',templates), mock.patch.object(sys,'argv',['deploy.py',release,'0'*64,'dedicated']), mock.patch.object(os,'geteuid',return_value=0,create=True), mock.patch.object(deploy.fcntl,'flock',create=True), mock.patch.object(deploy.fcntl,'LOCK_EX',1,create=True), mock.patch.object(deploy.fcntl,'LOCK_NB',2,create=True), mock.patch.object(deploy.pwd,'getpwnam',return_value=types.SimpleNamespace(pw_uid=1),create=True), mock.patch('builtins.open',side_effect=open_lock), mock.patch.object(deploy,'require_templates'), mock.patch.object(deploy,'copy_upload',side_effect=upload), mock.patch.object(deploy,'run') as run, mock.patch.object(deploy,'compose',side_effect=compose_result) as compose, mock.patch.object(pathlib.Path,'unlink') as unlink:
                deploy.main()
                run.assert_called_once_with('docker','build','-f',str(templates/'Dockerfile.dedicated'),'--build-arg','DRAWLIAR_BASE_IMAGE=live-core','-t','drawliar-app:'+release,str(releases/release/'context'))
                self.assertEqual(compose.call_args_list,[
                    mock.call(base/'compose.yaml','live-core','up','-d','--no-deps','dedicated',admin_image='live-admin',dedicated_image='drawliar-app:'+release),
                    mock.call(base/'compose.yaml','live-core','ps','--format','json',admin_image='live-admin',dedicated_image='drawliar-app:'+release,capture_output=True,text=True)])
                self.assertEqual((base/'current-image').read_text(),'live-core\n')
                self.assertEqual((base/'current-admin-image').read_text(),'live-admin\n')
                self.assertEqual((base/'current-dedicated-image').read_text(),'drawliar-app:'+release+'\n')
                self.assertEqual((base/'.env').read_text(),'protected-environment\n')
                self.assertFalse((base/'backups').exists())
                unlink.assert_called_once()

    def test_admin_rollout_only_recreates_admin_and_commits_admin_state(self):
        with tempfile.TemporaryDirectory() as directory:
            root = pathlib.Path(directory)
            (root / 'current-image').write_text('game-image\n')
            with mock.patch.object(deploy, 'BASE', root), mock.patch.object(deploy, 'compose') as compose, mock.patch.object(deploy, 'wait_healthy') as wait:
                deploy.deploy_admin(root / 'compose.yaml', 'new-admin', 'game-image', 'old-admin')
                compose.assert_called_once_with(root / 'compose.yaml', 'game-image', 'up', '-d', '--no-deps', 'admin', admin_image='new-admin')
                wait.assert_called_once_with(root / 'compose.yaml', 'game-image', ('admin',), admin_image='new-admin')
                self.assertEqual((root / 'current-image').read_text(), 'game-image\n')
                self.assertEqual((root / 'current-admin-image').read_text(), 'new-admin\n')

    def test_failed_admin_rollout_restores_previous_admin_without_state_changes(self):
        with tempfile.TemporaryDirectory() as directory:
            root = pathlib.Path(directory)
            (root / 'current-image').write_text('game-image\n')
            (root / 'current-admin-image').write_text('old-admin\n')
            with mock.patch.object(deploy, 'BASE', root), mock.patch.object(deploy, 'compose') as compose, mock.patch.object(deploy, 'wait_healthy', side_effect=[RuntimeError('not ready'), None]):
                with self.assertRaisesRegex(RuntimeError, 'not ready'):
                    deploy.deploy_admin(root / 'compose.yaml', 'new-admin', 'game-image', 'old-admin')
                self.assertEqual(compose.call_args_list, [
                    mock.call(root / 'compose.yaml', 'game-image', 'up', '-d', '--no-deps', 'admin', admin_image='new-admin'),
                    mock.call(root / 'compose.yaml', 'game-image', 'up', '-d', '--no-deps', 'admin', admin_image='old-admin')])
                self.assertEqual((root / 'current-image').read_text(), 'game-image\n')
                self.assertEqual((root / 'current-admin-image').read_text(), 'old-admin\n')

    def test_admin_health_requires_admin_healthy_and_ignores_other_services(self):
        rows = [{'Service': 'admin', 'Health': 'starting'}, {'Service': 'game', 'Health': 'healthy'}]
        with mock.patch.object(deploy, 'compose', side_effect=[types.SimpleNamespace(stdout=json.dumps(rows)), types.SimpleNamespace(stdout=json.dumps([{'Service': 'admin', 'Health': 'healthy'}, {'Service': 'game', 'Health': 'unhealthy'}]))]) as compose, mock.patch.object(deploy.time, 'sleep') as sleep:
            deploy.wait_healthy(pathlib.Path('/compose.yaml'), 'game-image', ('admin',), admin_image='admin-image')
            self.assertEqual(compose.call_count, 2)
            sleep.assert_called_once_with(5)

    def test_admin_main_never_builds_or_restarts_game_database(self):
        with tempfile.TemporaryDirectory() as directory:
            root = pathlib.Path(directory)
            base, releases, templates = root / 'base', root / 'releases', root / 'templates'
            base.mkdir()
            templates.mkdir()
            (base / 'compose.yaml').write_text('old-compose')
            (templates / 'compose.yaml').write_text('new-compose')
            (base / 'current-image').write_text('live-game\n')
            (base / '.env').write_text('protected-environment\n')
            entries = [('Apps/AdminServer/'+name, b'app') for name in ('DrawLiar.AdminServer.dll', 'DrawLiar.AdminServer.deps.json', 'DrawLiar.AdminServer.runtimeconfig.json', 'DrawLiar.Shared.dll')]
            entries += [('Apps/AdminServer/wwwroot/'+name, b'asset') for name in ('index.html', 'console.js', 'console.css')]
            payload = self.package(root, entries).read_bytes()
            release = 'release-20261009000000-1234abcd'
            real_open = open
            def open_lock(path, *args, **kwargs):
                return io.StringIO() if path == '/var/lock/drawliar-deploy.lock' else real_open(path, *args, **kwargs)
            def upload(incoming, archive, owner, checksum):
                archive.write_bytes(payload)
            def compose_result(path, image, *args, **kwargs):
                return types.SimpleNamespace(stdout=json.dumps([{'Service': 'admin', 'Health': 'healthy'}]))
            with mock.patch.object(deploy, 'BASE', base), mock.patch.object(deploy, 'RELEASES', releases), mock.patch.object(deploy, 'TEMPLATES', templates), mock.patch.object(sys, 'argv', ['deploy.py', release, '0'*64, 'admin']), mock.patch.object(os, 'geteuid', return_value=0, create=True), mock.patch.object(deploy.fcntl, 'flock', create=True), mock.patch.object(deploy.fcntl, 'LOCK_EX', 1, create=True), mock.patch.object(deploy.fcntl, 'LOCK_NB', 2, create=True), mock.patch.object(deploy.pwd, 'getpwnam', return_value=types.SimpleNamespace(pw_uid=1), create=True), mock.patch('builtins.open', side_effect=open_lock), mock.patch.object(deploy, 'require_templates'), mock.patch.object(deploy, 'copy_upload', side_effect=upload), mock.patch.object(deploy, 'run') as run, mock.patch.object(deploy, 'compose', side_effect=compose_result) as compose, mock.patch.object(pathlib.Path, 'unlink') as unlink:
                deploy.main()
                run.assert_called_once_with('docker', 'build', '-f', str(templates / 'Dockerfile.admin'), '--build-arg', 'DRAWLIAR_BASE_IMAGE=live-game', '-t', 'drawliar-app:'+release, str(releases / release / 'context'))
                self.assertEqual(compose.call_args_list, [
                    mock.call(base / 'compose.yaml', 'live-game', 'up', '-d', '--no-deps', 'admin', admin_image='drawliar-app:'+release),
                    mock.call(base / 'compose.yaml', 'live-game', 'ps', '--format', 'json', admin_image='drawliar-app:'+release, capture_output=True, text=True)])
                unlink.assert_called_once()
                self.assertEqual((base / 'current-image').read_text(), 'live-game\n')
                self.assertEqual((base / 'current-admin-image').read_text(), 'drawliar-app:'+release+'\n')
                self.assertEqual((base / '.env').read_text(), 'protected-environment\n')
                self.assertFalse((base / 'backups').exists())

    def test_unknown_deployment_mode_is_rejected(self):
        with mock.patch.object(sys, 'argv', ['deploy.py', 'release-20261009000000-1234abcd', '0'*64, 'game']), mock.patch.object(os, 'geteuid', return_value=0, create=True), mock.patch.object(deploy, 'run') as run:
            with self.assertRaisesRegex(RuntimeError, 'all 또는 admin'):
                deploy.main()
            run.assert_not_called()

    @unittest.skipUnless(hasattr(os, 'O_NOFOLLOW'), 'Linux 파일 디스크립터 검사')
    def test_upload_checks_symlink_owner_and_checksum(self):
        with tempfile.TemporaryDirectory() as directory:
            root = pathlib.Path(directory)
            incoming = root / 'incoming'
            incoming.write_bytes(b'upload')
            expected = hashlib.sha256(b'upload').hexdigest()
            deploy.copy_upload(incoming, root / 'good', os.getuid(), expected)
            self.assertEqual((root / 'good').read_bytes(), b'upload')
            with self.assertRaises(RuntimeError):
                deploy.copy_upload(incoming, root / 'wrong-owner', os.getuid() + 1, expected)
            with self.assertRaises(RuntimeError):
                deploy.copy_upload(incoming, root / 'wrong-hash', os.getuid(), '0' * 64)
            link = root / 'link'
            link.symlink_to(incoming)
            with self.assertRaises(OSError):
                deploy.copy_upload(link, root / 'symlink', os.getuid(), expected)


if __name__ == '__main__':
    unittest.main()
