#!/usr/bin/env bash
set -euo pipefail

if [[ "${EUID}" -ne 0 ]]; then
    echo "请使用 sudo 执行: sudo $0" >&2
    exit 1
fi

RUN_AS_USER="${SUDO_USER:-$(id -un)}"

DB_NAME="${DB_NAME:-ridemanager}"
DB_USER="${DB_USER:-ridemanager}"
DB_PASSWORD="${DB_PASSWORD:-ridemanager}"
POSTGRES_USER="${POSTGRES_USER:-postgres}"
PG_ADMIN_HOST="${PG_ADMIN_HOST:-localhost}"
PG_ADMIN_PORT="${PG_ADMIN_PORT:-5432}"
PG_ADMIN_USER="${PG_ADMIN_USER:-postgres}"
PG_ADMIN_PASSWORD="${PG_ADMIN_PASSWORD:-}"

if ! command -v psql >/dev/null 2>&1; then
    echo "未找到 psql，请先安装 PostgreSQL client。" >&2
    exit 1
fi

ADMIN_MODE=""
if sudo -H -u "${RUN_AS_USER}" psql -w --dbname postgres --command "select 1" >/dev/null 2>&1; then
    ADMIN_MODE="sudo-user"
elif sudo -H -u "${POSTGRES_USER}" psql -w --dbname postgres --command "select 1" >/dev/null 2>&1; then
    ADMIN_MODE="postgres-user"
else
    ADMIN_MODE="password"
    if [[ -z "${PG_ADMIN_PASSWORD}" ]]; then
        read -r -p "请输入 PostgreSQL 管理用户 [${PG_ADMIN_USER}]: " input_admin_user </dev/tty
        PG_ADMIN_USER="${input_admin_user:-${PG_ADMIN_USER}}"
        read -r -s -p "请输入 PostgreSQL 管理用户 ${PG_ADMIN_USER} 的密码: " PG_ADMIN_PASSWORD </dev/tty
        echo >/dev/tty
    fi
fi

run_admin_psql() {
    local database="$1"
    shift

    case "${ADMIN_MODE}" in
        sudo-user)
            sudo -H -u "${RUN_AS_USER}" psql -w -v ON_ERROR_STOP=1 --dbname "${database}" "$@"
            ;;
        postgres-user)
            sudo -H -u "${POSTGRES_USER}" psql -w -v ON_ERROR_STOP=1 --dbname "${database}" "$@"
            ;;
        password)
            PGPASSWORD="${PG_ADMIN_PASSWORD}" psql \
                -h "${PG_ADMIN_HOST}" \
                -p "${PG_ADMIN_PORT}" \
                -U "${PG_ADMIN_USER}" \
                -v ON_ERROR_STOP=1 \
                --dbname "${database}" \
                "$@"
            ;;
    esac
}

echo "==> 创建 PostgreSQL 用户和数据库"
run_admin_psql postgres \
    --set=db_name="${DB_NAME}" \
    --set=db_user="${DB_USER}" \
    --set=db_password="${DB_PASSWORD}" <<'SQL'
SELECT format('CREATE ROLE %I LOGIN PASSWORD %L', :'db_user', :'db_password')
WHERE NOT EXISTS (
    SELECT 1 FROM pg_catalog.pg_roles WHERE rolname = :'db_user'
)\gexec

SELECT format('ALTER ROLE %I WITH LOGIN PASSWORD %L', :'db_user', :'db_password')\gexec

SELECT format('CREATE DATABASE %I OWNER %I', :'db_name', :'db_user')
WHERE NOT EXISTS (
    SELECT 1 FROM pg_catalog.pg_database WHERE datname = :'db_name'
)\gexec

SELECT format('ALTER DATABASE %I OWNER TO %I', :'db_name', :'db_user')\gexec
SQL

echo "==> 授权 public schema"
run_admin_psql "${DB_NAME}" \
    --set=db_user="${DB_USER}" <<'SQL'
SELECT format('GRANT ALL ON SCHEMA public TO %I', :'db_user')\gexec
SELECT format('ALTER SCHEMA public OWNER TO %I', :'db_user')\gexec
SQL

echo "==> 数据库已就绪: ${DB_NAME} / ${DB_USER}"
