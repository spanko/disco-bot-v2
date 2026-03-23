using 'main.bicep'

// ============================================================================
// CLIENT DEPLOYMENT TEMPLATE
// Copy this file and rename: infra/params/<client-name>.bicepparam
// Then fill in the values below and run: azd up --environment <client-name>
// ============================================================================

param prefix = ''           // Client name prefix, e.g. 'acme'
param suffix = 'dev'        // Environment: dev, staging, prod

// Model configuration
param primaryModelName = 'gpt-4.1'
param primaryModelVersion = '2025-04-14'
param primaryModelCapacity = 50
param fallbackModelName = 'gpt-4.1-mini'
param fallbackModelVersion = '2025-04-14'
param fallbackModelCapacity = 30

// Security
param deployerObjectId = '' // Your Azure AD Object ID (az ad signed-in-user show --query id -o tsv)
param enablePublicAccess = true // Set false for production

// Data
param cosmosConsistency = 'Session'

param tags = {
  project: 'discovery-bot-v2'
  client: ''                // Client name
  environment: 'dev'
  managedBy: 'azd'
}
