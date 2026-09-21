# Security Policy

## Supported Versions

The latest tagged `v1.NN` release is the supported version. ANAN Core
self-updates in the field, so radios are expected to be on (or near) the
newest release; fixes ship as a new tag rather than as backports.

| Version | Supported |
| ------- | --------- |
| latest `v1.NN` release | :white_check_mark: |
| anything older | :x: |

## Reporting a Vulnerability

**Please do not open a public issue for an exploitable bug.**

Use GitHub's private vulnerability reporting on this repository:
**Security → Report a vulnerability**. You'll get an acknowledgement, and
the report stays private until a fixed release is out.

Reports touching the remote-access path (SPAKE2+ authentication, the
signalling broker, TX authorization) are especially welcome — that stack
is deny-by-default by design, and we want to know if you can get around
it.

Hardware/RF safety concerns (PA protection, keying) are treated with the
same priority as software vulnerabilities.
