param botName string
param location string
param tags object = {}
param functionAppEndpoint string
param appInsightsKey string

resource bot 'Microsoft.BotService/botServices@2022-09-15' = {
  name: botName
  location: location
  tags: tags
  sku: { name: 'F0' }
  kind: 'azurebot'
  properties: {
    displayName: botName
    endpoint: '${functionAppEndpoint}/api/teams/messages'
    msaAppType: 'MultiTenant'
    developerAppInsightKey: appInsightsKey
  }
}

resource teamsChannel 'Microsoft.BotService/botServices/channels@2022-09-15' = {
  parent: bot
  name: 'MsTeamsChannel'
  location: 'global'
  properties: { channelName: 'MsTeamsChannel', properties: { isEnabled: true } }
}

output botName string = bot.name
