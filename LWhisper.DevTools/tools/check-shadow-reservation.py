"""Check shadow-decode output ownership with three bounded local CPU processes."""
from concurrent.futures import ThreadPoolExecutor
import hashlib
import json
from pathlib import Path
import subprocess
import sys
from threading import Barrier
import wave


def main(dll, model, root):
    subprocess.run(['dotnet', dll, 'shadow-check-output', root], check=True, timeout=15)
    root = Path(root)
    root.mkdir()
    wav = root / 'synthetic.wav'
    with wave.open(str(wav), 'wb') as f:
        f.setparams((1, 2, 16000, 0, 'NONE', ''))
        f.writeframes(b'\x11\x22' * 16000)
    output = root / 'shared'
    cmd = ['dotnet', dll, 'shadow-decode', '--input', str(wav), '--model', model,
           '--beam', '--threads', '2', '--quiet', '--format', 'json', '--out', str(output)]
    prompt = 'synthetic reservation'
    variants = [[], ['--prompt', prompt], []]
    barrier = Barrier(len(variants), timeout=10)

    def run(extra):
        barrier.wait()
        return subprocess.run(cmd + extra, capture_output=True, timeout=60)

    with ThreadPoolExecutor(max_workers=len(variants)) as workers:
        results = list(workers.map(run, variants))
    codes = [r.returncode for r in results]
    assert sorted(codes) == [0, 1, 1], codes
    winner = results[codes.index(0)]
    assert b'report.json' in winner.stdout
    assert all(not r.stdout for r in results if r.returncode)
    assert {p.name for p in output.iterdir()} == {'.shadow-reservation', 'devtools-log.txt', 'report.json'}
    before = {p.name: p.read_bytes() for p in output.iterdir()}
    report = json.loads(before['report.json'])
    expected = hashlib.sha256(prompt.encode()).hexdigest() if variants[codes.index(0)] else None
    assert report['engine']['effectivePromptSha256'] == expected
    retry = subprocess.run(cmd, capture_output=True, timeout=15)
    assert retry.returncode == 1 and not retry.stdout
    assert before == {p.name: p.read_bytes() for p in output.iterdir()}
    print(f'Parallel exit codes: {codes}; one owner; report matches winner; sequential retry leaves outputs unchanged.')


if __name__ == '__main__':
    main(*sys.argv[1:])
