#!/usr/bin/env bash
# =============================================================================
# deploy/_helpers.sh — Shared functions for all deployment scripts
# Source this file: source "$(dirname "$0")/_helpers.sh"
# =============================================================================

set -euo pipefail

# Colors
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
BLUE='\033[0;34m'
NC='\033[0m' # No Color

log_info()  { echo -e "${BLUE}[INFO]${NC}  $*"; }
log_ok()    { echo -e "${GREEN}[OK]${NC}    $*"; }
log_warn()  { echo -e "${YELLOW}[WARN]${NC}  $*"; }
log_error() { echo -e "${RED}[ERROR]${NC} $*" >&2; }
log_step()  { echo -e "\n${BLUE}━━━ $* ━━━${NC}"; }

# ── Load environment file ────────────────────────────────────────────────────

load_env() {
    local env_name="${1:?Usage: load_env <env-name>}"
    local script_dir
    script_dir="$(cd "$(dirname "${BASH_SOURCE[1]}")" && pwd)"
    local env_file="${script_dir}/params/${env_name}.env"

    if [[ ! -f "$env_file" ]]; then
        log_error "Environment file not found: $env_file"
        log_info  "Create one from the template: cp deploy/params/template.env deploy/params/${env_name}.env"
        exit 1
    fi

    log_info "Loading environment: ${env_name} (${env_file})"

    # Source the env file (skip comments and blank lines)
    set -a
    # shellcheck disable=SC1090
    source <(grep -v '^\s*#' "$env_file" | grep -v '^\s*$')
    set +a

    # Validate required fields
    local missing=()
    [[ -z "${PREFIX:-}" ]]              && missing+=("PREFIX")
    [[ -z "${SUFFIX:-}" ]]              && missing+=("SUFFIX")
    [[ -z "${LOCATION:-}" ]]            && missing+=("LOCATION")
    [[ -z "${DEPLOYER_OBJECT_ID:-}" ]]  && missing+=("DEPLOYER_OBJECT_ID")
    [[ -z "${SUBSCRIPTION_ID:-}" ]]     && missing+=("SUBSCRIPTION_ID")

    if [[ ${#missing[@]} -gt 0 ]]; then
        log_error "Missing required parameters in ${env_file}:"
        for m in "${missing[@]}"; do log_error "  - $m"; done
        exit 1
    fi

    # Derived values
    RESOURCE_GROUP="${RESOURCE_GROUP_NAME:-${PREFIX}-${SUFFIX}-rg}"
    BASE_NAME="${PREFIX}${SUFFIX}"
    CONVERSATION_MODE="${CONVERSATION_MODE:-lightweight}"
    AUTH_MODE="${AUTH_MODE:-none}"
    MODEL_DEPLOYMENT_NAME="${MODEL_DEPLOYMENT_NAME:-gpt-4o}"
    COSMOS_CONSISTENCY="${COSMOS_CONSISTENCY:-Session}"
    TAGS="${TAGS:-'{\"project\":\"discovery-bot-v2\",\"managedBy\":\"deploy-scripts\"}'}"

    export RESOURCE_GROUP BASE_NAME CONVERSATION_MODE AUTH_MODE MODEL_DEPLOYMENT_NAME COSMOS_CONSISTENCY TAGS
}

# ── Prerequisite checks ──────────────────────────────────────────────────────

check_prereqs() {
    log_step "Checking prerequisites"
    local ok=true

    if ! command -v az &>/dev/null; then
        log_error "Azure CLI not found. Install: https://aka.ms/install-azure-cli"
        ok=false
    else
        log_ok "Azure CLI $(az version --query '\"azure-cli\"' -o tsv 2>/dev/null || echo 'installed')"
    fi

    if ! command -v docker &>/dev/null; then
        log_warn "Docker not found — you won't be able to build locally. ACR build will still work."
    else
        log_ok "Docker $(docker --version 2>/dev/null | grep -oP '\d+\.\d+\.\d+' || echo 'installed')"
    fi

    if ! command -v dotnet &>/dev/null; then
        log_warn ".NET SDK not found — needed for local dev only."
    else
        log_ok ".NET SDK $(dotnet --version 2>/dev/null)"
    fi

    # Check Azure login
    if ! az account show &>/dev/null 2>&1; then
        log_error "Not logged into Azure. Run: az login"
        ok=false
    else
        local current_sub
        current_sub=$(az account show --query id -o tsv)
        if [[ "$current_sub" != "${SUBSCRIPTION_ID}" ]]; then
            log_warn "Current subscription ($current_sub) differs from SUBSCRIPTION_ID (${SUBSCRIPTION_ID})"
            log_info "Switching subscription..."
            az account set --subscription "${SUBSCRIPTION_ID}"
        fi
        log_ok "Azure subscription: ${SUBSCRIPTION_ID}"
    fi

    # Check Bicep
    if ! az bicep version &>/dev/null 2>&1; then
        log_info "Installing Bicep CLI..."
        az bicep install
    fi
    log_ok "Bicep CLI $(az bicep version 2>&1 | grep -oP '\d+\.\d+\.\d+' || echo 'installed')"

    if [[ "$ok" == false ]]; then
        log_error "Prerequisites check failed. Fix the issues above and retry."
        exit 1
    fi

    log_ok "All prerequisites satisfied"
}

# ── Get Bicep deployment outputs ─────────────────────────────────────────────

get_output() {
    local output_name="$1"
    az deployment group show \
        --resource-group "${RESOURCE_GROUP}" \
        --name "deploy-disco-bot" \
        --query "properties.outputs.${output_name}.value" \
        -o tsv 2>/dev/null
}

# ── Confirm destructive action ───────────────────────────────────────────────

confirm_action() {
    local prompt="${1:-Are you sure?}"
    echo -e "${YELLOW}${prompt}${NC}"
    read -r -p "Type 'yes' to confirm: " response
    if [[ "$response" != "yes" ]]; then
        log_info "Cancelled."
        exit 0
    fi
}
