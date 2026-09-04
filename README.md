# Entorn .NET scanner

First-party scanner for .NET solution/project structure, ASP.NET HTTP endpoints, messaging, data dependencies, configured HTTP targets, and source ownership.

The worker implements the language-neutral [`scanner/v1` protocol](https://github.com/Entorn-dev/entorn-scanner-contracts). Its historical `archie.dotnet` scanner identity remains unchanged in the first Entorn release so existing installations and deterministic observations remain compatible during the wider product rename.

## Build and test

```bash
dotnet restore --locked-mode
dotnet test --no-restore --configuration Release
```

## Package

```bash
scripts/package-linux-x64.sh 1.3.0
```

The script produces a deterministic `tar.gz` and SHA-256 file under `artifacts/`. The archive contains no signing key. An approved release is signed offline and admitted to the signed scanner catalog separately.

The scanner reads repository files but does not execute target code, use the network, or inherit repository-controlled environment configuration.

## License and contributions

Licensed under Apache-2.0. Contributions require Developer Certificate of Origin sign-off; see [CONTRIBUTING.md](CONTRIBUTING.md).
