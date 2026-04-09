#!/usr/bin/env bash
# =============================================================================
# deploy/verify.sh — Verify a Discovery Bot deployment
#
# Usage: ./deploy/verify.sh <env-name>
# =============================================================================

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
source "${SCRIPT_DIR}/_helpers.sh"

ENV_NAME="${1:?Usage: $0 <env-name>}"
load_env "$ENV_NAME"

OUTPUTS_FILE="${SCRIPT_DIR}/params/${ENV_NAME}.outputs"
if [[ ! -f "$OUTPUTS_FILE" ]]; then
    log_error "Outputs file not found. Run provision.sh first."
    exit 1
fi

# shellcheck disable=SC1090
source "$OUTPUTS_FILE"

APP_URL="https://${CONTAINER_APP_FQDN}"
PASS=0
FAIL=0
WARN=0

check() {
    local name="$1" url="$2" expected_status="${3:-200}"
    local http_code body

    http_code=$(curl -s -o /tmp/verify_body -w "%{http_code}" "$url" 2>/dev/null || echo "000")
    body=$(cat /tmp/verify_body 2>/dev/null)

    if [[ "$http_code" == "$expected_status" ]]; then
        log_ok "${name} → ${http_code}"
        PASS=$((PASS + 1))
    elif [[ "$http_code" == "000" ]]; then
        log_error "${name} → Connection failed"
        FAIL=$((FAIL + 1))
    else
        log_warn "${name} → ${http_code} (expected ${expected_status})"
        WARN=$((WARN + 1))
    fi
}

log_step "Verifying deployment: ${ENV_NAME}"
log_info "App URL: ${APP_URL}"

echo ""

# ── Health Checks ────────────────────────────────────────────────────────────

check "Liveness  /health"       "${APP_URL}/health"
check "Readiness /health/ready" "${APP_URL}/health/ready"

# ── Auth Endpoint ────────────────────────────────────────────────────────────

check "Auth mode /api/auth/mode" "${APP_URL}/api/auth/mode"

# ── Static Files ─────────────────────────────────────────────────────────────

check "Web UI    /" "${APP_URL}/"

# ── Conversation API (should reject empty body) ─────────────────────────────

conv_code=$(curl -s -o /dev/null -w "%{http_code}" \
    -X POST "${APP_URL}/api/conversation" \
    -H "Content-Type: application/json" \
    -d '{}' 2>/dev/null || echo "000")

if [[ "$conv_code" == "400" ]]; then
    log_ok "Conversation API → 400 (correct rejection of empty body)"
    PASS=$((PASS + 1))
elif [[ "$conv_code" == "401" || "$conv_code" == "403" ]]; then
    log_ok "Conversation API → ${conv_code} (auth is enforcing)"
    PASS=$((PASS + 1))
else
    log_warn "Conversation API → ${conv_code} (expected 400 or 401)"
    WARN=$((WARN + 1))
fi

# ── Azure Resources ──────────────────────────────────────────────────────────

log_info ""
log_info "Azure resource status:"

# Container App
aca_state=$(az containerapp show \
    --name "${CONTAINER_APP_NAME}" \
    --resource-group "${RESOURCE_GROUP}" \
    --query "properties.runningStatus" -o tsv 2>/dev/null || echo "unknown")
log_info "  Container App: ${aca_state}"

# ACR
acr_status=$(az acr show \
    --name "${ACR_NAME}" \
    --query "status.displayStatus" -o tsv 2>/dev/null || echo "unknown")
log_info "  ACR: ${acr_status}"

# Latest image
latest_tag=$(az acr repository show-tags \
    --name "${ACR_NAME}" \
    --repository "discovery-bot" \
    --orderby time_desc --top 1 -o tsv 2>/dev/null || echo "none")
log_info "  Latest image tag: ${latest_tag}"

# ── Summary ──────────────────────────────────────────────────────────────────

echo ""
log_step "Verification Summary"
echo ""
echo -e "  ${GREEN}Passed:${NC}   ${PASS}"
echo -e "  ${YELLOW}Warnings:${NC} ${WARN}"
echo -e "  ${RED}Failed:${NC}   ${FAIL}"
echo ""

if [[ $FAIL -gt 0 ]]; then
    log_error "Some checks failed. Review the output above."
    exit 1
elif [[ $WARN -gt 0 ]]; then
    log_warn "Some checks returned unexpected results."
    exit 0
else
    log_ok "All checks passed."
    exit 0
fi
