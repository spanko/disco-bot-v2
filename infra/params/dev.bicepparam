using '../main.bicep'

param prefix = 'disco'
param suffix = 'dev'
param primaryModelName = 'gpt-4.1'
param primaryModelVersion = '2025-04-14'
param primaryModelCapacity = 30
param fallbackModelName = 'gpt-4.1-mini'
param fallbackModelVersion = '2025-04-14'
param fallbackModelCapacity = 20
param deployerObjectId = '' // TODO: Fill with your Azure AD Object ID
param enablePublicAccess = true
param cosmosConsistency = 'Session'
param tags = {
  project: 'discovery-bot-v2'
  client: 'internal-dev'
  environment: 'dev'
  managedBy: 'azd'
}
