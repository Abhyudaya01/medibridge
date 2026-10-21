"""Provision only the approved free-offer SQL resources. Never prints or persists credentials."""
import json
import os
from pathlib import Path
import re
import secrets
import string
import subprocess

EXPECTED = '36637271-a6f8-4110-9be1-55f112612c8c'
ENV_FILE = Path(__file__).with_name('azure.env')
settings = dict(line.split('=', 1) for line in ENV_FILE.read_text().splitlines() if line and not line.startswith('#'))
cli_env = dict(os.environ, AZURE_LOGGING_ENABLE_LOG_FILE='false', AZURE_CORE_COLLECT_TELEMETRY='false')


def az(args, *, change=False, output='json'):
    if change:
        active = subprocess.run(['az', 'account', 'show', '--query', 'id', '-o', 'tsv'], env=cli_env, capture_output=True, text=True, check=True).stdout.strip()
        if active != EXPECTED:
            raise RuntimeError('Azure guard rejected an unexpected active subscription.')
    result = subprocess.run(['az', *args, '--subscription', EXPECTED, '--only-show-errors', '-o', output], env=cli_env, capture_output=True, text=True)
    if result.returncode:
        # Azure error output can contain request arguments: never relay it for credential-bearing operations.
        code = re.search(r'ERROR: \(([A-Za-z0-9_.]+)\)', result.stderr)
        raise RuntimeError('Azure operation failed: ' + ' '.join(args[:3]) + ('; code=' + code.group(1) if code else '; exit=' + str(result.returncode)) + '. Credential-bearing diagnostics suppressed.')
    return json.loads(result.stdout) if output == 'json' and result.stdout.strip() else result.stdout.strip()


def record(name, value):
    settings[name] = value
    text = ENV_FILE.read_text()
    lines = text.splitlines()
    lines = [f'{name}={value}' if line.startswith(name + '=') else line for line in lines]
    if not any(line.startswith(name + '=') for line in text.splitlines()): lines.append(f'{name}={value}')
    ENV_FILE.write_text('\n'.join(lines) + '\n')


def main():
    group = settings['AZURE_RESOURCE_GROUP']
    region = settings['AZURE_REGION']
    app = settings['AZURE_APP_NAME']
    server = settings.get('AZURE_SQL_SERVER')
    database = settings.get('AZURE_SQL_DATABASE', 'medibridge')
    if server:
        # Recover only our named deployment. Existing secret remains exclusively in App Service.
        connection_settings = az(['webapp', 'config', 'connection-string', 'list', '-g', group, '-n', app])
        existing = next((item for item in connection_settings if item['name'] == 'Sql'), None)
        if existing is not None:
            if server + '.database.windows.net' not in existing['connectionString']:
                raise RuntimeError('Existing Sql target differs from public deployment metadata; refusing to overwrite it.')
            verify = az(['sql', 'db', 'show', '-g', group, '-s', server, '-n', database])
            if verify.get('useFreeLimit') is not True or verify.get('freeLimitExhaustionBehavior') != 'AutoPause':
                raise RuntimeError('Existing database does not have the required free-offer safeguards.')
            print('Approved SQL resources already configured; credentials were not displayed.')
            return
    if az(['provider', 'show', '--namespace', 'Microsoft.Sql', '--query', 'registrationState'], output='tsv') != 'Registered':
        print('Registering Microsoft.Sql for the approved subscription.', flush=True)
        az(['provider', 'register', '--namespace', 'Microsoft.Sql', '--wait'], change=True, output='none')
    password = ''.join(secrets.choice(string.ascii_letters + string.digits + '!@#%_-') for _ in range(48)) + 'Aa9!'
    admin = 'medibridgeadmin'
    if not server:
        server = 'sql-medibridge-abhi-' + secrets.token_hex(3)
        az(['sql', 'server', 'create', '-g', group, '-n', server, '-l', region, '--admin-user', admin, '--admin-password', password, '--minimal-tls-version', '1.2'], change=True, output='none')
        record('AZURE_SQL_SERVER', server)
        record('AZURE_SQL_DATABASE', database)
    else:
        az(['sql', 'server', 'update', '-g', group, '-n', server, '--admin-password', password], change=True, output='none')
    print(f'SQL server created in {region}: {server}', flush=True)
    az(['sql', 'db', 'create', '-g', group, '-s', server, '-n', database, '--edition', 'GeneralPurpose', '--family', 'Gen5', '--capacity', '2', '--compute-model', 'Serverless', '--min-capacity', '0.5', '--auto-pause-delay', '60', '--max-size', '32GB', '--backup-storage-redundancy', 'Local', '--use-free-limit', 'true', '--free-limit-exhaustion-behavior', 'AutoPause'], change=True, output='none')
    verify = az(['sql', 'db', 'show', '-g', group, '-s', server, '-n', database])
    if verify.get('useFreeLimit') is not True or verify.get('freeLimitExhaustionBehavior') != 'AutoPause':
        raise RuntimeError('Free-offer settings were not confirmed; App Service configuration was not changed.')
    print('Free-offer database and AutoPause exhaustion policy verified.', flush=True)
    az(['sql', 'server', 'firewall-rule', 'create', '-g', group, '-s', server, '-n', 'AllowAzureServices', '--start-ip-address', '0.0.0.0', '--end-ip-address', '0.0.0.0'], change=True, output='none')
    connection = f'Server=tcp:{server}.database.windows.net,1433;Initial Catalog={database};User ID={admin};Password={password};Encrypt=True;TrustServerCertificate=False;Connection Timeout=60;Pooling=True;'
    az(['webapp', 'config', 'connection-string', 'set', '-g', group, '-n', app, '--connection-string-type', 'SQLAzure', '--settings', 'Sql=' + connection], change=True, output='none')
    print('App Service Sql connection string configured; no credentials persisted locally or displayed.', flush=True)


if __name__ == '__main__':
    try: main()
    except Exception as error:
        # Suppress subprocess tracebacks, arguments, and secret-bearing command output.
        print(str(error) if isinstance(error, RuntimeError) else 'Provisioning failed; credential-bearing diagnostics were suppressed.')
        raise SystemExit(1)
