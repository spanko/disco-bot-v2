#!/usr/bin/env bash
# =============================================================================
# deploy/teardown.sh — Destroy all Azure resources for an environment
#
# Usage: ./deploy/teardown.sh <env-name>
#
# WARNING: This deletes the entire resource group. Irreversible.
# =============================================================================

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
source "${SCRIPT_DIR}/_helpers.sh"

ENV_NAME="${1:?Usage: $0 <env-name>}"
load_env "$ENV_NAME"

log_step "Teardown: ${ENV_NAME}"

echo ""
echo "  This will permanently delete:"
echo "    Resource Group:  ${RESOURCE_GROUP}"
echo "    Location:        ${LOCATION}"
echo "    All resources inside (Cosmos, ACR, ACA, AI Foundry, etc.)"
echo ""

confirm_action "This action is IRREVERSIBLE. Delete resource group '${RESOURCE_GROUP}'?"

log_step "Deleting resource group: ${RESOURCE_GROUP}"

az group delete \
    --name "${RESOURCE_GROUP}" \
    --yes \
    --no-wait

log_ok "Deletion initiated (runs in background)"
log_info "Monitor: az group show --name ${RESOURCE_GROUP} --query properties.provisioningState -o tsv"

# Clean up outputs file
OUTPUTS_FILE="${SCRIPT_DIR}/params/${ENV_NAME}.outputs"
if [[ -f "$OUTPUTS_FILE" ]]; then
    rm "$OUTPUTS_FILE"
    log_ok "Removed ${OUTPUTS_FILE}"
fi

echo ""
log_ok "Teardown initiated for ${ENV_NAME}"
