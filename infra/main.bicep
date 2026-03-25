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
param primaryModelName string = 'gpt-4.1'

@description('Primary model version')
param primaryModelVersion string = '2025-04-14'

@description('Primary model capacity (thousands TPM)')
param primaryModelCapacity int = 50

@description('Fallback model deployment name')
param fallbackModelName string = 'gpt-4.1-mini'

@description('Fallback model version')
param fallbackModelVersion string = '2025-04-14'

@description('Fallback model capacity')
param fallbackModelCapacity int = 30

@description('Deployer AAD Object ID for RBAC')
param deployerObjectId string

@description('Enable public network access (true for dev, false for prod)')
param enablePublicAccess bool = true

@description('Cosmos DB consistency level')
@allowed(['Session', 'BoundedStaleness', 'Strong', 'ConsistentPrefix', 'Eventual'])
param cosmosConsistency string = 'Session'

// ---------------------------------------------------------------------------
// Variables
// ---------------------------------------------------------------------------

var baseName = '${prefix}${suffix}'
var uniqueSuffix = substring(uniqueString(resourceGroup().id, baseName), 0, 6)

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
    // Custom discovery database with our containers
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
    // Note: The enterprise_memory database (thread-message-store, etc.)
    // is created automatically by Foundry when the capability host is set.
    // Minimum 3000 RU/s total for Foundry containers.
  }
}

// AI Search — knowledge semantic index + Foundry vector stores
module aiSearch 'modules/ai-search.bicep' = {
  name: 'deploy-ai-search'
  params: {
    searchServiceName: '${baseName}-search-${uniqueSuffix}'
    location: location
    tags: tags
    sku: 'basic'
  }
}

// Blob Storage — BYO files + chunks for Foundry, plus our custom containers
module storage 'modules/storage.bicep' = {
  name: 'deploy-storage'
  params: {
    storageAccountName: replace('${baseName}stor${uniqueSuffix}', '-', '')
    location: location
    tags: tags
    containers: ['uploads', 'questionnaires', 'exports']
    // Note: Foundry automatically provisions 'files' and 'chunks' containers
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
// Modules — Compute & Delivery
// ---------------------------------------------------------------------------

// Azure Functions (Flex Consumption) — API layer
module functionApp 'modules/function-app.bicep' = {
  name: 'deploy-function-app'
  params: {
    functionAppName: '${baseName}-func-${uniqueSuffix}'
    appServicePlanName: '${baseName}-plan-${uniqueSuffix}'
    storageAccountName: replace('${baseName}func${uniqueSuffix}', '-', '')
    appInsightsConnectionString: appInsights.outputs.connectionString
    location: location
    tags: tags
  }
}

// ---------------------------------------------------------------------------
// Modules — Security
// ---------------------------------------------------------------------------

module rbac 'modules/role-assignments.bicep' = {
  name: 'deploy-rbac'
  params: {
    cosmosAccountId: cosmos.outputs.accountId
    searchServiceId: aiSearch.outputs.searchServiceId
    storageAccountId: storage.outputs.storageAccountId
    functionAppPrincipalId: functionApp.outputs.functionAppPrincipalId
    deployerObjectId: deployerObjectId
  }
}

// ---------------------------------------------------------------------------
// Outputs — used by azd and post-provision scripts
// ---------------------------------------------------------------------------

output PROJECT_ENDPOINT string = 'https://${baseName}-foundry-${uniqueSuffix}.services.ai.azure.com/api/projects/${baseName}-project'
output MODEL_DEPLOYMENT_NAME string = primaryModelName
output COSMOS_ENDPOINT string = cosmos.outputs.endpoint
output COSMOS_DATABASE string = 'discovery'
output STORAGE_ENDPOINT string = storage.outputs.blobEndpoint
output AI_SEARCH_ENDPOINT string = aiSearch.outputs.searchEndpoint
output APP_INSIGHTS_CONNECTION string = appInsights.outputs.connectionString
output FUNCTION_APP_NAME string = functionApp.outputs.functionAppName
output FUNCTION_APP_HOSTNAME string = functionApp.outputs.functionAppHostName
