# Discovery Bot v2 — Deployment Scripts

Self-contained deployment scripts for a **single instance** of Discovery Bot v2.  
No dependencies on the GitHub Actions workflows, no hardcoded environment names.  
Designed to work from a fresh clone in any Git host (GitHub, ADO, etc.).

## Prerequisites

| Tool | Version | Check |
|------|---------|-------|
| Azure CLI | 2.60+ | `az version` |
| .NET SDK | 9.0+ | `dotnet --version` |
| Docker | 24+ | `docker --version` |
| Bicep CLI | 0.25+ | `az bicep version` |

You must be logged into Azure: `az login` and have **Owner** or **Contributor + User Access Administrator** on the target subscription.

## Quick Start (New Environment)

```bash
# 1. Create your parameter file from the template
cp deploy/params/template.env deploy/params/myenv.env
# Edit myenv.env with your values (prefix, location, deployer ID, etc.)

# 2. Provision all Azure infrastructure
./deploy/provision.sh myenv

# 3. Build & deploy the container image
./deploy/build-and-deploy.sh myenv

# 4. Verify
./deploy/verify.sh myenv
```

## Scripts

| Script | Purpose |
|--------|---------|
| `provision.sh` | Creates resource group + all Azure resources via Bicep |
| `build-and-deploy.sh` | Builds Docker image, pushes to ACR, updates ACA |
| `verify.sh` | Smoke tests health endpoints and prints status |
| `teardown.sh` | Destroys the resource group (irreversible) |
| `compile-bicep.sh` | Compiles `main.bicep` → `main.json` for ARM-only environments |

## Parameter Files

Each environment gets a `.env` file in `deploy/params/`. See `template.env` for all options.  
The `.env` format was chosen over `.bicepparam` for portability — the scripts read `.env` and pass values as `--parameters` to `az deployment`.

## ADO Pipeline

`azure-pipelines.yml` is a drop-in ADO pipeline that mirrors the GitHub Actions workflow but reads from ADO variable groups instead of GitHub secrets.

## Security Notes

- No secrets are stored in any file — all sensitive values come from environment variables or Azure Key Vault
- The `appsettings.json` in the source is **not used at runtime** — all config comes from ACA environment variables set by Bicep
- Docker images run as non-root user (`app`)
- All Azure access uses managed identity (DefaultAzureCredential)
