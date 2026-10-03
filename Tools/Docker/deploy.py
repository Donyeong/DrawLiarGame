#!/usr/bin/python3
import fcntl, hashlib, json, os, pathlib, pwd, re, shutil, stat, subprocess, sys, tarfile, time
BASE = pathlib.Path('/srv/drawliar/dev')
RELEASES = pathlib.Path('/opt/drawliar/releases')
TEMPLATES = pathlib.Path('/opt/drawliar/deploy')
MAX_PAYLOAD_BYTES = 1024**3
DELIVERY_METADATA = {'Dockerfile', 'Dockerfile.postgres', 'postgres-entrypoint.sh', 'compose.yaml'}
def run(*args, **kwargs):
    return subprocess.run(args, check=True, **kwargs)
def compose(path, image, *args, **kwargs):
    env = os.environ.copy()
    env['DRAWLIAR_IMAGE'] = image
    return run('docker', 'compose', '--project-name', 'drawliar-dev', '--env-file', str(BASE / '.env'), '-f', str(path), *args, env=env, **kwargs)
def copy_upload(incoming, archive, uploader_uid, expected):
    descriptor = os.open(incoming, os.O_RDONLY | os.O_NOFOLLOW | os.O_NONBLOCK)
    with os.fdopen(descriptor, 'rb') as source:
        details = os.fstat(source.fileno())
        if not stat.S_ISREG(details.st_mode) or details.st_uid != uploader_uid or details.st_size > MAX_PAYLOAD_BYTES:
            raise RuntimeError('업로드 파일 소유자 또는 크기가 올바르지 않습니다.')
        digest = hashlib.sha256()
        total = 0
        with archive.open('xb') as output:
            while chunk := source.read(1024 * 1024):
                total += len(chunk)
                if total > MAX_PAYLOAD_BYTES:
                    raise RuntimeError('배포 파일 크기 초과')
                digest.update(chunk)
                output.write(chunk)
        if digest.hexdigest() != expected:
            raise RuntimeError('배포 파일 무결성 검사 실패')
def extract_apps(archive, context):
    context.mkdir(mode=0o700)
    with tarfile.open(archive, 'r:gz') as package:
        total = 0
        seen = set()
        for member in package:
            path = pathlib.PurePosixPath(member.name)
            if path.is_absolute() or '..' in path.parts or not (member.isdir() or member.isfile()):
                raise RuntimeError('안전하지 않은 압축 경로')
            if member.size < 0:
                raise RuntimeError('안전하지 않은 압축 크기')
            total += member.size
            if total > MAX_PAYLOAD_BYTES:
                raise RuntimeError('배포 파일 크기 초과')
            if not path.parts:
                if not member.isdir():
                    raise RuntimeError('안전하지 않은 압축 경로')
                continue
            if len(path.parts) == 1 and path.name in DELIVERY_METADATA and member.isfile():
                continue
            if path.parts[0] != 'Apps' or path in seen:
                raise RuntimeError('허용되지 않은 배포 파일')
            seen.add(path)
            target = context.joinpath(*path.parts)
            if member.isdir():
                target.mkdir(parents=True, exist_ok=True)
            else:
                target.parent.mkdir(parents=True, exist_ok=True)
                with package.extractfile(member) as source, target.open('xb') as output:
                    shutil.copyfileobj(source, output)
def compose_rows(snapshot):
    try:
        parsed = json.loads(snapshot)
        return parsed if isinstance(parsed, list) else [parsed]
    except json.JSONDecodeError:
        return [json.loads(line) for line in snapshot.splitlines() if line.strip()]
def require_templates():
    for directory in (TEMPLATES, *TEMPLATES.parents):
        details = directory.lstat()
        if not stat.S_ISDIR(details.st_mode) or details.st_uid != 0 or details.st_mode & 0o022:
            raise RuntimeError('배포 템플릿 경로는 root 소유의 쓰기 보호된 디렉터리여야 합니다.')
    for name in ('Dockerfile', 'Dockerfile.postgres', 'postgres-entrypoint.sh', 'compose.yaml'):
        path = TEMPLATES / name
        details = path.lstat()
        if not stat.S_ISREG(details.st_mode) or details.st_uid != 0 or details.st_mode & 0o022:
            raise RuntimeError('배포 템플릿은 root 소유의 쓰기 보호된 파일이어야 합니다.')
