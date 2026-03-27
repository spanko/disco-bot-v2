// ============================================================================
// Discovery Chatbot v2 — Main Infrastructure Orchestrator
// Stampable: one parameter file per client → full isolated deployment
// ============================================================================

targetScope = 'resourceGroup'

// ---------------------------------------------------------------------------
// Parameters
// ---------------------------------------------------------------------------

@description('Resource naming prefix (e.g., client name)')
param prefix string

@description('Environment suffix (dev, staging, prod)')
param suffix string

@description('Azure region')
param location string = resourceGroup().location

@description('Tags for all resources')
param tags object = {}

@description('Primary model deployment name')
param primaryModelName string = 'gpt-4o'

@description('Deployer AAD Object ID for RBAC')
param deployerObjectId string

@description('Cosmos DB consistency level')
@allowed(['Session', 'BoundedStaleness', 'Strong', 'ConsistentPrefix', 'Eventual'])
param cosmosConsistency string = 'Session'

@description('Container image tag (default: latest). Set to empty string for initial deploy with placeholder image.')
param imageTag string = ''

// ---------------------------------------------------------------------------
// Variables
// ---------------------------------------------------------------------------

var baseName = '${prefix}${suffix}'
var uniqueSuffix = substring(uniqueString(resourceGroup().id, baseName), 0, 6)
var acrName = replace('${baseName}acr${uniqueSuffix}', '-', '')

// ---------------------------------------------------------------------------
// Modules — BYO Resources for Standard Agent Setup
// ---------------------------------------------------------------------------

// Cosmos DB — houses both Foundry-managed containers (enterprise_memory)
// and our custom discovery containers
module cosmos 'modules/cosmos-db.bicep' = {
  name: 'deploy-cosmos'
  params: {
    accountName: '${baseName}-cosmos-${uniqueSuffix}'
    location: location
    tags: tags
    consistencyLevel: cosmosConsistency
    databases: [
      {
        name: 'discovery'
        containers: [
          { name: 'knowledge-items', partitionKeyPath: '/relatedContextId' }
          { name: 'discovery-sessions', partitionKeyPath: '/contextId' }
          { name: 'questionnaires', partitionKeyPath: '/questionnaireId' }
          { name: 'user-profiles', partitionKeyPath: '/userId' }
        ]
      }
    ]
  }
}

// AI Search — knowledge semantic index + Foundry vector stores
module aiSearch 'modules/ai-search.bicep' = {
  name: 'deploy-ai-search'
  params: {
    searchServiceName: '${baseName}-search-${uniqueSuffix}'
    location: location
    tags: tags
    sku: 'free'
  }
}

// Blob Storage — BYO files + chunks for Foundry, plus our custom containers
module storage 'modules/storage.bicep' = {
  name: 'deploy-storage'
  params: {
    storageAccountName: replace('${baseName}stor${uniqueSuffix}', '-', '')
    location: location
    tags: tags
    containers: ['uploads', 'questionnaires', 'exports', 'documents']
  }
}

// App Insights — unified observability (Foundry traces + custom telemetry)
module appInsights 'modules/app-insights.bicep' = {
  name: 'deploy-appinsights'
  params: {
    appInsightsName: '${baseName}-insights-${uniqueSuffix}'
    workspaceName: '${baseName}-logs-${uniqueSuffix}'
    location: location
    tags: tags
  }
}

// ---------------------------------------------------------------------------
// Container Registry — shared across stamps in the same subscription
// ---------------------------------------------------------------------------

resource acr 'Microsoft.ContainerRegistry/registries@2023-07-01' = {
  name: acrName
  location: location
  tags: tags
  sku: { name: 'Basic' }
  properties: { adminUserEnabled: false }
}

// Grant ACA pull access to ACR
var acrPullRole = '7f951dda-4ed3-4680-a7ca-43fe172d538d'

resource acrPull 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(acr.id, '${baseName}-app-${uniqueSuffix}', acrPullRole)
  scope: acr
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', acrPullRole)
    principalId: containerApp.outputs.containerAppPrincipalId
    principalType: 'ServicePrincipal'
  }
}

// ---------------------------------------------------------------------------
// Compute — Azure Container Apps
// ---------------------------------------------------------------------------

module containerApp 'modules/container-app.bicep' = {
  name: 'deploy-container-app'
  params: {
    containerAppName: '${baseName}-app-${uniqueSuffix}'
    containerAppEnvName: '${baseName}-env-${uniqueSuffix}'
    containerImage: empty(imageTag) ? 'mcr.microsoft.com/dotnet/samples:aspnetapp' : '${acrName}.azurecr.io/discovery-bot:${imageTag}'
    acrLoginServer: empty(imageTag) ? '' : acr.properties.loginServer
    location: location
    tags: tags
    projectEndpoint: 'https://${baseName}-foundry-${uniqueSuffix}.services.ai.azure.com/api/projects/${baseName}-project'
    modelDeploymentName: primaryModelName
    agentName: 'discovery-agent'
    cosmosEndpoint: cosmos.outputs.endpoint
    cosmosDatabase: 'discovery'
    storageEndpoint: storage.outputs.blobEndpoint
    aiSearchEndpoint: aiSearch.outputs.searchEndpoint
    knowledgeIndexName: 'knowledge-items'
    appInsightsConnectionString: appInsights.outputs.connectionString
  }
}

// ---------------------------------------------------------------------------
// Security — RBAC
// ---------------------------------------------------------------------------

module rbac 'modules/role-assignments.bicep' = {
  name: 'deploy-rbac'
  params: {
    cosmosAccountId: cosmos.outputs.accountId
    searchServiceId: aiSearch.outputs.searchServiceId
    storageAccountId: storage.outputs.storageAccountId
    appPrincipalId: containerApp.outputs.containerAppPrincipalId
    deployerObjectId: deployerObjectId
  }
}

// ---------------------------------------------------------------------------
// Observability (optional)
// ---------------------------------------------------------------------------

@description('Enable observability alert rules')
param enableObservability bool = true

@description('Alert notification email addresses')
param alertEmails array = []

module observability 'modules/observability.bicep' = if (enableObservability) {
  name: 'deploy-observability'
  params: {
    appInsightsId: appInsights.outputs.appInsightsId
    location: location
    tags: tags
    alertEmails: alertEmails
  }
}

// ---------------------------------------------------------------------------
// Outputs
// ---------------------------------------------------------------------------

output PROJECT_ENDPOINT string = 'https://${baseName}-foundry-${uniqueSuffix}.services.ai.azure.com/api/projects/${baseName}-project'
output MODEL_DEPLOYMENT_NAME string = primaryModelName
output COSMOS_ENDPOINT string = cosmos.outputs.endpoint
output COSMOS_DATABASE string = 'discovery'
output STORAGE_ENDPOINT string = storage.outputs.blobEndpoint
output AI_SEARCH_ENDPOINT string = aiSearch.outputs.searchEndpoint
output APP_INSIGHTS_CONNECTION string = appInsights.outputs.connectionString
output ACR_NAME string = acr.name
output ACR_LOGIN_SERVER string = acr.properties.loginServer
output CONTAINER_APP_NAME string = containerApp.outputs.containerAppName
output CONTAINER_APP_FQDN string = containerApp.outputs.containerAppFqdn
