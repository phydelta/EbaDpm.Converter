# Releasing

Releases are built and published by GitHub Actions, on demand, from `main`. Nobody builds a
release executable on a local machine, and tags are never pushed by hand.

## Versioning

The project follows [Semantic Versioning](https://semver.org/):

| Change | Version bump |
|---|---|
| Output format changes incompatibly (schema DDL, removed columns or tables, changed meaning of a value), or a CLI option is removed or changes meaning | **major** |
| New EBA release or source model supported, new CLI option, new validation checks, output fixes that add or correct rows | **minor** |
| Fixes that do not change the output of a correct conversion, documentation, dependencies | **patch** |

Because the output is a dictionary consumed by other tools, a fix that changes the emitted rows is
at least a **minor** release, and its `CHANGELOG.md` entry states the effect per table.

The single source of the version is `<Version>` in [`Directory.Build.props`](../Directory.Build.props).
Pre-releases use a SemVer suffix (`1.2.0-rc.1`).

## Release procedure

1. **Open a release pull request** from a branch named `release/X.Y.Z`:
   - set `<Version>X.Y.Z</Version>` in `Directory.Build.props`;
   - in `CHANGELOG.md`, rename the `## [Unreleased]` section to `## X.Y.Z - YYYY-MM-DD` and add a
     new, empty `## [Unreleased]` section above it.
2. **Run the full data-dependent test suite locally** (CI does not have the EBA data files) and
   state the result in the pull request:
   ```powershell
   $env:EBADPM_TEST_DATA = "D:\eba-data"
   dotnet test
   ```
3. **Merge** the pull request once CI is green and it is approved.
4. **Run the release workflow**: *Actions → Release → Run workflow*, on branch `main`. Tick
   *prerelease* for versions with a suffix, or *draft* to review the release before it becomes
   public.
5. If the `release` environment requires approval, a maintainer approves the pending deployment.

The workflow then:

1. reads the version from `Directory.Build.props` and refuses to continue if it is not valid
   SemVer, if the tag `vX.Y.Z` already exists, or if `CHANGELOG.md` has no non-empty section for it;
2. builds the solution and runs the test suite;
3. publishes the self-contained single-file executable (`win-x64`) and packages it with
   `LICENSE`, `README.md` and `CHANGELOG.md` as `EbaDpm.Converter-X.Y.Z-win-x64.zip`;
4. writes `SHA256SUMS.txt` and a build provenance attestation for the zip;
5. creates the tag `vX.Y.Z` on the commit it built and the GitHub release, with the
   `CHANGELOG.md` section as release notes.

## Verifying a downloaded release

```powershell
# Checksum
Get-FileHash .\EbaDpm.Converter-X.Y.Z-win-x64.zip -Algorithm SHA256   # compare with SHA256SUMS.txt

# Provenance: proves the file was built by this repository's release workflow
gh attestation verify .\EbaDpm.Converter-X.Y.Z-win-x64.zip --repo OWNER/EbaDpm.Converter
```

## Fixing a release

Tags `v*` are protected and cannot be moved or deleted. If a release is broken, fix it on `main`
and publish a new patch version. A release that should not be used can be edited on GitHub to
say so, or marked as a pre-release.
