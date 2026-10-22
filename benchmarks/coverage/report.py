#!/usr/bin/env python3
"""Summarize measured Core-only Coverlet Cobertura output. No inferred coverage."""
import argparse
import datetime as dt
import hashlib
import json
from pathlib import Path
import subprocess
import xml.etree.ElementTree as ET


def summarize(xml_path):
    root = ET.parse(xml_path).getroot()
    packages = root.findall('./packages/package')
    if len(packages) != 1 or packages[0].get('name') != 'MediBridge.Core':
        raise ValueError('Expected exactly the MediBridge.Core package; collect using coverage.runsettings.')
    total, covered = int(root.attrib['lines-valid']), int(root.attrib['lines-covered'])
    if total <= 0 or not 0 <= covered <= total:
        raise ValueError('Invalid covered/total line counts.')
    # Check the exporter percentage instead of trusting a precomputed percentage alone.
    if abs(float(root.attrib['line-rate']) - covered / total) > .0002:
        raise ValueError('Cobertura line rate does not match its measured counts.')
    files = {}
    for cls in packages[0].findall('./classes/class'):
        name = cls.attrib['filename'].replace('\\', '/')
        marker = 'src/MediBridge.Core/'
        if marker not in name:
            raise ValueError('Coverage contains a file outside Core: ' + name)
        name = name[name.index(marker):]
        lines = files.setdefault(name, {})
        for line in cls.findall('./lines/line'):
            number = int(line.attrib['number'])
            lines[number] = max(lines.get(number, 0), int(line.attrib['hits']))
    unique_total = sum(len(lines) for lines in files.values())
    unique_covered = sum(sum(hits > 0 for hits in lines.values()) for lines in files.values())
    if (unique_total, unique_covered) != (total, covered):
        raise ValueError('Physical source line totals disagree with Cobertura totals.')
    return {'assembly': 'MediBridge.Core', 'coveredLines': covered, 'totalLines': total,
            'lineCoveragePercent': 100 * covered / total,
            'files': [{'path': path, 'coveredLines': sum(h > 0 for h in lines.values()), 'totalLines': len(lines)} for path, lines in sorted(files.items())]}


def source_hash():
    paths = [Path('Directory.Build.props'), Path('coverage.runsettings')]
    for folder in ('src', 'tests', 'benchmarks'):
        paths.extend(p for p in Path(folder).rglob('*') if p.suffix in {'.cs', '.csproj', '.json', '.py'}
                     and not {'bin', 'obj', 'work', 'results', '__pycache__'}.intersection(p.parts))
    digest = hashlib.sha256()
    for path in sorted(paths):
        digest.update((path.as_posix() + '\n').encode())
        digest.update(path.read_bytes())
    return digest.hexdigest()


def run(xml_path, output):
    now = dt.datetime.now(dt.timezone.utc)
    run_id = now.strftime('%Y%m%dT%H%M%SZ')
    try:
        report = dict(schemaVersion=1, status='completed', runId=run_id, completedAtUtc=now.isoformat(),
                      **summarize(xml_path), inputSha256=hashlib.sha256(Path(xml_path).read_bytes()).hexdigest(),
                      sourceBaseCommit=subprocess.check_output(['git', 'rev-parse', 'HEAD'], text=True).strip(),
                      sourceContentSha256=source_hash(), sourceScope='Directory.Build.props, coverage.runsettings, src/tests/benchmark code and JSON; excludes obj/bin/work/results',
                      measurement='Coverlet collector 6.0.4; Core assembly only; generated obj files excluded. Physical executable source lines. External crash-child processes are not included by this in-process collector.')
        status = 'completed'
        summary = f"Core lines {report['coveredLines']}/{report['totalLines']} ({report['lineCoveragePercent']:.4f}%)"
    except (ValueError, KeyError, ET.ParseError, OSError, subprocess.SubprocessError) as error:
        report = dict(schemaVersion=1, status='failed', runId=run_id, completedAtUtc=now.isoformat(), error=str(error))
        status, summary = 'failed', 'Coverage extraction failed; see artifact'
    output = Path(output).resolve()
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(json.dumps(report, indent=2) + '\n')
    ledger = output.parent.parent / 'RESULTS.md'
    if not ledger.exists():
        ledger.write_text('# Measured benchmark results\n\n| Completed UTC | Harness | Run ID | Status | Target | Measured result | Artifact |\n|---|---|---|---|---|---|---|\n')
    with ledger.open('a') as stream:
        stream.write(f"| {now.isoformat()} | coverage | {run_id} | {status} | Local Core / Coverlet | {summary} | `results/{output.name}` |\n")
    print(summary)
    return 0 if status == 'completed' else 1


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('cobertura', type=Path)
    parser.add_argument('--output', type=Path, default=Path('benchmarks/results/coverage.json'))
    args = parser.parse_args()
    raise SystemExit(run(args.cobertura, args.output))
