param cosmosAccountId string
param searchServiceId string
param storageAccountId string
param functionAppPrincipalId string
param deployerObjectId string

var cosmosDbOperator = '230815da-be43-4aae-9cb4-875f7bd000aa'
var storageBlobContrib = 'ba92f5b4-2d11-453d-a403-e96b0029c9fe'
var storageBlobDataOwner = 'b7e6dc6d-f1e8-4753-8033-0f276bb0955b'
var storageQueueDataContrib = '974c5e8b-45b9-4653-ba55-5f855dd0fb88'
var searchIndexContrib = '8ebe5a00-799e-43f5-93ac-243d3dce84a7'

resource funcCosmos 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(cosmosAccountId, functionAppPrincipalId, cosmosDbOperator)
  scope: resourceGroup()
  properties: { roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', cosmosDbOperator), principalId: functionAppPrincipalId, principalType: 'ServicePrincipal' }
}

resource funcStorage 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(storageAccountId, functionAppPrincipalId, storageBlobContrib)
  scope: resourceGroup()
  properties: { roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', storageBlobContrib), principalId: functionAppPrincipalId, principalType: 'ServicePrincipal' }
}

// Required for MI-based AzureWebJobsStorage__accountName (Functions host blob leases)
resource funcStorageBlobOwner 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(storageAccountId, functionAppPrincipalId, storageBlobDataOwner)
  scope: resourceGroup()
  properties: { roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', storageBlobDataOwner), principalId: functionAppPrincipalId, principalType: 'ServicePrincipal' }
}

// Required for MI-based AzureWebJobsStorage__accountName (Functions host queue triggers)
resource funcStorageQueue 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(storageAccountId, functionAppPrincipalId, storageQueueDataContrib)
  scope: resourceGroup()
  properties: { roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', storageQueueDataContrib), principalId: functionAppPrincipalId, principalType: 'ServicePrincipal' }
}

resource funcSearch 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(searchServiceId, functionAppPrincipalId, searchIndexContrib)
  scope: resourceGroup()
  properties: { roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', searchIndexContrib), principalId: functionAppPrincipalId, principalType: 'ServicePrincipal' }
}

