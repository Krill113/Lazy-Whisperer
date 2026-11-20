// Offline witness adapter for an already installed transcribe-cpp module and Parakeet GGUF.
import fs from 'node:fs';
import { spawnSync } from 'node:child_process';
import path from 'node:path';
import { createHash } from 'node:crypto';
import { pathToFileURL } from 'node:url';

const sha = bytes => createHash('sha256').update(bytes).digest('hex');

function pcm16(wav) {
    if (wav.toString('ascii', 0, 4) !== 'RIFF' || wav.toString('ascii', 8, 12) !== 'WAVE')
        throw new Error();
    let format, pcm;
    for (let i = 12; i + 8 <= wav.length;) {
        const kind = wav.toString('ascii', i, i + 4);
        const size = wav.readUInt32LE(i + 4);
        i += 8;
        if (i + size > wav.length) throw new Error();
        if (kind === 'fmt ') format = wav.subarray(i, i + size);
        if (kind === 'data') pcm = wav.subarray(i, i + size);
        i += size + (size % 2);
    }
    if (!format || format.readUInt16LE(0) !== 1 || format.readUInt16LE(2) !== 1 ||
        format.readUInt32LE(4) !== 16000 || format.readUInt16LE(14) !== 16 || !pcm?.length || pcm.length % 2)
        throw new Error();
    return Float32Array.from({length: pcm.length / 2}, (_, i) => pcm.readInt16LE(i * 2) / 32768);
}

let model;
try {
    const [dll, modulePath, modelPath, output, ...wavPaths] = process.argv.slice(2);
    if (!wavPaths.length || [modulePath, modelPath, output, ...wavPaths].some(p => !path.isAbsolute(p)))
        throw new Error();
    if (spawnSync('dotnet', [dll, 'shadow-check-output', output]).status !== 0) throw new Error();
    const modelSha256 = sha(fs.readFileSync(modelPath));
    const runner = `transcribe-cpp:${sha(fs.readFileSync(modulePath))}`;
    const module = await import(pathToFileURL(modulePath));
    module.setLogHandler(() => {});
    model = await module.TranscribeModel.load(modelPath);
    const rows = new Map();
    for (const file of wavPaths) {
        const bytes = fs.readFileSync(file);
        const wavSha256 = sha(bytes);
        if (rows.has(wavSha256)) continue;
        const result = await model.transcribe(pcm16(bytes));
        if (typeof result.text !== 'string') throw new Error();
        rows.set(wavSha256, JSON.stringify({wavSha256, text: result.text, success: true,
            engine: 'parakeet', runner, modelSha256}));
    }
    fs.writeFileSync(output, [...rows.values()].join('\n') + '\n', {flag: 'wx'});
} catch {
    console.error('Witness generation failed; check local runner, Parakeet model, WAVs and destination.');
    process.exitCode = 1;
} finally {
    model?.dispose();
}
