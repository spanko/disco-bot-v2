#!/bin/bash
# Post-provision: Container Apps configuration is handled inline in Bicep.
# This script only logs the deployment outputs for verification.

set -euo pipefail

echo "=== Post-Provision Verification ==="

CONTAINER_APP_NAME=$(azd env get-values | grep CONTAINER_APP_NAME | cut -d= -f2 | tr -d '"')
CONTAINER_APP_FQDN=$(azd env get-values | grep CONTAINER_APP_FQDN | cut -d= -f2 | tr -d '"')
ACR_NAME=$(azd env get-values | grep ACR_NAME | cut -d= -f2 | tr -d '"')

echo "Container App:  $CONTAINER_APP_NAME"
echo "App URL:        https://$CONTAINER_APP_FQDN"
echo "ACR:            $ACR_NAME"
echo ""
echo "To deploy manually:"
echo "  az acr build --registry $ACR_NAME --image discovery-bot:latest ."
echo "  az containerapp update --name $CONTAINER_APP_NAME --resource-group \$AZURE_RESOURCE_GROUP --image ${ACR_NAME}.azurecr.io/discovery-bot:latest"
echo ""
echo "=== Post-Provision Complete ==="
