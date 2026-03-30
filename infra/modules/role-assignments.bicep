param cosmosAccountId string
param cosmosAccountName string
param searchServiceId string
param storageAccountId string
param appPrincipalId string
param deployerObjectId string

var cosmosDbOperator = '230815da-be43-4aae-9cb4-875f7bd000aa'
var cosmosDataContributor = '00000000-0000-0000-0000-000000000002'
var storageBlobContrib = 'ba92f5b4-2d11-453d-a403-e96b0029c9fe'
var searchIndexContrib = '8ebe5a00-799e-43f5-93ac-243d3dce84a7'

// Management-plane: create/delete databases and containers
resource appCosmos 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(cosmosAccountId, appPrincipalId, cosmosDbOperator)
  scope: resourceGroup()
  properties: { roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', cosmosDbOperator), principalId: appPrincipalId, principalType: 'ServicePrincipal' }
}

// Data-plane: read/write documents (Cosmos-native RBAC, not ARM)
resource cosmosAccount 'Microsoft.DocumentDB/databaseAccounts@2024-05-15' existing = {
  name: cosmosAccountName
}

resource appCosmosData 'Microsoft.DocumentDB/databaseAccounts/sqlRoleAssignments@2024-05-15' = {
  parent: cosmosAccount
  name: guid(cosmosAccountId, appPrincipalId, cosmosDataContributor)
  properties: {
    roleDefinitionId: '${cosmosAccount.id}/sqlRoleDefinitions/${cosmosDataContributor}'
    principalId: appPrincipalId
    scope: cosmosAccount.id
  }
}

resource appStorage 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(storageAccountId, appPrincipalId, storageBlobContrib)
  scope: resourceGroup()
  properties: { roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', storageBlobContrib), principalId: appPrincipalId, principalType: 'ServicePrincipal' }
}

resource appSearch 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(searchServiceId, appPrincipalId, searchIndexContrib)
  scope: resourceGroup()
  properties: { roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', searchIndexContrib), principalId: appPrincipalId, principalType: 'ServicePrincipal' }
}
