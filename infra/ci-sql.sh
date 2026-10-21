#!/usr/bin/env bash
set -euo pipefail
sql_test_password="$(openssl rand -base64 32)Aa9!"
printf '::add-mask::%s\n' "$sql_test_password"
export MSSQL_SA_PASSWORD="$sql_test_password"
docker run --name medibridge-test-sql -e ACCEPT_EULA=Y -e MSSQL_SA_PASSWORD -p 1433:1433 -d mcr.microsoft.com/mssql/server:2022-latest >/dev/null
for attempt in {1..60}; do
  if docker exec -e SQLCMDPASSWORD="$sql_test_password" medibridge-test-sql /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -C -Q 'SELECT 1' >/dev/null 2>&1; then
    printf 'MEDIBRIDGE_TEST_SQL=Server=localhost,1433;Database=MediBridgeCi;User ID=sa;Password=%s;Encrypt=True;TrustServerCertificate=True\n' "$sql_test_password" >> "$GITHUB_ENV"
    exit 0
  fi
  sleep 2
done
echo 'SQL Server test container did not become ready.' >&2
exit 1
