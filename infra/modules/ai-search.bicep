param searchServiceName string
param location string
param tags object = {}
param sku string = 'basic'

resource searchService 'Microsoft.Search/searchServices@2024-03-01-preview' = {
  name: searchServiceName
  location: location
  tags: tags
  sku: { name: sku }
  properties: { replicaCount: 1, partitionCount: 1, hostingMode: 'default' }
}

output searchServiceId string = searchService.id
output searchEndpoint string = 'https://${searchService.name}.search.windows.net'
output searchServiceName string = searchService.name
