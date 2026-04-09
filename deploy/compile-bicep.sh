#!/usr/bin/env bash
# =============================================================================
# deploy/compile-bicep.sh — Compile Bicep to ARM JSON
#
# Usage: ./deploy/compile-bicep.sh
#
# Produces infra/main.json for environments that can't run Bicep directly
# (e.g., ARM template deployments, Azure DevOps tasks that only accept JSON).
# =============================================================================

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/.." && pwd)"

source "${SCRIPT_DIR}/_helpers.sh"

log_step "Compiling Bicep to ARM JSON"

# Ensure Bicep CLI is available
if ! az bicep version &>/dev/null 2>&1; then
    log_info "Installing Bicep CLI..."
    az bicep install
fi

# Compile main template
az bicep build \
    --file "${REPO_ROOT}/infra/main.bicep" \
    --outfile "${REPO_ROOT}/infra/main.json"

log_ok "Compiled: infra/main.json ($(wc -c < "${REPO_ROOT}/infra/main.json") bytes)"

# Compile management plane template
if [[ -f "${REPO_ROOT}/infra/management-plane.bicep" ]]; then
    az bicep build \
        --file "${REPO_ROOT}/infra/management-plane.bicep" \
        --outfile "${REPO_ROOT}/infra/management-plane.json"

    log_ok "Compiled: infra/management-plane.json"
fi

echo ""
log_info "ARM JSON templates are ready for deployment."
log_info "These files are gitignored — regenerate after any Bicep changes."
