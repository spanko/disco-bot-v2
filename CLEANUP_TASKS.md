# Discovery Bot v2 — Repo Cleanup

## Priority Order
Work through these in order. Run `dotnet build` after each section to confirm nothing breaks.

---

## 1. CRITICAL — Security: Delete Leaked Secrets

The file `settings.json` in the repo root contains plaintext Azure Storage account keys and App Insights instrumentation keys. It was likely created by `func azure functionapp fetch-app-settings` and is NOT used by the application at runtime. It must be deleted and the leaked key rotated.

```powershell
# Delete the file
git rm settings.json

# Add to .gitignore so it never comes back
Add-Content .gitignore "`nsettings.json"

# Commit immediately
git add .gitignore
git commit -m "SECURITY: Remove leaked credentials from settings.json"
git push

# Rotate the compromised storage account key
az storage account keys renew --account-name discdevfunc3xr5ve --resource-group discovery-dev --key key1
```

The function app uses managed identity for storage access (`AzureWebJobsStorage__accountName`), so rotating the key won't break anything.

---

## 2. Remove Unused NuGet Packages

These packages are in `src/DiscoveryAgent/DiscoveryAgent.csproj` but have zero references anywhere in the codebase. Remove all five:

| Package | Why it's unused |
|---------|----------------|
| `Microsoft.Bot.Builder` | No Bot Framework code exists. No Teams adapter in Program.cs. Zero `using` statements. |
| `Microsoft.Bot.Builder.Integration.AspNet.Core` | Same — zero references anywhere. |
| `Azure.Security.KeyVault.Secrets` | No `SecretClient`, no `GetSecret` calls. Key Vault is provisioned but never accessed from code. |
| `Azure.AI.DocumentIntelligence` | No `AnalyzeDocument`, no document analysis calls anywhere. |
| `Microsoft.Extensions.Logging.ApplicationInsights` | Legacy App Insights logging package. Replaced by `Azure.Monitor.OpenTelemetry.AspNetCore` which is already in the csproj. Zero references to the old package. |

Remove these lines from the csproj:
```xml
<!-- REMOVE these five -->
<PackageReference Include="Microsoft.Bot.Builder" Version="4.*" />
<PackageReference Include="Microsoft.Bot.Builder.Integration.AspNet.Core" Version="4.*" />
<PackageReference Include="Azure.Security.KeyVault.Secrets" Version="4.*" />
<PackageReference Include="Azure.AI.DocumentIntelligence" Version="1.*" />
<PackageReference Include="Microsoft.Extensions.Logging.ApplicationInsights" Version="2.*" />
```

After removing, run `dotnet build` to confirm no compilation errors.

Also fix the misleading version specifier:
```xml
<!-- CHANGE this -->
<PackageReference Include="Azure.AI.Extensions.OpenAI" Version="1.0.0-beta.*" />
<!-- TO this (matches what actually resolves) -->
<PackageReference Include="Azure.AI.Extensions.OpenAI" Version="2.0.0-beta.*" />
```

Commit: `chore: Remove 5 unused NuGet packages, fix Extensions.OpenAI version specifier`

---

## 3. Remove Unused Bicep Modules

Three infrastructure modules are provisioned but never used by the application.

### 3a. Remove Bot Service
- Delete `infra/modules/bot-service.bicep`
- Remove the bot service module block from `infra/main.bicep` (the `module botService` block around line 152-162)
- Remove the `output BOT_NAME` line from `infra/main.bicep`

### 3b. Remove Key Vault
- Delete `infra/modules/key-vault.bicep`
- Remove the key vault module block from `infra/main.bicep` (the `module keyVault` block around line 114-122)
- Remove `keyVaultId: keyVault.outputs.keyVaultId` from the RBAC module params in `infra/main.bicep`
- Remove the `output KEY_VAULT_URI` line from `infra/main.bicep`
- In `infra/modules/role-assignments.bicep`: remove the `param keyVaultId string` parameter, the `keyVaultSecretsUser` variable, and the `funcKv` role assignment resource

### 3c. Remove Static Web App
- Delete `infra/modules/static-web-app.bicep`
- Remove the static web app module block from `infra/main.bicep` (the `module staticWebApp` block around line 165-173)
- Remove the `output STATIC_WEB_APP_URL` line from `infra/main.bicep`

After these removals, verify `infra/main.bicep` still has valid references — no dangling module outputs or missing parameters.

Commit: `infra: Remove unused Bot Service, Key Vault, and Static Web App modules`

**Note:** These resources may still exist in your dev resource group. They won't be deleted by removing the Bicep — you'll need to delete them manually from the Azure portal or CLI if you want to clean up the live environment:
```powershell
# Optional: delete the live resources (only if you want to clean up Azure costs)
az bot delete --name discdev-bot-SUFFIX --resource-group discovery-dev
az keyvault delete --name discdev-kv-SUFFIX --resource-group discovery-dev
az staticwebapp delete --name discdev-web-SUFFIX --resource-group discovery-dev
```

---

## 4. Clean Up Debug Artifacts

### 4a. Remove or secure the DiagDI endpoint
In `src/DiscoveryAgent/Functions/HealthFunction.cs`, the `DiagDI` function endpoint exposes the full DI service dependency graph to anyone with a function key. Two options:

**Option A (recommended): Remove it entirely.** The DI issues are resolved. Delete the `DiagDI` method and the `TryResolve` helper method. Keep only the `Health` function. Remove the unused `using` statements that were only needed for DiagDI (`Azure.AI.Projects`, `Azure.Search.Documents`, `Azure.Storage.Blobs`, `DiscoveryAgent.Configuration`, `DiscoveryAgent.Core.Interfaces`, `Microsoft.Azure.Cosmos`).

The simplified HealthFunction.cs should be:
```csharp
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;

