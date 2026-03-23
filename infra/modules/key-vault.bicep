param keyVaultName string
param location string
param tags object = {}
param deployerObjectId string

resource keyVault 'Microsoft.KeyVault/vaults@2023-07-01' = {
  name: keyVaultName
  location: location
  tags: tags
  properties: {
    tenantId: subscription().tenantId
    sku: { family: 'A', name: 'standard' }
    enableRbacAuthorization: true
    enableSoftDelete: true
  }
}

output keyVaultId string = keyVault.id
output vaultUri string = keyVault.properties.vaultUri
