## Summary

<!-- What does this change and why? Link the issue it closes, e.g. "Closes #12". -->

## Type of change

- [ ] Conversion fix (the output changes)
- [ ] New feature
- [ ] Validation (new or changed checks / known exceptions)
- [ ] Documentation
- [ ] Build, CI or dependencies

## Effect on the output

<!-- Required when the output changes. Which source model (DPM 1.0, DPM 2.0, both)?
     Rows added/removed per target table for the releases you measured. -->

## Evidence

<!-- For conversion or validation changes: the Access rows, layout cells or metamodel definitions
     that justify the change. A difference against another SQLite export is not enough on its own. -->

## Checklist

- [ ] `dotnet build` passes with no warnings
- [ ] The fast test suite passes (`dotnet test` without data files)
- [ ] If `src/EbaDpm.Converter.Core` or a dependency it uses changed: the full data-dependent suite passes, run once after the last change (`EBADPM_TEST_DATA` set), with the result stated above
- [ ] Otherwise: any data test added or changed was run (`--filter`)
- [ ] Tests added or updated (a fix comes with a test that fails without it)
- [ ] Documentation updated where behaviour changed
- [ ] `CHANGELOG.md` updated under **Unreleased** (if user-visible)