namespace DiscoveryAgent.Functions;

public class HealthFunction
{
    [Function("Health")]
    public IActionResult Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "health")] HttpRequest req)
        => new OkObjectResult(new { status = "healthy", timestamp = DateTime.UtcNow });
}
```

**Option B: Keep it but lock it down.** Change `AuthorizationLevel.Function` to `AuthorizationLevel.Admin` so only the master key works, not the default function key.

### 4b. Organize test data
Move `test/data/` contents to `docs/examples/`:
```powershell
mkdir -p docs/examples
git mv test/data/context-tech-consulting-001.json docs/examples/
git mv test/data/questionnaire-tech-consulting-001.json docs/examples/
Remove-Item -Recurse test/data  # if empty after move
```

Commit: `chore: Remove DiagDI debug endpoint, organize test data`

---

## 5. Clean Up Empty Projects and Stale Files

### 5a. Handle the empty test project
`tests/DiscoveryAgent.Tests/` has a csproj with xunit + Moq dependencies but zero test files. Two options:

**Option A (recommended for now): Delete it.** No tests exist. Remove it from the solution file too.
```powershell
Remove-Item -Recurse tests/
# If discovery-chatbot.sln exists, also remove the project reference from it
```

**Option B: Keep it but add at least one smoke test** that validates the DomainModels serialize/deserialize correctly. This is the minimum viable test.

### 5b. Verify .gitignore is complete
Add these entries if missing:
```
settings.json
*.publish.xml
publish/
```

Commit: `chore: Remove empty test project, update .gitignore`

---

## 6. Verify Bicep Integrity

After all Bicep changes, verify the template is still valid:
```powershell
az bicep build --file infra/main.bicep
```

This will catch any dangling references to removed modules or missing parameters.

---

## Summary of Files Changed

| Action | File |
|--------|------|
| DELETE | `settings.json` |
| DELETE | `infra/modules/bot-service.bicep` |
| DELETE | `infra/modules/key-vault.bicep` |
| DELETE | `infra/modules/static-web-app.bicep` |
| DELETE | `tests/` (entire directory) |
| EDIT | `src/DiscoveryAgent/DiscoveryAgent.csproj` (remove 5 packages, fix version) |
| EDIT | `infra/main.bicep` (remove 3 modules + outputs) |
| EDIT | `infra/modules/role-assignments.bicep` (remove Key Vault param + role) |
| EDIT | `src/DiscoveryAgent/Functions/HealthFunction.cs` (remove DiagDI) |
| EDIT | `.gitignore` (add settings.json, publish artifacts) |
| MOVE | `test/data/*.json` → `docs/examples/` |

## Expected Commit History
1. `SECURITY: Remove leaked credentials from settings.json`
2. `chore: Remove 5 unused NuGet packages, fix Extensions.OpenAI version specifier`
3. `infra: Remove unused Bot Service, Key Vault, and Static Web App modules`
4. `chore: Remove DiagDI debug endpoint, organize test data`
5. `chore: Remove empty test project, update .gitignore`
