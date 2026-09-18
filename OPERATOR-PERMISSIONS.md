# Operator permissions

This file is the canonical record of repository-specific permissions the operator has made durable.
Edit or remove an entry to amend or revoke it. These permissions do not replace Baton's project trust
ceiling, widen a lane's `PermissionGrant`, or confer authority to originate work, spend, merge, or act
outside the named scope.

The register is vendor-neutral. Each entry names the vendor, allowed repository data, purposes, and
duration; no entry for a vendor or data class means no durable permission has been recorded for it.

## Google Antigravity (AGY) repository processing

Granted by the repository operator on 2026-09-18, until revoked.

Google Antigravity workers may read and process the Baton repository and Baton worktrees for
implementation, review, advice, and testing. This permission includes committed and uncommitted source,
diffs, tests, documentation, and Baton-generated task artifacts. The repository's committed content is
public; this entry expressly also permits the named uncommitted worktree content to be sent to AGY.

This permission excludes credentials, tokens, `.env` files, unrelated repositories, and arbitrary files
elsewhere in the operator's home directory. A conductor must keep those exclusions out of the worker's
inputs and working scope.
