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