def main():
    if os.geteuid() != 0 or len(sys.argv) != 3:
        raise RuntimeError('root 배포 helper 인자가 필요합니다.')
    release, expected = sys.argv[1:]
    if not re.fullmatch(r'release-[0-9]{14}-[a-f0-9]{8}', release) or not re.fullmatch(r'[a-f0-9]{64}', expected):
        raise RuntimeError('배포 식별자 또는 체크섬이 올바르지 않습니다.')
    with open('/var/lock/drawliar-deploy.lock','w') as lock:
        fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
        incoming = pathlib.Path('/srv/drawliar/incoming') / (release + '.tar.gz')
        require_templates()
        RELEASES.mkdir(parents=True, exist_ok=True)
        destination = RELEASES / release
        destination.mkdir()
        archive = destination / 'payload.tar.gz'
        copy_upload(incoming, archive, pwd.getpwnam('kona-deploy').pw_uid, expected)
        context = destination / 'context'
        extract_apps(archive, context)
        for service in ('MainServer','GameServer','DedicatedServer','AdminServer'):
            for artifact in ('DrawLiar.'+service+'.dll', 'DrawLiar.'+service+'.deps.json', 'DrawLiar.'+service+'.runtimeconfig.json', 'DrawLiar.Shared.dll'):
                if not (context/'Apps'/service/artifact).is_file():
                    raise RuntimeError('필수 서버 산출물이 없습니다.')
        image = 'drawliar-app:' + release
        run('docker','build','--pull','-f',str(TEMPLATES/'Dockerfile'),'-t',image,str(context))
        present = subprocess.run(['docker','image','inspect','drawliar-postgres:16-noble'], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL).returncode == 0
        if not present:
            run('docker','build','-f',str(TEMPLATES/'Dockerfile.postgres'),'-t','drawliar-postgres:16-noble',str(TEMPLATES))
        current = BASE/'compose.yaml'
        previous_image = (BASE/'current-image').read_text().strip() if (BASE/'current-image').exists() else image
        if current.exists():
            compose(TEMPLATES/'compose.yaml', previous_image, 'stop','dedicated','game','admin','main')
            backup = BASE/'backups'/release
            backup.mkdir(parents=True, mode=0o700)
            with open(backup/'drawliar.sql','wb') as output:
                compose(TEMPLATES/'compose.yaml', previous_image, 'exec','-T','db','pg_dump','-U','drawliar','-d','drawliar', stdout=output)
        shutil.copyfile(TEMPLATES/'compose.yaml', current)
        try:
            compose(current,image,'up','-d','--remove-orphans')
            for attempt in range(36):
                snapshot = subprocess.check_output(['docker','compose','--project-name','drawliar-dev','--env-file',str(BASE/'.env'),'-f',str(current),'ps','--format','json'], env=dict(os.environ,DRAWLIAR_IMAGE=image), text=True)
                rows = compose_rows(snapshot)
                if {row.get('Service') for row in rows} == {'db','main','game','dedicated','admin'} and all(row.get('Health') == 'healthy' for row in rows):
                    (BASE/'current-image').write_text(image+'\n')
                    incoming.unlink()
                    print('DrawLiar 서버 4종 및 PostgreSQL 상태 검사 통과')
                    return
                time.sleep(5)
            raise RuntimeError('준비 검사 실패. 배포 로그와 DB 백업을 확인하세요.')
        except Exception:
            try:
                compose(current,image,'stop','dedicated','game','admin','main')
            except Exception:
                print('앱 중지에 실패했습니다. DrawLiar 컨테이너 상태를 확인하세요.', file=sys.stderr)
            raise
if __name__ == '__main__':
    main()
