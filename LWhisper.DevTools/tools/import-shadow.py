"""Join one finalized capture, two DevTools JSON reports and Parakeet JSONL."""
import hashlib
import json
from pathlib import Path
import re
import sys
import subprocess


def read(path):
    return json.loads(Path(path).read_text(encoding='utf-8-sig'))


def rows(path):
    return [json.loads(line) for line in Path(path).read_text(encoding='utf-8-sig').splitlines()]


def sha(path):
    with path.open('rb') as f:
        return hashlib.file_digest(f, 'sha256').hexdigest()


def unique(items, key):
    index = {}
    for item in items:
        k = key(item)
        if k in index:
            raise ValueError('duplicate')
        index[k] = item
    return index


def main(dll, capture, prompted, neutral, witness, provenance):
    destination = Path(capture) / 'shadow-manifest.json'
    if subprocess.run(['dotnet', dll, 'shadow-check-output', str(destination)], capture_output=True).returncode:
        raise ValueError('unsafe destination')
    root = Path(capture).resolve(strict=True)
    p = read(provenance)
    reports = [read(v) for v in (prompted, neutral)]
    if any(x['schemaVersion'] != 1 for x in reports):
        raise ValueError('unsupported report version')
    if reports[0]['engine']['promptChars'] <= 0 or reports[1]['engine']['promptChars'] != 0:
        raise ValueError('wrong arms')
    if any(reports[0]['engine'].get(k) != reports[1]['engine'].get(k)
           for k in reports[0]['engine'].keys() | reports[1]['engine'].keys() if k not in ('promptChars', 'effectivePromptSha256')):
        raise ValueError('engine settings differ')
    if (reports[0]['engine']['gpu'] or reports[0]['engine']['language'] != p['language']
            or sha(Path(reports[0]['engine']['modelPath'])) != p['modelSha256'].lower()):
        raise ValueError('provenance mismatch')
    prompt = subprocess.run(['dotnet', dll, 'shadow-prompt-hash', p['effectivePromptFile']],
                            capture_output=True, text=True)
    prompt_hash = prompt.stdout.strip()
    bound = (prompt.returncode == 0 and prompt_hash == p['promptSha256'].lower()
             and reports[0]['engine'].get('effectivePromptSha256') == prompt_hash
             and 'effectivePromptSha256' in reports[1]['engine']
             and reports[1]['engine']['effectivePromptSha256'] is None)
    arms = [unique(r['runs'], lambda x: Path(x['file']).resolve()) for r in reports]
    ws = unique(rows(witness), lambda x: x['wavSha256'].lower())
    meta = rows(root / 'meta.jsonl')
    finals = [x for x in meta if x['type'] == 'session']
    if len(finals) != 1 or not (root / 'session.wav').is_file():
        raise ValueError('not finalized')
    raw = unique((x for x in meta if x['type'] == 'segment'), lambda x: x['segmentId'])
    post = unique(((i, x) for i, x in enumerate(meta, 1) if x['type'] == 'postfilter'), lambda x: x[1]['segmentId'])
    if not raw or raw.keys() != post.keys():
        raise ValueError('missing rows')
    segments = []
    for id_, m in raw.items():
        name = m['wavFile']
        if not re.fullmatch(r'seg-\d+\.wav', name, flags=re.I):
            raise ValueError('unexpected WAV name')
        wav = root / name
        h = sha(wav)

        def arm(index):
            r = arms[index].get(wav)
            if (not bound or r is None or r['fileSha256'].lower() != h or r['error'] is not None or r['usedFallback']
                    or not r['beam'] or not m['beam'] or m['usedFallback'] or m['error'] is not None
                    or r['audioContextSize'] != m['audioContextSize'] or r['threads'] != m['threads']
                    or r['parallel'] != p['parallelism'] or m['language'] != p['language']):
                return None
            return dict(wavSha256=h, text=r['text'], success=True, fallback=False,
                        config=dict(engine=p['engine'], modelSha256=p['modelSha256'], backend='cpu',
                                    runtime=p['runtime'], language=p['language'], beam=True,
                                    audioContext=r['audioContextSize'], threads=r['threads'], parallelism=r['parallel']),
                        promptSha256=p['promptSha256'] if index == 0 else None)

        order, result = post[id_]
        segments.append(dict(id=id_, order=order, wav=name, wavSha256=h, archivedRaw=m['rawText'],
                             archivedPost=result['rawText'], prompted=arm(0), neutral=arm(1), witness=ws.get(h)))
    history = 'single-segment' if len(segments) == 1 and p['captureBackend'] == 'cpu' else 'unknown'
    payload = dict(version=1, sessions=[dict(id=root.name, history=history,
                                           compressionThreshold=p['compressionThreshold'], segments=segments)])
    with (root / 'shadow-manifest.json').open('x', encoding='utf-8') as f:
        json.dump(payload, f, ensure_ascii=False)


if __name__ == '__main__':
    try:
        main(*sys.argv[1:])
    except (OSError, ValueError, KeyError, TypeError):
        sys.exit('Capture import rejected; check local inputs and declared provenance.')
