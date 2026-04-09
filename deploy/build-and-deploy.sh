#!/usr/bin/env bash
# =============================================================================
# deploy/build-and-deploy.sh — Build container image and deploy to ACA
#
# Usage: ./deploy/build-and-deploy.sh <env-name> [--tag <tag>]
# Example: ./deploy/build-and-deploy.sh myenv
#          ./deploy/build-and-deploy.sh myenv --tag v1.2.3
#
# Builds the Docker image using ACR Tasks (no local Docker required),
# pushes to ACR, and updates the Container App to the new image.
# =============================================================================

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/.." && pwd)"

source "${SCRIPT_DIR}/_helpers.sh"

ENV_NAME="${1:?Usage: $0 <env-name> [--tag <tag>]}"
shift
load_env "$ENV_NAME"

# Parse optional --tag
IMAGE_TAG="${IMAGE_TAG:-$(git -C "${REPO_ROOT}" rev-parse --short HEAD 2>/dev/null || date +%Y%m%d%H%M%S)}"
while [[ $# -gt 0 ]]; do
    case $1 in
        --tag) IMAGE_TAG="$2"; shift 2 ;;
        *) log_error "Unknown option: $1"; exit 1 ;;
    esac
done

# Load provisioning outputs
OUTPUTS_FILE="${SCRIPT_DIR}/params/${ENV_NAME}.outputs"
if [[ ! -f "$OUTPUTS_FILE" ]]; then
    log_error "Outputs file not found: ${OUTPUTS_FILE}"
    log_info  "Run provision.sh first: ./deploy/provision.sh ${ENV_NAME}"
    exit 1
fi

# shellcheck disable=SC1090
source "$OUTPUTS_FILE"

IMAGE_NAME="discovery-bot"

log_step "Building and deploying Discovery Bot"
log_info "ACR:       ${ACR_NAME}"
log_info "Image:     ${IMAGE_NAME}:${IMAGE_TAG}"
log_info "Target:    ${CONTAINER_APP_NAME}"

# ── Build with ACR Tasks ─────────────────────────────────────────────────────

log_step "Building image via ACR Tasks (remote build)"

az acr build \
    --registry "${ACR_NAME}" \
    --image "${IMAGE_NAME}:${IMAGE_TAG}" \
    --image "${IMAGE_NAME}:latest" \
    --file "${REPO_ROOT}/Dockerfile" \
    "${REPO_ROOT}" \
    2>&1 | tail -20

log_ok "Image pushed: ${ACR_LOGIN_SERVER}/${IMAGE_NAME}:${IMAGE_TAG}"

# ── Update Container App ─────────────────────────────────────────────────────

log_step "Updating Container App to new image"

az containerapp update \
    --name "${CONTAINER_APP_NAME}" \
    --resource-group "${RESOURCE_GROUP}" \
    --image "${ACR_LOGIN_SERVER}/${IMAGE_NAME}:${IMAGE_TAG}" \
    --output none

log_ok "Container App updated"

# ── Wait for readiness ───────────────────────────────────────────────────────

log_step "Waiting for application to become ready"

APP_URL="https://${CONTAINER_APP_FQDN}"
MAX_WAIT=120
ELAPSED=0

while [[ $ELAPSED -lt $MAX_WAIT ]]; do
    HTTP_CODE=$(curl -s -o /dev/null -w "%{http_code}" "${APP_URL}/health" 2>/dev/null || echo "000")
    if [[ "$HTTP_CODE" == "200" ]]; then
        log_ok "Application is healthy (${APP_URL}/health → 200)"
        break
    fi
    log_info "Waiting... (${ELAPSED}s, last status: ${HTTP_CODE})"
    sleep 10
    ELAPSED=$((ELAPSED + 10))
done

if [[ $ELAPSED -ge $MAX_WAIT ]]; then
    log_warn "Health check did not pass within ${MAX_WAIT}s — the app may still be starting."
    log_info "Check manually: curl ${APP_URL}/health"
fi

# ── Summary ──────────────────────────────────────────────────────────────────

log_step "Deployment Complete"

echo ""
echo "  Image:    ${ACR_LOGIN_SERVER}/${IMAGE_NAME}:${IMAGE_TAG}"
echo "  App URL:  ${APP_URL}"
echo ""
echo "  Verify:   ./deploy/verify.sh ${ENV_NAME}"
echo ""
