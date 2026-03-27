using '../main.bicep'

// ============================================================================
// CLIENT DEPLOYMENT TEMPLATE
// Copy this file and rename: infra/params/<client-name>.bicepparam
// Then fill in the values below and run:
//   az deployment group create -g <rg> -f infra/main.bicep -p infra/params/<client>.bicepparam
// ============================================================================

param prefix = ''           // Client name prefix, e.g. 'acme'
param suffix = 'dev'        // Environment: dev, staging, prod

// Model configuration
param primaryModelName = 'gpt-4.1'

// Security
param deployerObjectId = '' // Your Azure AD Object ID (az ad signed-in-user show --query id -o tsv)

// Data
param cosmosConsistency = 'Session'

param tags = {
  project: 'discovery-bot-v2'
  client: ''                // Client name
  environment: 'dev'
  managedBy: 'azd'
}
