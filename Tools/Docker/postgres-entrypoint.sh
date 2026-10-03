#!/bin/bash
set -euo pipefail
install -d -m 0700 -o postgres -g postgres "$PGDATA"
if [[ ! -f "$PGDATA/PG_VERSION" ]]; then
  runuser -u postgres -- initdb --encoding=UTF8 --locale=C.UTF-8 --auth-local=trust --auth-host=scram-sha-256 -D "$PGDATA" >/dev/null
  echo 'host all all all scram-sha-256' >> "$PGDATA/pg_hba.conf"
  runuser -u postgres -- pg_ctl -D "$PGDATA" -o "-c listen_addresses='' -c unix_socket_directories=/tmp" -w start >/dev/null
  runuser -u postgres -- psql -h /tmp -U postgres -v ON_ERROR_STOP=1 -v password="$POSTGRES_PASSWORD" <<'SQL'
CREATE ROLE drawliar LOGIN PASSWORD :'password';
CREATE DATABASE drawliar OWNER drawliar;
SQL
  runuser -u postgres -- pg_ctl -D "$PGDATA" -m fast -w stop >/dev/null
fi
exec runuser -u postgres -- postgres -D "$PGDATA" -c listen_addresses='*' -c max_connections=60 -c shared_buffers=64MB
