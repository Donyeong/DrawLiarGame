#!/usr/bin/python3
import fcntl, hashlib, json, os, pathlib, pwd, re, shutil, stat, subprocess, sys, tarfile, time
BASE = pathlib.Path('/srv/drawliar/dev')
RELEASES = pathlib.Path('/opt/drawliar/releases')
TEMPLATES = pathlib.Path('/opt/drawliar/deploy')
MAX_PAYLOAD_BYTES = 1024**3
DELIVERY_METADATA = {'Dockerfile', 'Dockerfile.admin', 'Dockerfile.dedicated', 'Dockerfile.postgres', 'postgres-entrypoint.sh', 'compose.yaml'}
APP_SERVICES = ('MainServer', 'GameServer', 'DedicatedServer', 'AdminServer')
def run(*args, **kwargs):
    return subprocess.run(args, check=True, **kwargs)
def compose(path, image, *args, admin_image=None, dedicated_image=None, **kwargs):
    env = os.environ.copy()
    env['DRAWLIAR_IMAGE'] = image
    env['DRAWLIAR_ADMIN_IMAGE'] = admin_image or image
    dedicated_state = BASE/'current-dedicated-image'
    env['DRAWLIAR_DEDICATED_IMAGE'] = dedicated_image or (dedicated_state.read_text().strip() if dedicated_state.exists() else image)
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
def extract_apps(archive, context, services=APP_SERVICES):
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
            if len(path.parts) > 1 and path.parts[1] not in services:
                raise RuntimeError('배포 범위 밖의 서버 파일')
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
def validate_apps(context, services):
    for service in services:
        for artifact in ('DrawLiar.'+service+'.dll', 'DrawLiar.'+service+'.deps.json', 'DrawLiar.'+service+'.runtimeconfig.json', 'DrawLiar.Shared.dll'):
            if not (context/'Apps'/service/artifact).is_file():
                raise RuntimeError('필수 서버 산출물이 없습니다.')
    if 'AdminServer' in services:
        for asset in ('index.html', 'console.js', 'console.css'):
            if not (context/'Apps'/'AdminServer'/'wwwroot'/asset).is_file():
                raise RuntimeError('관리자 UI 산출물이 없습니다.')

def require_templates():
    for directory in (TEMPLATES, *TEMPLATES.parents):
        details = directory.lstat()
        if not stat.S_ISDIR(details.st_mode) or details.st_uid != 0 or details.st_mode & 0o022:
            raise RuntimeError('배포 템플릿 경로는 root 소유의 쓰기 보호된 디렉터리여야 합니다.')
    for name in ('Dockerfile', 'Dockerfile.admin', 'Dockerfile.dedicated', 'Dockerfile.postgres', 'postgres-entrypoint.sh', 'compose.yaml'):
        path = TEMPLATES / name
        details = path.lstat()
        if not stat.S_ISREG(details.st_mode) or details.st_uid != 0 or details.st_mode & 0o022:
            raise RuntimeError('배포 템플릿은 root 소유의 쓰기 보호된 파일이어야 합니다.')
def wait_healthy(path, image, services, admin_image=None, dedicated_image=None):
    for attempt in range(36):
        selected_image = {'dedicated_image': dedicated_image} if dedicated_image is not None else {}
        snapshot = compose(path, image, 'ps', '--format', 'json', admin_image=admin_image, **selected_image, capture_output=True, text=True).stdout
        rows = compose_rows(snapshot)
        selected = [row for row in rows if row.get('Service') in services]
        if {row.get('Service') for row in selected} == set(services) and all(row.get('Health') == 'healthy' for row in selected):
            return
        time.sleep(5)
    raise RuntimeError('준비 검사 실패. 배포 로그를 확인하세요.')

def deploy_admin(current, image, previous_image, previous_admin_image):
    try:
        compose(current, previous_image, 'up', '-d', '--no-deps', 'admin', admin_image=image)
        wait_healthy(current, previous_image, ('admin',), admin_image=image)
    except Exception:
        compose(current, previous_image, 'up', '-d', '--no-deps', 'admin', admin_image=previous_admin_image)
        wait_healthy(current, previous_image, ('admin',), admin_image=previous_admin_image)
        raise
    (BASE/'current-admin-image').write_text(image+'\n')

def deploy_dedicated(current, image, previous_image, previous_admin_image, previous_dedicated_image):
    try:
        compose(current, previous_image, 'up', '-d', '--no-deps', 'dedicated', admin_image=previous_admin_image, dedicated_image=image)
        wait_healthy(current, previous_image, ('dedicated',), admin_image=previous_admin_image, dedicated_image=image)
    except Exception:
        compose(current, previous_image, 'up', '-d', '--no-deps', 'dedicated', admin_image=previous_admin_image, dedicated_image=previous_dedicated_image)
        wait_healthy(current, previous_image, ('dedicated',), admin_image=previous_admin_image, dedicated_image=previous_dedicated_image)
        raise
    (BASE/'current-dedicated-image').write_text(image+'\n')

