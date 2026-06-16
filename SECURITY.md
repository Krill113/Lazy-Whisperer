# Security Policy

## Reporting a Vulnerability

Please report security issues privately:

- Open a GitHub Security Advisory (repository **Security** tab → *Report a vulnerability*), or
- For non-sensitive reports, open a regular issue.

Please avoid disclosing exploitable details publicly until a fix is available.

## Notes

- LWhisper performs speech recognition fully offline (local Whisper models, no cloud ASR).
- A self-update mechanism (download from GitHub Releases + SHA-256 verification of the archive, fail-closed) is **planned**. Until code signing is in place, Windows SmartScreen may warn on first launch.
- Trust model for updates: an update is only as trustworthy as this project's GitHub account.
