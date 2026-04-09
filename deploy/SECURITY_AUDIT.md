# Security Audit Report — disco-bot-v2

**Date:** 2026-04-09  
**Scope:** Full repository scan (source, infrastructure, Docker, CI/CD, git history)  
**Method:** Manual audit + pattern-based scanning (semgrep unavailable — egress-blocked)

---

## Summary

| Severity | Count | Status |
|----------|-------|--------|
| 🔴 Critical | 1 | Requires immediate fix |
| 🟠 High | 3 | Fix before production |
| 🟡 Medium | 4 | Fix during cleanup |
| 🔵 Low | 3 | Recommended improvements |

---

## 🔴 Critical

### C1. Hardcoded endpoints in `appsettings.json`

**File:** `src/DiscoveryAgent/appsettings.json`  
**Risk:** Leaks dev environment resource names (Cosmos account, Storage account, AI Search, Foundry endpoint) to anyone with repo access.  
**Impact:** Attacker can enumerate Azure resource names for reconnaissance.  
**Fix:** Replace all values with empty strings. The app reads config from environment variables at runtime (`DiscoveryBotSettings.FromEnvironment()`), so `appsettings.json` is never used in production. Keep it as a schema reference with placeholder values only.

```json
{
  "DiscoveryBot": {
    "ProjectEndpoint": "",
    "AgentName": "discovery-agent",
    "ModelDeploymentName": "gpt-4o",
    "CosmosEndpoint": "",
    "CosmosDatabase": "discovery",
    "StorageEndpoint": "",
    "AiSearchEndpoint": "",
    "KnowledgeIndexName": "knowledge-items",
    "InstructionsPath": "config/instructions.md"
  }
}
```

---

## 🟠 High

### H1. Docker containers run as root

**Files:** `Dockerfile`, `Dockerfile.management`  
**Risk:** If the container is compromised, the attacker has root access inside the container.  
**Fix:** Add a non-root user to both Dockerfiles:

```dockerfile
FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS runtime
RUN groupadd -r appuser && useradd -r -g appuser -s /sbin/nologin appuser
WORKDIR /app
COPY --from=build /app .
USER appuser
```

### H2. Error messages leak internal details in non-dev mode

**Files:** Multiple handlers  
**Risk:** Several endpoints return `ex.Message` directly even in production:
- `LightweightConversationHandler.cs:218` — returns raw exception message
- `ConversationHandler.cs:355` — returns raw exception message  
- `Program.cs:669` (test runner) — always returns `{ex.GetType().Name}: {ex.Message}`
- `StampManager.cs:170` — stores `ex.Message` in stamp record (less critical but still leaks)

**Fix:** Wrap all error responses in a consistent handler that strips internal details in production. The conversation endpoint in `Program.cs:326` already does this correctly — apply the same pattern everywhere.

### H3. No CORS policy configured

**File:** `src/DiscoveryAgent/Program.cs`  
**Risk:** The API accepts requests from any origin. If the app URL is public, any website can make cross-origin requests.  
**Fix:** Add CORS middleware:

```csharp
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.WithOrigins($"https://{Environment.GetEnvironmentVariable("ALLOWED_ORIGIN") ?? "*"}")
              .AllowAnyHeader()
              .AllowAnyMethod();
    });
});
// ...
app.UseCors();
```

---

## 🟡 Medium

### M1. Hardcoded ACR fallback in FleetMonitor

**File:** `src/ManagementPlane/Services/FleetMonitor.cs:35`  
**Code:** `_sourceAcrName = Environment.GetEnvironmentVariable("SOURCE_ACR_NAME") ?? "discodevacrvjnr3y";`  
**Risk:** Environment-specific fallback baked into source code.  
**Fix:** Remove the fallback. Require `SOURCE_ACR_NAME` as a mandatory env var.

### M2. Hardcoded resource names in GitHub Actions workflows

**Files:** `.github/workflows/deploy.yaml`, `.github/workflows/deploy-management.yaml`  
**Risk:** Workflows are coupled to the `discodev` environment. Can't be reused for other environments.  
**Fix:** Replace with repository variables or use the new `deploy/` scripts:

```yaml
env:
  ACR_NAME: ${{ vars.ACR_NAME }}
  IMAGE_NAME: discovery-bot
  ACA_NAME: ${{ vars.ACA_NAME }}
  RESOURCE_GROUP: ${{ vars.RESOURCE_GROUP }}
```

### M3. `dev.bicepparam` contains deployer's personal Object ID

**File:** `infra/params/dev.bicepparam`  
**Value:** `d9a4dcc0-50de-4c43-b46b-4d81233e3b1b`  
**Risk:** Personal Azure AD Object ID in source control.  
**Fix:** Move to a `.gitignore`-d file or use `az ad signed-in-user show --query id -o tsv` at deploy time (which `deploy/provision.sh` already handles).

### M4. JWT signing key passed as plain-text ACA env var

**File:** `infra/modules/container-app.bicep`  
**Risk:** `JWT_SIGNING_KEY` is stored as a plain environment variable on the container. Visible in Azure portal, CLI, and deployment logs.  
**Fix:** Store in Key Vault and reference as a Key Vault secret in the ACA config:

```bicep
{ name: 'JWT_SIGNING_KEY', secretRef: 'jwt-signing-key' }
```

---

## 🔵 Low

### L1. `Newtonsoft.Json` and `System.Text.Json` both referenced

**File:** `src/DiscoveryAgent/DiscoveryAgent.csproj`  
**Risk:** Inconsistent JSON handling, potential deserialization differences.  
**Fix:** Remove `Newtonsoft.Json` if not used. The codebase appears to use only `System.Text.Json`.

### L2. Stale documentation files in repo root

**Files:** `CLAUDE_CODE_INSTRUCTIONS.md`, `CLAUDE_CODE_SDK_ANSWERS.md`, `CLEANUP_TASKS.md`, `FOUNDRY_FUNCTION_CALL_REFERENCE.md`, `OBSERVABILITY_IMPLEMENTATION_PLAN.md`  
**Risk:** Confusing for new developers. Contains stale references to old architecture, personal FQDNs, and debugging notes.  
**Fix:** Move to `docs/internal/` or delete. Keep only `README.md` in the root.

### L3. `System.Text.Json` version pinned to v10.* in main project, v9.* in Core

**Files:** `src/DiscoveryAgent/DiscoveryAgent.csproj`, `src/DiscoveryAgent.Core/DiscoveryAgent.Core.csproj`  
**Risk:** Version skew between projects could cause subtle serialization issues.  
**Fix:** Align both to the same major version.

---

## Items Already Done Well

- ✅ `DefaultAzureCredential` used everywhere — no connection strings or keys in code
- ✅ Cosmos DB has both management-plane and data-plane RBAC
- ✅ Health probes configured on ACA
- ✅ `.gitignore` covers `local.settings.json`, `settings.json`, `*.patch`, publish artifacts
- ✅ No secrets found in git history
- ✅ Auth middleware skips health endpoints correctly
- ✅ Input validation on conversation endpoint (checks for null/empty message)
- ✅ File upload has size limits (20MB) and content-type allowlist
- ✅ Environment variable–based config (not hardcoded connection strings)
- ✅ Workload identity federation for CI/CD (no stored credentials in GitHub)
