"""Generate and verify a synthetic shadow-repeat run. Python 3 standard library only."""
import hashlib
import json
from pathlib import Path
import subprocess
import sys
import wave


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def main(dll, root):
    assert not subprocess.run(['dotnet', dll, 'shadow-check-output', root], capture_output=True).returncode
    root = Path(root).resolve()
    root.mkdir()
    inputs = root / 'input'
    inputs.mkdir()
    config = dict(engine='whisper-fixture', modelSha256='a'*64, backend='cpu', runtime='fixture',
                  language='ru', beam=True, audioContext=448, threads=4, parallelism=1)

    def segment(id_, order, prompt, neutral, witness, archived):
        name = f'seg-{id_}.wav'
        with wave.open(str(inputs / name), 'wb') as f:
            f.setparams((1, 2, 16000, 0, 'NONE', ''))
            f.writeframes((id_ * 25).to_bytes(2, 'little') * 16000)
        sha = digest(inputs / name)
        def arm(text, prompt_hash):
            return dict(wavSha256=sha, text=text, success=True, fallback=False,
                        config=config, promptSha256=prompt_hash)
        return dict(id=id_, order=order, wav=name, wavSha256=sha, archivedRaw=prompt,
                    archivedPost=archived, prompted=arm(prompt, 'b'*64), neutral=arm(neutral, None),
                    witness=dict(wavSha256=sha, text=witness, success=True, engine='parakeet',
                                 runner='fixture', modelSha256='c'*64))

    # Completion order 2, 1, 3. The switch changes prefix removal on segment 1.
    segments = [segment(2, 1, 'маяк маяк', 'фонарь', 'Фонарь.', 'маяк'),
                segment(1, 2, 'маяк горит', 'маяк горит', '', 'горит'),
                segment(3, 3, 'винт винт', 'болт X:1', 'болт X1', 'винт'),
                segment(4, 4, 'ярлык ярлык', 'ярлык,', 'ярлык,', 'ярлык')]
    manifest = inputs / 'manifest.json'
    data = dict(version=1, sessions=[dict(id='synthetic-lantern', history='validated-replay',
                                         compressionThreshold=2.4, segments=segments)])
    manifest.write_text(json.dumps(data), encoding='utf-8')
    before = {p: digest(p) for p in inputs.iterdir()}
    output = root / 'result.json'
    cmd = ['dotnet', dll, 'shadow-repeat', str(manifest), str(output)]
    first = subprocess.run(cmd, capture_output=True, text=True)
    assert first.returncode == 0, (first.returncode, 'shadow command failed')
    assert 'маяк' not in first.stdout + first.stderr
    report = json.loads(output.read_text(encoding='utf-8'))
    session, = report['sessions']
    assert session['baseline'] == 'горит маяк винт ярлык'
    assert session['shadow'] == 'маяк горит фонарь винт ярлык'
    assert [s['reason'] for s in session['segments']] == ['selected', 'unflagged', 'witness-disagreement', 'no-word-change']
    assert report['selected'] == 1 and report['flagged'] == 3 and report['covered'] == 3
    saved = digest(output)
    assert subprocess.run(cmd, capture_output=True).returncode != 0
    assert digest(output) == saved
    assert subprocess.run(cmd[:-1] + [str(inputs / 'new.json')], capture_output=True).returncode != 0
    assert not (inputs / 'new.json').exists()
    assert before == {p: digest(p) for p in inputs.iterdir()}
    def invalid_case(name, mutate, expected):
        copy = json.loads(json.dumps(data))
        mutate(copy['sessions'][0])
        manifest.write_text(json.dumps(copy), encoding='utf-8')
        dest = root / (name + '.json')
        process = subprocess.run(cmd[:-1] + [str(dest)], capture_output=True, text=True)
        if expected is None:
            assert process.returncode != 0 and not dest.exists(), name
        else:
            assert process.returncode == 0, name
            actual = json.loads(dest.read_text(encoding='utf8'))
            assert actual['sessions'][0]['segments'][0]['reason'] == expected, name

    invalid_case('config', lambda s: s['segments'][0]['neutral']['config'].update(beam=False), 'missing-or-invalid-arms')
    invalid_case('bad-witness', lambda s: s['segments'][0]['witness'].update(wavSha256='0'*64), 'missing-or-invalid-witness')
    invalid_case('empty', lambda s: s['segments'][0]['witness'].update(text=''), 'empty-evidence')
    invalid_case('history', lambda s: s.update(history='unknown'), 'unknown-history')
    invalid_case('duplicate', lambda s: s['segments'].append(s['segments'][0]), None)
    invalid_case('escape', lambda s: s['segments'][0].update(wav='../result.json'), None)
    invalid_case('wrong-bytes', lambda s: s['segments'][0].update(wav='seg-1.wav'), 'invalid-wav')
    manifest.write_text(json.dumps(data), encoding='utf-8')
    assert before == {p: digest(p) for p in inputs.iterdir()}
    # Exercise the same capture/DevTools/witness importer documented for new inputs.
    capture = root / 'capture'
    capture.mkdir()
    wav = capture / 'seg-0001.wav'
    wav.write_bytes((inputs / 'seg-2.wav').read_bytes())
    (capture / 'session.wav').write_bytes(wav.read_bytes())
    meta = [dict(type='segment', segmentId=1, wavFile=wav.name, beam=True, usedFallback=False,
                 error=None, language='ru', threads=4, audioContextSize=448, rawText='маяк маяк'),
            dict(type='postfilter', segmentId=1, rawText='маяк'), dict(type='session')]
    (capture / 'meta.jsonl').write_text(''.join(json.dumps(x) + chr(10) for x in meta), encoding='utf-8')
    prompt_file = root / 'prompt.txt'
    prompt_file.write_text('  synthetic  ', encoding='utf-8-sig')
    effective = hashlib.sha256(b'synthetic').hexdigest()
    model = root / 'dummy-model.bin'
    model.write_bytes(b'fixture only')
    declarations = root / 'provenance.json'
    declarations.write_text(json.dumps(dict(captureBackend='cpu', engine='fixture', runtime='fixture',
        language='ru', effectivePromptFile=str(prompt_file), promptSha256=effective,
        modelSha256=digest(model), compressionThreshold=2.4, parallelism=1)), encoding='utf-8')
    for name, chars, text in [('normal', 9, 'маяк маяк'), ('neutral', 0, 'фонарь')]:
        engine = dict(promptChars=chars, effectivePromptSha256=effective if chars else None,
                      modelPath=str(model), gpu=False, language='ru')
        run = dict(file=str(wav), fileSha256=digest(wav), error=None, usedFallback=False,
                   beam=True, audioContextSize=448, threads=4, parallel=1, text=text)
        (root / (name + '.json')).write_text(json.dumps(dict(schemaVersion=1, engine=engine, runs=[run])), encoding='utf-8')
    witness_file = root / 'witness.jsonl'
    witness_file.write_text(json.dumps(dict(segments[0]['witness'], wavSha256=digest(wav))), encoding='utf-8')
    observed = {p: digest(p) for p in capture.iterdir()}
    importer = Path(__file__).with_name('import-shadow.py')
    proc = subprocess.run([sys.executable, '-I', str(importer), dll, str(capture), str(root/'normal.json'),
                           str(root/'neutral.json'), str(witness_file), str(declarations)], capture_output=True)
    assert proc.returncode == 0, 'documented capture import failed'
    imported_result = root / 'capture-result.json'
    proc = subprocess.run(cmd[:3] + [str(capture/'shadow-manifest.json'), str(imported_result)], capture_output=True)
    assert proc.returncode == 0, 'imported capture shadow run failed'
    imported = json.loads(imported_result.read_text(encoding='utf-8'))
    assert imported['selected'] == 1 and imported['sessions'][0]['shadow'] == 'фонарь'
    assert observed == {p: digest(p) for p in observed}
    # 1. A bad predecessor cannot validate the following, otherwise eligible switch.
    invalid_case('predecessor-wav', lambda s: s['segments'].insert(0,
        dict(s['segments'][2], order=1, id=17, wav='seg-1.wav')) or
        [x.update(order=x['order']+1) for x in s['segments'][1:]], 'invalid-wav')
    invalid_case('predecessor-archive', lambda s: s['segments'].insert(0,
        dict(s['segments'][2], order=1, id=20, archivedPost='несовпадающий архив')) or
        [x.update(order=x['order']+1) for x in s['segments'][1:]], 'archive-mismatch')
    for name in ('predecessor-wav', 'predecessor-archive'):
        blocked = json.loads((root / (name + '.json')).read_text(encoding='utf-8'))
        assert blocked['selected'] == 0 and blocked['sessions'][0]['segments'][1]['reason'] == 'invalid-predecessor'
    manifest.write_text(json.dumps(data), encoding='utf-8')

    # 2. The local bench restriction alone is insufficient: nested repositories are refused.
    git_root = root / 'sibling-repo'
    git_root.mkdir()
    subprocess.run(['git', 'init', '-q', str(git_root)], check=True)
    forbidden = git_root / 'result.json'
    assert subprocess.run(cmd[:-1] + [str(forbidden)], capture_output=True).returncode != 0
    assert not forbidden.exists()
    assert subprocess.run(['dotnet', dll, 'shadow-decode', '--input', str(wav),
                           '--out', str(git_root/'decode')], capture_output=True).returncode != 0
    assert not (git_root/'decode').exists()

    # 3. Keep the decode report untouched while replacing the prompt with a same-length file.
    fake_prompt = root / 'replacement.txt'
    fake_prompt.write_text('different', encoding='utf-8')
    wrong = json.loads(declarations.read_text(encoding='utf-8'))
    wrong.update(effectivePromptFile=str(fake_prompt), promptSha256=digest(fake_prompt))
    wrong_path = root / 'wrong-provenance.json'
    wrong_path.write_text(json.dumps(wrong), encoding='utf-8')
    for name in ('wrong-prompt', 'length-only'):
        other = root / name
        other.mkdir()
        for source in observed:
            (other / source.name).write_bytes(source.read_bytes())
        reports = []
        for arm in ('normal', 'neutral'):
            report = json.loads((root / (arm + '.json')).read_text(encoding='utf-8'))
            report['runs'][0]['file'] = str(other/wav.name)
            if name == 'length-only':
                del report['engine']['effectivePromptSha256']
            report_path = root / (name + '-' + arm + '.json')
            report_path.write_text(json.dumps(report), encoding='utf-8')
            reports.append(str(report_path))
        proc = subprocess.run([sys.executable, '-I', str(importer), dll, str(other), *reports, str(witness_file),
                               str(wrong_path if name == 'wrong-prompt' else declarations)], capture_output=True)
        assert proc.returncode == 0, name
        result = root / (name + '-result.json')
        proc = subprocess.run(cmd[:3] + [str(other/'shadow-manifest.json'), str(result)], capture_output=True)
        assert proc.returncode == 0, name
        outcome = json.loads(result.read_text(encoding='utf-8'))
        assert outcome['selected'] == 0 and outcome['sessions'][0]['segments'][0]['reason'] == 'missing-or-invalid-arms'
    assert before == {p: digest(p) for p in inputs.iterdir()}
    print('B2: one selection; exact ID-order/prefix outcomes; rejection, no overwrite, input hashes unchanged')


if __name__ == '__main__':
    main(*sys.argv[1:])
