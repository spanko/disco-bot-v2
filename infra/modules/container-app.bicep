param containerAppName string
param containerAppEnvName string
param containerImage string
param acrLoginServer string = ''
param location string
param tags object = {}

// App settings as params (injected from main.bicep outputs)
param projectEndpoint string
param modelDeploymentName string
param agentName string
param cosmosEndpoint string
param cosmosDatabase string
param storageEndpoint string
param aiSearchEndpoint string
param knowledgeIndexName string
param appInsightsConnectionString string

@description('Conversation mode: lightweight (no Cosmos/Search/Blob), standard, or full')
@allowed(['lightweight', 'standard', 'full'])
param conversationMode string = 'standard'

@description('Auth mode: none, magic_link, invite_code, or entra_external')
@allowed(['none', 'magic_link', 'invite_code', 'entra_external'])
param authMode string = 'none'

@secure()
@description('JWT signing key for magic_link auth mode')
param jwtSigningKey string = ''

resource env 'Microsoft.App/managedEnvironments@2024-03-01' = {
  name: containerAppEnvName
  location: location
  tags: tags
  properties: {
    zoneRedundant: false
  }
}

resource app 'Microsoft.App/containerApps@2024-03-01' = {
  name: containerAppName
  location: location
  tags: tags
  identity: { type: 'SystemAssigned' }
  properties: {
    managedEnvironmentId: env.id
    configuration: {
      activeRevisionsMode: 'Single'
      registries: empty(acrLoginServer) ? [] : [
        {
          server: acrLoginServer
          identity: 'system'
        }
      ]
      ingress: {
        external: true
        targetPort: 8080
        transport: 'http'
        allowInsecure: false
      }
    }
    template: {
      containers: [
        {
          name: 'discovery-bot'
          image: containerImage
          resources: { cpu: json('0.5'), memory: '1Gi' }
          env: union([
            { name: 'PROJECT_ENDPOINT', value: projectEndpoint }
            { name: 'MODEL_DEPLOYMENT_NAME', value: modelDeploymentName }
            { name: 'AGENT_NAME', value: agentName }
            { name: 'COSMOS_ENDPOINT', value: cosmosEndpoint }
            { name: 'COSMOS_DATABASE', value: cosmosDatabase }
            { name: 'STORAGE_ENDPOINT', value: storageEndpoint }
            { name: 'AI_SEARCH_ENDPOINT', value: aiSearchEndpoint }
            { name: 'KNOWLEDGE_INDEX_NAME', value: knowledgeIndexName }
            { name: 'APPLICATIONINSIGHTS_CONNECTION_STRING', value: appInsightsConnectionString }
            { name: 'CONVERSATION_MODE', value: conversationMode }
            { name: 'AUTH_MODE', value: authMode }
            { name: 'ASPNETCORE_ENVIRONMENT', value: 'Production' }
          ], authMode == 'magic_link' ? [
            { name: 'JWT_SIGNING_KEY', value: jwtSigningKey }
          ] : [])
          probes: [
            {
              type: 'Liveness'
              httpGet: { path: '/health', port: 8080 }
              initialDelaySeconds: 10
              periodSeconds: 30
            }
            {
              type: 'Readiness'
              httpGet: { path: '/health/ready', port: 8080 }
              initialDelaySeconds: 15
              periodSeconds: 30
            }
          ]
        }
      ]
      scale: {
        minReplicas: 0
        maxReplicas: 5
        rules: [
          {
            name: 'http-scaling'
            http: { metadata: { concurrentRequests: '10' } }
          }
        ]
      }
    }
  }
}

output containerAppName string = app.name
output containerAppFqdn string = app.properties.configuration.ingress.fqdn
output containerAppPrincipalId string = app.identity.principalId
