# Repository setup on GitHub (maintainers)

Most of the collaboration infrastructure lives in the repository (`.github/`), but some settings
only exist on GitHub. This is the one-time checklist after creating the repository.

## 1. Replace the placeholders

`OWNER` stands for the GitHub user or organisation that owns the repository:

- [`.github/CODEOWNERS`](../.github/CODEOWNERS): `@OWNER` → `@your-user` (or `@org/team`).
- [`README.md`](../README.md) badges and [`docs/releasing.md`](releasing.md): `OWNER/EbaDpm.Converter`.
- [`CODE_OF_CONDUCT.md`](../CODE_OF_CONDUCT.md): `[CONTACT]` → the address that receives
  code-of-conduct reports.

## 2. General settings

*Settings → General*

- **Pull Requests**: allow **squash merging** only (disable merge commits and rebase merging);
  default commit message: *Pull request title and description*.
- Enable **Always suggest updating pull request branches** and
  **Automatically delete head branches**.
- **Features**: keep Issues enabled; enable Discussions if you want questions out of the issue
  tracker.

## 3. Rulesets

*Settings → Rules → Rulesets → New ruleset → Import a ruleset*, and import both files:

| File | Effect |
|---|---|
| [`.github/rulesets/main-branch.json`](../.github/rulesets/main-branch.json) | `main` only changes through pull requests: 1 approval (code owner), conversations resolved, CI (`Build and test`) and CodeQL (`Analyze (C#)`) green and up to date, squash merge, linear history, no force push or deletion |
| [`.github/rulesets/release-tags.json`](../.github/rulesets/release-tags.json) | Tags `v*` cannot be moved or deleted |

The main-branch ruleset lets the **repository admin role** bypass the rules *through a pull
request*. With a single maintainer this is what allows merging your own pull requests (GitHub
does not let authors approve their own pull requests); checks still run. Remove the bypass once
there is a second maintainer who can review.

Import the rulesets **after** CI and CodeQL have run at least once (push the initial commit
first), so the status checks are known to GitHub.

## 4. Actions

*Settings → Actions → General*

- **Actions permissions**: allow actions created by GitHub and verified creators (or *all actions*
  if you prefer); the workflows only use `actions/*` and `github/codeql-action`.
- **Workflow permissions**: *Read repository contents and packages permissions* (the default).
  The release workflow asks for `contents: write` explicitly, only for its own job.
- **Fork pull request workflows**: require approval for first-time contributors.

## 5. The `release` environment

*Settings → Environments → New environment* → name it **`release`**:

- **Required reviewers**: add yourself (and future maintainers). Every run of the release workflow
  then waits for an explicit approval before it builds and publishes anything.
- **Deployment branches and tags**: *Selected branches* → `main`.

If the environment is not created, GitHub creates it automatically on the first release run, but
without protection.

## 6. Security

*Settings → Code security*

- Enable **Private vulnerability reporting** (used by [`SECURITY.md`](../SECURITY.md)).
- Enable **Dependabot alerts** and **Dependabot security updates**. Version updates are configured
  in [`.github/dependabot.yml`](../.github/dependabot.yml).
- Enable **Secret scanning** and **Push protection**.
- **Code scanning** is already configured by the CodeQL workflow; nothing to enable.

## 7. Labels

The issue forms and the release-notes categories use these labels. Create the ones that do not
exist (*Issues → Labels*): `bug`, `enhancement`, `documentation` (default) and `triage`,
`mapping`, `validation`, `dependencies`, `ci`, `breaking-change`, `skip-changelog`.

## 8. First release

With everything above in place, follow [releasing.md](releasing.md). For the first publication of
the current version (`1.1.0`, already in `CHANGELOG.md`), step 4 of the procedure is enough: run
the *Release* workflow on `main`.
