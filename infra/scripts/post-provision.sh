#!/bin/bash
# Post-provision: Set capability host connections and configure Function App settings
# This runs automatically after azd provision via azure.yaml hooks.

set -euo pipefail

echo "=== Post-Provision Setup ==="

# Read outputs from azd
PROJECT_ENDPOINT=$(azd env get-values | grep PROJECT_ENDPOINT | cut -d= -f2 | tr -d '"')
MODEL_DEPLOYMENT_NAME=$(azd env get-values | grep MODEL_DEPLOYMENT_NAME | cut -d= -f2 | tr -d '"')
COSMOS_ENDPOINT=$(azd env get-values | grep COSMOS_ENDPOINT | cut -d= -f2 | tr -d '"')
COSMOS_DATABASE=$(azd env get-values | grep COSMOS_DATABASE | cut -d= -f2 | tr -d '"')
STORAGE_ENDPOINT=$(azd env get-values | grep STORAGE_ENDPOINT | cut -d= -f2 | tr -d '"')
AI_SEARCH_ENDPOINT=$(azd env get-values | grep AI_SEARCH_ENDPOINT | cut -d= -f2 | tr -d '"')
APP_INSIGHTS_CONNECTION=$(azd env get-values | grep APP_INSIGHTS_CONNECTION | cut -d= -f2 | tr -d '"')
FUNCTION_APP_NAME=$(azd env get-values | grep FUNCTION_APP_NAME | cut -d= -f2 | tr -d '"')

echo "Setting Function App configuration..."
az functionapp config appsettings set \
  --name "$FUNCTION_APP_NAME" \
  --resource-group "$AZURE_RESOURCE_GROUP" \
  --settings \
    PROJECT_ENDPOINT="$PROJECT_ENDPOINT" \
    MODEL_DEPLOYMENT_NAME="$MODEL_DEPLOYMENT_NAME" \
    AGENT_NAME="discovery-agent" \
    COSMOS_ENDPOINT="$COSMOS_ENDPOINT" \
    COSMOS_DATABASE="$COSMOS_DATABASE" \
    STORAGE_ENDPOINT="$STORAGE_ENDPOINT" \
    AI_SEARCH_ENDPOINT="$AI_SEARCH_ENDPOINT" \
    APPLICATIONINSIGHTS_CONNECTION_STRING="$APP_INSIGHTS_CONNECTION" \
  --output none

echo "=== Post-Provision Complete ==="
echo "Function App: $FUNCTION_APP_NAME"
echo "Project Endpoint: $PROJECT_ENDPOINT"