def require_no_active_games():
    sql = '''BEGIN READ ONLY;
SELECT count(*) FROM "Room" r JOIN "DedicatedNode" d ON d."NodeId"=r."NodeId"
WHERE r."IsInProgress" AND d."HeartbeatAt">now()-interval '30 seconds'
  AND r."UpdatedAt">now()-interval '90 seconds' AND r."PlayerCount"+r."SpectatorCount">0;
COMMIT;'''
    try:
        count = run('docker', 'exec', 'drawliar-dev-db-1', 'psql', '-U', 'drawliar', '-d', 'drawliar',
                    '-qAt', '-v', 'ON_ERROR_STOP=1', '-c', sql, capture_output=True, text=True).stdout.strip()
    except (subprocess.CalledProcessError, OSError):
        raise RuntimeError('진행 중인 경기 상태 조회에 실패했습니다. 기존 앱은 유지됩니다.') from None
    if not re.fullmatch(r'[0-9]+', count):
        raise RuntimeError('진행 중인 경기 상태를 확인할 수 없습니다. 기존 앱은 유지됩니다.')
    if int(count) != 0:
        raise RuntimeError('진행 중인 경기가 있습니다. 경기 종료 후 다시 배포하세요.')

def main():
    if os.geteuid() != 0 or len(sys.argv) not in (3, 4):
        raise RuntimeError('root 배포 helper 인자가 필요합니다.')
    release, expected = sys.argv[1:3]
    mode = sys.argv[3] if len(sys.argv) == 4 else 'all'
    if mode not in ('all', 'admin', 'dedicated'):
        raise RuntimeError('배포 범위는 all 또는 admin 또는 dedicated이어야 합니다.')
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
        services = ('AdminServer',) if mode == 'admin' else ('DedicatedServer',) if mode == 'dedicated' else APP_SERVICES
        extract_apps(archive, context, services)
        validate_apps(context, services)
        image = 'drawliar-app:' + release
        current = BASE/'compose.yaml'
        image_state = BASE/'current-image'
        admin_state = BASE/'current-admin-image'
        dedicated_state = BASE/'current-dedicated-image'
        if mode == 'admin':
            if not current.is_file() or not image_state.is_file():
                raise RuntimeError('관리자 단독 배포에는 기존 운영 배포가 필요합니다.')
            previous_image = image_state.read_text().strip()
            previous_admin_image = admin_state.read_text().strip() if admin_state.exists() else previous_image
            run('docker', 'build', '-f', str(TEMPLATES/'Dockerfile.admin'), '--build-arg', 'DRAWLIAR_BASE_IMAGE='+previous_image, '-t', image, str(context))
            shutil.copyfile(TEMPLATES/'compose.yaml', current)
            deploy_admin(current, image, previous_image, previous_admin_image)
            incoming.unlink()
            print('관리자 서버 상태 검사 통과. 게임 서버와 DB는 유지됩니다.')
            return
        if mode == 'dedicated':
            if not current.is_file() or not image_state.is_file():
                raise RuntimeError('데디케이티드 단독 배포에는 기존 운영 배포가 필요합니다.')
            previous_image = image_state.read_text().strip()
            previous_admin_image = admin_state.read_text().strip() if admin_state.exists() else previous_image
            previous_dedicated_image = dedicated_state.read_text().strip() if dedicated_state.exists() else previous_image
            run('docker', 'build', '-f', str(TEMPLATES/'Dockerfile.dedicated'), '--build-arg', 'DRAWLIAR_BASE_IMAGE='+previous_image, '-t', image, str(context))
            shutil.copyfile(TEMPLATES/'compose.yaml', current)
            deploy_dedicated(current, image, previous_image, previous_admin_image, previous_dedicated_image)
            incoming.unlink()
            print('데디케이티드 서버 상태 검사 통과. 메인·게임·관리자 서버와 DB는 유지됩니다.')
            return
        run('docker','build','--pull','-f',str(TEMPLATES/'Dockerfile'),'-t',image,str(context))
        present = subprocess.run(['docker','image','inspect','drawliar-postgres:16-noble'], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL).returncode == 0
        if not present:
            run('docker','build','-f',str(TEMPLATES/'Dockerfile.postgres'),'-t','drawliar-postgres:16-noble',str(TEMPLATES))
        previous_image = image_state.read_text().strip() if image_state.exists() else image
        previous_admin_image = admin_state.read_text().strip() if admin_state.exists() else previous_image
        if current.exists():
            require_no_active_games()
            compose(TEMPLATES/'compose.yaml', previous_image, 'stop','dedicated','game','admin','main', admin_image=previous_admin_image)
            backup = BASE/'backups'/release
            backup.mkdir(parents=True, mode=0o700)
            with open(backup/'drawliar.sql','wb') as output:
                compose(TEMPLATES/'compose.yaml', previous_image, 'exec','-T','db','pg_dump','-U','drawliar','-d','drawliar', admin_image=previous_admin_image, stdout=output)
        shutil.copyfile(TEMPLATES/'compose.yaml', current)
        try:
            compose(current,image,'up','-d','--remove-orphans', dedicated_image=image)
            wait_healthy(current, image, ('db','main','game','dedicated','admin'), dedicated_image=image)
            image_state.write_text(image+'\n')
            admin_state.write_text(image+'\n')
            dedicated_state.write_text(image+'\n')
            incoming.unlink()
            print('DrawLiar 서버 4종 및 PostgreSQL 상태 검사 통과')
            return
        except Exception:
            try:
                compose(current,image,'stop','dedicated','game','admin','main')
            except Exception:
                print('앱 중지에 실패했습니다. DrawLiar 컨테이너 상태를 확인하세요.', file=sys.stderr)
            raise
if __name__ == '__main__':
    main()
