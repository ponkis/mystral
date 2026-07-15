# Security Policy

## Supported versions

Security fixes are provided for the latest released version of Mystral. Older
releases are not maintained.

| Version | Supported          |
| ------- | ------------------ |
| 2.0.x   | :white_check_mark: |
| < 2.0   | :x:                |

## Reporting a vulnerability

**Please do not report security vulnerabilities through public GitHub issues,
discussions, or pull requests.**

Instead, report them privately through GitHub's private vulnerability reporting:

1. Go to the repository's **Security** tab.
2. Click **Report a vulnerability**
   (<https://github.com/ponkis/mystral/security/advisories/new>).
3. Provide as much detail as you can (see below).

> If private vulnerability reporting is not yet enabled for the repository, a
> maintainer can turn it on under **Settings → Security → Advanced Security →
> Private vulnerability reporting**.

Alternatively, you can email the maintainer directly at **ponkis@ponkis.xyz**.

### What to include

- A description of the vulnerability and its impact.
- The Mystral version and build (Debug/Development or Release/Production).
- Your Windows version.
- Steps to reproduce, proof-of-concept, or affected code paths.
- Any suggested remediation, if you have one.

### What to expect

- **Acknowledgement** of your report within a few days.
- An assessment and, where applicable, a coordinated fix and release.
- Credit for the discovery once a fix is public, unless you prefer to remain
  anonymous.

## Scope notes

- Mystral stores Last.fm credentials and the sharing (Globe) token in a
  per-user, DPAPI-encrypted credential store — never in `settings.json`. Reports
  about credential handling are in scope.
- The optional social-sharing integration talks to a proprietary backend that
  contributors cannot run. Issues in Mystral's client-side handling (token
  storage, host allow-listing, request validation) are in scope; the backend
  itself is out of scope for this repository.
