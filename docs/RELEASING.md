# Releasing Curtain

This document describes the maintainer release flow. Pushing source changes to `main` is not a software release.

## Distribution channels

- **GitHub repository:** canonical public source history and version tags.
- **GitHub Releases:** version page, release notes, source snapshot, and a convenient installer mirror.
- **Cloudflare R2:** the existing public beta download endpoint and the Velopack feed used by the in-app updater: `https://curtain-updates.cheneylab.cn/beta`.

The GitHub Release and R2 feed serve different purposes. They do not conflict. Only release artifacts need to be published; ordinary source commits do not require an R2 upload.

## Version and source mapping

1. Use the app/package version format accepted by `scripts/B3-Pack.ps1`, for example `0.2.0-beta.6`.
2. Create the matching Git tag only after the intended source commit has passed validation and the resulting publish output has been launched and tested.
3. Use a matching tag such as `v0.2.0-beta.6` for the GitHub Release.
4. Do not retroactively tag the existing `0.2.0-beta.5.1` binary as built from the initial public source snapshot: that source-to-binary relationship has not been confirmed.
5. Record the exact source commit SHA in the release notes.

## Build and validation

1. Start from a clean checkout of the exact commit intended for release.
2. Run `python scripts/static-check.py`.
3. Run `dotnet run --project tests/Curtain.Core.Tests/Curtain.Core.Tests.csproj -c Release`.
4. Build/publish Release x64 using the current packaging procedure.
5. Run the published `Curtain.App.exe` directly and confirm the main UI and core interactions work before packaging.
6. Confirm that the project `LICENSE`, `THIRD-PARTY-NOTICES.md`, and archived third-party license/NOTICE texts are present in the publish output and the full package.
7. Calculate SHA-256 for the final Setup.exe and retain it in the release report.

## Publish the R2 update feed

The app's current beta update feed is `https://curtain-updates.cheneylab.cn/beta`. The existing R2 prefix contains the Velopack `releases.win.json` index and versioned `.nupkg` objects, alongside versioned Setup.exe downloads.

1. Use `scripts/B3-Pack.ps1` to generate the release output. Its R2 feed output is local under `artifacts/B3/r2-feed-beta`; its installer copy is under `artifacts/B3/installer-<version>`.
2. Upload the generated versioned full/delta package objects to the existing `beta/` prefix.
3. Upload the generated `releases.win.json` only after the package objects it references are available.
4. Upload the versioned Setup.exe to the existing public download prefix.
5. Preserve existing versioned objects. Do not empty or replace the entire `beta/` prefix; the current feed must continue to serve clients on earlier beta versions.
6. Verify the public index and each referenced object after upload, and confirm the latest entry is the intended version.

Do not embed R2 credentials in the application or public repository.

## Publish the GitHub Release

1. Push the validated version tag.
2. Create a GitHub Release from that exact tag with concise user-facing notes: features, fixes, known limitations, installer architecture, and the source commit SHA.
3. Attach the same Setup.exe as a convenient mirror and include its SHA-256. Compare its SHA-256 with the R2 copy; the files must be byte-identical.
4. Keep the R2 feed as the in-app updater source. A GitHub Release does not replace the Velopack feed.
5. Update the bilingual README download link and, after the official website is live, its download link to the current version.
6. Perform one final smoke test from the public download link.

## Synchronization rule

- **Ordinary code change:** commit and push to GitHub; no R2 upload and no Release.
- **Versioned release:** tag the validated source commit, publish the Velopack feed to R2, publish the matching GitHub Release and installer mirror, then update the current download links.
- The R2 feed and GitHub installer mirror are two copies of the same release artifact, not two independent versions. Verify matching SHA-256 values to prevent drift.

The current workflow is a maintainer-operated process. Automation can be added later after the manual release flow has been verified end to end.
