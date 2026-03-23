param cosmosAccountId string
param searchServiceId string
param storageAccountId string
param keyVaultId string
param functionAppPrincipalId string
param deployerObjectId string

var cosmosDbOperator = '230815da-be43-4aae-9cb4-875f7bd000aa'
var storageBlobContrib = 'ba92f5b4-2d11-453d-a403-e96b0029c9fe'
var searchIndexContrib = '8ebe5a00-799e-43f5-93ac-243d3dce84a7'
var keyVaultSecretsUser = '4633458b-17de-408a-b874-0445c86b69e6'

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

resource funcSearch 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(searchServiceId, functionAppPrincipalId, searchIndexContrib)
  scope: resourceGroup()
  properties: { roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', searchIndexContrib), principalId: functionAppPrincipalId, principalType: 'ServicePrincipal' }
}

resource funcKv 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(keyVaultId, functionAppPrincipalId, keyVaultSecretsUser)
  scope: resourceGroup()
  properties: { roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', keyVaultSecretsUser), principalId: functionAppPrincipalId, principalType: 'ServicePrincipal' }
}
