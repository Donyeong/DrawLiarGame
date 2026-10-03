#!/bin/bash
set -euo pipefail
[[ $(id -u) -eq 0 ]] || { echo 'sudo로 실행하세요.'; exit 1; }
host="${1:?공개 IPv4 또는 DNS 필요}"
[[ "$host" =~ ^[A-Za-z0-9][A-Za-z0-9.-]{0,252}$ ]] || exit 1
. /etc/os-release
[[ "$ID" = ubuntu && "$VERSION_ID" = 24.04 && $(uname -m) = x86_64 ]] || { echo 'Ubuntu 24.04 x64가 필요합니다.'; exit 1; }
docker compose version >/dev/null
getent passwd kona-deploy >/dev/null
install -d -m 0755 /srv/drawliar /srv/drawliar/dev /srv/drawliar/dev/secrets /opt/drawliar/releases
install -d -m 0755 -o root -g root /opt/drawliar/deploy
for template in Dockerfile Dockerfile.postgres postgres-entrypoint.sh compose.yaml; do
  install -m 0644 -o root -g root "$(dirname "$0")/$template" "/opt/drawliar/deploy/$template"
done
install -d -m 0700 -o kona-deploy -g kona-deploy /srv/drawliar/incoming
if [[ ! -f /srv/drawliar/dev/.env ]]; then
  DRAWLIAR_SETUP_HOST="$host" python3 - <<'PY'
import os,pathlib,secrets
root=pathlib.Path('/srv/drawliar/dev')
password=secrets.token_hex(32)
tls=secrets.token_hex(32)
values={'DRAWLIAR_PUBLIC_HOST':os.environ['DRAWLIAR_SETUP_HOST'],'DRAWLIAR_DB_PASSWORD':password,'DRAWLIAR_DATABASE':'Host=db;Port=5432;Database=drawliar;Username=drawliar;Password='+password+';Maximum Pool Size=15','DRAWLIAR_CLUSTER_KEY':secrets.token_hex(48),'DRAWLIAR_ADMIN_KEY':secrets.token_hex(48),'DRAWLIAR_TLS_PASSWORD':tls}
path=root/'.env'
path.write_text(''.join(k+'='+v+'\n' for k,v in values.items()))
path.chmod(0o600)
(root/'secrets'/'tls.pass').write_text(tls)
(root/'secrets'/'tls.pass').chmod(0o600)
PY
fi
if [[ ! -f /srv/drawliar/dev/secrets/server.pfx ]]; then
  [[ "$host" =~ ^[0-9]+\.[0-9]+\.[0-9]+\.[0-9]+$ ]] && san="IP:$host" || san="DNS:$host"
  openssl req -x509 -newkey rsa:3072 -sha256 -nodes -days 365 -keyout /srv/drawliar/dev/secrets/server.key -out /srv/drawliar/dev/secrets/server.pem -subj "/CN=$host" -addext "subjectAltName=$san" >/dev/null 2>&1
  openssl pkcs12 -export -out /srv/drawliar/dev/secrets/server.pfx -inkey /srv/drawliar/dev/secrets/server.key -in /srv/drawliar/dev/secrets/server.pem -passout file:/srv/drawliar/dev/secrets/tls.pass
  chmod 0600 /srv/drawliar/dev/secrets/server.key
  chown 1654:1654 /srv/drawliar/dev/secrets/server.pfx
  chmod 0400 /srv/drawliar/dev/secrets/server.pfx
fi
install -m 0755 -o root -g root "$(dirname "$0")/deploy.py" /usr/local/sbin/drawliar-deploy
printf 'kona-deploy ALL=(root) NOPASSWD: /usr/local/sbin/drawliar-deploy\n' > /etc/sudoers.d/drawliar-deploy
chmod 0440 /etc/sudoers.d/drawliar-deploy
visudo -cf /etc/sudoers.d/drawliar-deploy >/dev/null
fingerprint=$(openssl x509 -in /srv/drawliar/dev/secrets/server.pem -outform DER | sha256sum | awk '{print toupper($1)}')
DRAWLIAR_SETUP_PIN="$fingerprint" python3 - <<'PY'
import os,pathlib
path=pathlib.Path('/srv/drawliar/dev/.env')
values=[line for line in path.read_text().splitlines() if not line.startswith('DRAWLIAR_CERTIFICATE_SHA256=')]
values.append('DRAWLIAR_CERTIFICATE_SHA256='+os.environ['DRAWLIAR_SETUP_PIN'])
path.write_text('\n'.join(values)+'\n')
path.chmod(0o600)
PY
printf 'DRAWLIAR_CERTIFICATE_SHA256=%s\n' "$fingerprint"
