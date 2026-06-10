# Security Policy

## Supported Versions

This project is currently in early development. Security fixes are provided for the latest released version only.

| Version | Supported |
| ------- | --------- |
| latest  | Yes       |

## Reporting a Vulnerability

If you discover a security issue, please do not open a public GitHub issue.

Instead, report it privately to the repository owner through GitHub or by using GitHub's private vulnerability reporting feature if available.

Please include:

- A short description of the issue
- Steps to reproduce it
- Potential impact
- Any relevant screenshots or logs
- Suggested fix, if known

## Sensitive Data

This application stores Torn API keys locally on the user's Windows profile using Windows DPAPI protection.

API keys should never be committed to the repository, included in release packages, or shared in logs, screenshots, issues, or pull requests.

## Scope

Security-relevant areas include:

- Torn API key storage and handling
- Local settings storage
- Release packaging
- API request handling
- Accidental exposure of user data or credentials
