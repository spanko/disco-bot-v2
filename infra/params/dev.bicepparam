using '../main.bicep'

param prefix = 'disco'
param suffix = 'dev'
param primaryModelName = 'gpt-4.1'
param deployerObjectId = 'd9a4dcc0-50de-4c43-b46b-4d81233e3b1b'
param cosmosConsistency = 'Session'
param tags = {
  project: 'discovery-bot-v2'
  client: 'internal-dev'
  environment: 'dev'
  managedBy: 'azd'
}
