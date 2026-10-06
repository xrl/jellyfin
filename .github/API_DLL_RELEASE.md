# API DLL release assets

`API DLL release` builds the existing `v12.1-rpi.N` tag only if its commit is an ancestor of fork `main`. It never creates, moves or falls back from a tag. The build uses the digest-pinned .NET 10.0.401 SDK, Debug API tests/analyzers, Release API tests and full format verification. Only ordinary AnyCPU `Jellyfin.Api.dll` is shipped, after identity/reference comparison with the pinned official 12.1 assembly.

Assets are `Jellyfin.Api.dll`, `Jellyfin.Api.dll.sha256` and `Jellyfin.Api.provenance.json` (source tag/SHA, SDK, base digest, checksum and assembly/reference inventory). An existing release is accepted only when all three assets match exactly; no overwrite/delete is performed. Public-read anonymous downloads are checked after publication. Release write permission exists only in the publish job, never PR validation.

After this workflow is reviewed and merged to `main`, with fork Actions enabled/registered, publish the already-existing tag without recreating it:

```sh
gh workflow run api-dll-release.yml --repo xrl/jellyfin --ref main -f tag=v12.1-rpi.1
```

New matching tags also trigger the workflow once its code is in their lineage. A PR validates the current `v12.1-rpi.1` tagged source without publishing. Manual publication is restricted to the workflow on `main`. The workflow produces a prerelease; this is a DLL transport, not image publication or deployment.

`v12.1-rpi.1` retains the known denied-audio HTTP500 defect (the policy throws the wrong SecurityException type). This workflow does not fix or hide that behavior. Native architecture/device media acceptance remains separate. Review actual release checksums/provenance and pin them in the image consumer; never assume a checksum from an earlier feature commit is the merged-tag checksum.
