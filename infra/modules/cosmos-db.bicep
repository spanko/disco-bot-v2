param accountName string
param location string
param tags object = {}
param consistencyLevel string = 'Session'
param databases array = []

resource cosmosAccount 'Microsoft.DocumentDB/databaseAccounts@2024-05-15' = {
  name: accountName
  location: location
  tags: tags
  kind: 'GlobalDocumentDB'
  properties: {
    databaseAccountOfferType: 'Standard'
    consistencyPolicy: { defaultConsistencyLevel: consistencyLevel }
    locations: [{ locationName: location, failoverPriority: 0 }]
    capabilities: [{ name: 'EnableServerless' }]
  }
}

output accountId string = cosmosAccount.id
output endpoint string = cosmosAccount.properties.documentEndpoint
output accountName string = cosmosAccount.name
