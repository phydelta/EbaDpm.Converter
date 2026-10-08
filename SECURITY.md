# Security policy

## Supported versions

Only the latest released version receives fixes. Please check that a problem still occurs with
the latest release before reporting it.

## Reporting a vulnerability

**Do not open a public issue for security problems.**

Report vulnerabilities privately through GitHub: go to the repository's **Security** tab and choose
**Report a vulnerability**. Include:

- the affected version,
- a description of the problem and its impact,
- steps or a minimal input to reproduce it (do not attach EBA data files; describe them by name).

You should receive an acknowledgement within a few working days. Once the problem is confirmed, a
fix is prepared in a private security advisory and released as soon as possible; the advisory is
then published with credit to the reporter, unless they prefer otherwise.

## Scope

EbaDpm.Converter is an offline command-line tool: it reads a local Access database and local
Excel workbooks and writes a local SQLite database. Relevant reports include, for example, crafted
input files that cause code execution, writes outside the requested output path, or vulnerable
dependencies. Release executables are built by GitHub Actions and published with a SHA-256
checksum file and a build provenance attestation (`gh attestation verify`).
