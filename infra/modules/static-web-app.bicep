param staticWebAppName string
param location string
param tags object = {}
param functionAppName string

resource swa 'Microsoft.Web/staticSites@2023-12-01' = {
  name: staticWebAppName
  location: location
  tags: tags
  sku: { name: 'Free', tier: 'Free' }
  properties: {}
}

output staticWebAppUrl string = 'https://${swa.properties.defaultHostname}'
output staticWebAppName string = swa.name
