#!/bin/bash
set -euo pipefail
CLIENT_NAME=${1:?"Usage: $0 <client-name> <deployer-object-id>"}
DEPLOYER_ID=${2:?"Usage: $0 <client-name> <deployer-object-id>"}
PARAM_FILE="infra/params/${CLIENT_NAME}.bicepparam"
if [ -f "$PARAM_FILE" ]; then echo "Already exists: $PARAM_FILE"; exit 1; fi
echo "Creating parameter file for client: $CLIENT_NAME"
sed -e "s/param prefix = ''/param prefix = '${CLIENT_NAME}'/" \
    -e "s/param deployerObjectId = ''/param deployerObjectId = '${DEPLOYER_ID}'/" \
    -e "s/client: ''/client: '${CLIENT_NAME}'/" \
    infra/params/template.bicepparam > "$PARAM_FILE"
echo "Created: $PARAM_FILE"
echo "Next: review $PARAM_FILE, then run: azd up --environment $CLIENT_NAME"
