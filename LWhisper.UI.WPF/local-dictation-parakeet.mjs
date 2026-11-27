import { pathToFileURL } from 'node:url';

try {
  const chunks = [];
  let length = 0;
  for await (const chunk of process.stdin) {
    length += chunk.length;
    if (length > 120 * 32000) throw new Error('audio too long');
    chunks.push(chunk);
  }
  if (!length || length % 2) throw new Error('invalid audio');
  const bytes = Buffer.concat(chunks);
  const pcm = new Float32Array(length / 2);
  for (let i = 0; i < pcm.length; i++) pcm[i] = bytes.readInt16LE(i * 2) / 32768;
  const { TranscribeModel, setLogHandler } = await import(pathToFileURL(process.argv[2]));
  setLogHandler(() => {});
  const model = await TranscribeModel.load(process.argv[3]);
  try {
    if (!String(model.backend).startsWith('Vulkan')) throw new Error('GPU unavailable');
    const result = await model.transcribe(pcm);
    process.stdout.write('LWHISPER_REVIEW_RESULT:' + Buffer.from(result.text, 'utf8').toString('base64') + '\n');
  } finally { model.dispose(); }
} catch { process.exitCode = 1; }
