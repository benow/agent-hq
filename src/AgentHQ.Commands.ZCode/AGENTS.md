# AgentHQ.Commands.ZCode — feature knowledge

The `/plan` command pack (`Commands/*.md`) and the installer (`Installer.cs`, `Runner.cs`) that puts
it, and the hook, into a ZCode installation.

Behaviour is documented in [docs/features/zcode-integration.md](../../docs/features/zcode-integration.md)
and the command meanings in [docs/features/plan-lifecycle.md](../../docs/features/plan-lifecycle.md#the-plan-command-reference).
This file records the format constraints.

## Command templates

- One file per command, named `<subcommand>.md` — the filename is what ZCode turns into
  `/plan <subcommand>`. An orphan file (a template with no state-machine entry) installs fine and
  then always fails at ingest; a missing file means the command does not exist.
- **The first line must be the version marker** `<!-- agent-hq v<N> -->`. The installer uses it to
  decide what it may overwrite: a file whose first line lacks the marker is treated as user-edited
  and left alone. Do not put anything above it, and do not remove it.
- Body: a `---` front-matter block with `description:`, then the prompt text. `$ARGUMENTS` receives
  whatever the user typed after the command, which is how `<file>` (and the optional `<reason>`)
  reaches Agent HQ — the hook posts the *expanded* prompt, and `PlanCommand.TryParse` reads the
  first whitespace token as the file.
- **The reason request must match `PlanCommand.TakesReason`** (`sendback`, `fail`, `block`,
  `defer`, `abandon`). A template that asks for a reason under another command loses it silently.
- **A template must not claim a transition it cannot cause.** `/plan verify` only enters
  verification; `/plan pass` and `/plan fail` close it. `/plan implement` must explicitly tell the
  agent to run `/plan implemented` afterwards. This split is what keeps the claims auditable.
- Wording is behaviour here: the templates are where the evidence requirements (commits, test
  results, lessons consulted, no self-verification) actually get enforced.

## Versioning a change

Bump `Installer.CommandVersion` **and** the markers in the changed templates together. Files that
still have to be left alone because the user edited them are reported as warnings and the installer
exits `2` (not an error — a deliberate, visible partial install).

## The installer

1. Copies `Commands/*.md` from `AppContext.BaseDirectory/Commands` to `~/.zcode/commands/plan/`
   (user scope, so the commands exist in every project). Writes only when the destination is absent,
   carries a marker, or is a legacy-name file whose body is unchanged.
2. Merges the hook into `~/.zcode/settings.json` under `hooks.UserPromptSubmit`. The entry is
   identified by its exact command string; re-running is a no-op. Unrelated keys and hooks are
   preserved — the merge must never rewrite the whole file from a partial parse. `|| true` at the
   end of the command guarantees a stopped Agent HQ never blocks prompting.
3. Appends the `<!-- agent-hq -->` section to the repository's `AGENTS.md`, once.

**That snippet is shipped in two places** — `Installer.cs` and this repository's root `AGENTS.md`
(added by running the installer on itself). If you change one, change the other, or the next
install on this repo will produce a second, different section. The test asserts marker-guarded
idempotency, not content parity.

## Legacy name

`dev-bridge` is the previous product name. The installer deliberately recognises the older marker
(`<!-- dev-bridge v<N> -->`): an unchanged legacy file is upgraded, an edited one is left alone.
This is the one place besides the deploy script where the old name is allowed to appear.

## Testing

`InstallerTests` runs the installer against a fake `HOME` twice: the second run must change nothing,
`settings.json` must keep unrelated keys, and edited templates must be preserved. Any new install
behaviour needs a case there.
