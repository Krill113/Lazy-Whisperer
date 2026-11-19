# Local dictation archive — specification and verification plan

## Scope
At the bottom of Settings, development builds offer a checkbox and an editable local destination. No new microphone listener, upload, ASR tuning or retention policy. A normal launch defaults to off and `%APPDATA%\LWhisper\debug`. Each manually started dictation gets its own folder; stopping capture does not delete old files.

## Plan, checked against the existing code
1. Reuse `UpdateService.IsDevBuild` (version 0.0.0, including local Release); do not use `#if DEBUG`. Collapse the controls and ignore their saved values in versioned releases. Preserve the existing explicit environment opt-in used by DevTools and the temporary data collection process.
2. Keep the nullable preference and folder in the settings clone. Show effective values, but do not turn a temporary environment opt-in into permanent consent just because the user saves unrelated settings. Cancel discards edits. An empty destination selects the standard root.
3. Snapshot the choice at recording start after the previous result is finalized. Reuse the existing WAV/JSONL writer; streaming uses its existing segment/session instrumentation, single-shot dictation saves the returned audio and recognition result together. No writes or directory creation merely from opening Settings.
4. Verify isolated consecutive sessions, folder change, explicit off, inheritance, a versioned build with dev settings present, and actual WPF control loading/saving. Build into separate artifacts, leaving the running collection process and its cutoff untouched.
