using '../main.bicep'

// Dev environment — fill in deployerObjectId before deploying
// Get your ID: az ad signed-in-user show --query id -o tsv
param prefix = 'disco'
param suffix = 'dev'
param primaryModelName = 'gpt-4o'
param deployerObjectId = '' // REQUIRED: your Azure AD Object ID
param cosmosConsistency = 'Session'
param tags = {
  project: 'discovery-bot-v2'
  client: 'internal-dev'
  environment: 'dev'
  managedBy: 'azd'
}
