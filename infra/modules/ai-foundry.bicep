@description('Name for the AI Services account (Foundry Hub)')
param accountName string

@description('Name for the Foundry project')
param projectName string

param location string
param tags object = {}

@description('Model deployment name')
param modelDeploymentName string = 'gpt-4o'

@description('Model name to deploy')
param modelName string = 'gpt-4o'

@description('Model version')
param modelVersion string = '2024-11-20'

@description('Model SKU')
param modelSku string = 'Standard'

@description('Model capacity (tokens per minute in thousands)')
param modelCapacity int = 10

// ── AI Services Account (Foundry Hub) ──────────────────────────
resource account 'Microsoft.CognitiveServices/accounts@2024-10-01' = {
  name: accountName
  location: location
  tags: tags
  kind: 'AIServices'
  sku: { name: 'S0' }
  identity: { type: 'SystemAssigned' }
  properties: {
    publicNetworkAccess: 'Enabled'
    allowProjectManagement: true
    disableLocalAuth: false
    customSubDomainName: accountName
  }
}

// ── Foundry Project ────────────────────────────────────────────
resource project 'Microsoft.CognitiveServices/accounts/projects@2024-10-01' = {
  parent: account
  name: projectName
  location: location
  tags: tags
  kind: 'AIServices'
  sku: { name: 'S0' }
  properties: {}
}

// ── Model Deployment ───────────────────────────────────────────
resource modelDeploy 'Microsoft.CognitiveServices/accounts/deployments@2024-10-01' = {
  parent: account
  name: modelDeploymentName
  sku: {
    name: modelSku
    capacity: modelCapacity
  }
  properties: {
    model: {
      format: 'OpenAI'
      name: modelName
      version: modelVersion
    }
  }
}

// ── Outputs ────────────────────────────────────────────────────
output accountId string = account.id
output accountName string = account.name
output projectEndpoint string = 'https://${account.name}.services.ai.azure.com/api/projects/${project.name}'
output accountPrincipalId string = account.identity.principalId
