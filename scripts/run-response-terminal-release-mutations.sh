#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
source_file="$repo_root/src/Netclaw.Providers/OpenAi/OpenAiResponseStreamClient.cs"
output_path="${1:-$repo_root/artifacts/stryker/response-terminal-release}"
if [[ "$output_path" != /* ]]; then
  output_path="$repo_root/$output_path"
fi

span="$(python3 - "$source_file" <<'PY'
from pathlib import Path
import sys

source = Path(sys.argv[1]).read_text()
begin = '// response-terminal-release:begin'
end = '// response-terminal-release:end'
if source.count(begin) != 1 or source.count(end) != 1:
    raise SystemExit('The response terminal markers must each occur once.')
start = source.index(begin)
stop = source.index(end)
if stop <= start or source[start:stop].count('if (outcome == "completed")') != 1:
    raise SystemExit('The response terminal release decision changed.')
print(f'{start}..{stop}')
PY
)"

(
  cd "$repo_root/src/Netclaw.Actors.MutationTests"
  dotnet stryker \
    --config-file stryker-config.json \
    --project Netclaw.Providers.csproj \
    --mutate "OpenAi/OpenAiResponseStreamClient.cs{$span}" \
    --output "$output_path" \
    --skip-version-check
)

python3 - "$output_path/reports/mutation-report.json" "$source_file" <<'PY'
import json
from pathlib import Path
import sys

report = json.loads(Path(sys.argv[1]).read_text())
source_path = Path(sys.argv[2])
source = source_path.read_text()
first = source[:source.index('// response-terminal-release:begin')].count('\n') + 1
last = source[:source.index('// response-terminal-release:end')].count('\n') + 1
target = []
outside = []
for name, data in report['files'].items():
    for mutant in data.get('mutants', []):
        if mutant['status'] == 'Ignored':
            continue
        if Path(name).resolve() == source_path.resolve() and first <= mutant['location']['start']['line'] < last:
            target.append(mutant)
        elif mutant['status'] != 'CompileError':
            outside.append(mutant)
expected = {('Equality mutation', 'outcome != "completed"'), ('String mutation', '""'), ('Statement mutation', ';')}
if (len(target) != 3 or outside or any(mutant['status'] != 'Killed' for mutant in target)
        or {(mutant['mutatorName'], mutant['replacement']) for mutant in target} != expected):
    raise SystemExit(f'Expected three killed response terminal mutants; got {[(m["mutatorName"], m["status"]) for m in target]}.')
print('Response terminal release: three expected mutants killed.')
PY
