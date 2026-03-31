// ============================================================================
// Management Plane — Fleet management for Discovery Bot stamps
// Separate ACA with Easy Auth to us.gt.com Entra ID
// ============================================================================

targetScope = 'resourceGroup'

param location string = resourceGroup().location
param cosmosEndpoint string
param cosmosDatabase string = 'management'
param acrLoginServer string
param imageTag string = ''
param subscriptionId string = subscription().subscriptionId

@description('Entra tenant ID for GT operator auth')
param gtTenantId string

@description('Entra client ID for management plane app registration')
param gtClientId string

param tags object = {}

var baseName = 'disco-mgmt'
var uniqueSuffix = substring(uniqueString(resourceGroup().id, baseName), 0, 6)

// ── ACA Environment ─────────────────────────────────────────────
resource env 'Microsoft.App/managedEnvironments@2024-03-01' = {
  name: '${baseName}-env-${uniqueSuffix}'
  location: location
  tags: tags
  properties: {
    zoneRedundant: false
  }
}

// ── Container App ───────────────────────────────────────────────
resource app 'Microsoft.App/containerApps@2024-03-01' = {
  name: '${baseName}-app-${uniqueSuffix}'
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
          name: 'management-plane'
          image: empty(imageTag) ? 'mcr.microsoft.com/dotnet/samples:aspnetapp' : '${acrLoginServer}/management-plane:${imageTag}'
          resources: { cpu: json('0.5'), memory: '1Gi' }
          env: [
            { name: 'COSMOS_ENDPOINT', value: cosmosEndpoint }
            { name: 'COSMOS_DATABASE', value: cosmosDatabase }
            { name: 'AZURE_SUBSCRIPTION_ID', value: subscriptionId }
            { name: 'ASPNETCORE_ENVIRONMENT', value: 'Production' }
          ]
          probes: [
            {
              type: 'Liveness'
              httpGet: { path: '/health', port: 8080 }
              initialDelaySeconds: 10
              periodSeconds: 30
            }
          ]
        }
      ]
      scale: {
        minReplicas: 1
        maxReplicas: 2
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

// ── Easy Auth (GT operators only) ───────────────────────────────
resource authConfig 'Microsoft.App/containerApps/authConfigs@2024-03-01' = {
  parent: app
  name: 'current'
  properties: {
    platform: { enabled: true }
    identityProviders: {
      azureActiveDirectory: {
        registration: {
          clientId: gtClientId
          openIdIssuer: 'https://login.microsoftonline.com/${gtTenantId}/v2.0'
        }
      }
    }
    globalValidation: {
      unauthenticatedClientAction: 'RedirectToLoginPage'
    }
  }
}

// ── Outputs ─────────────────────────────────────────────────────
output managementAppFqdn string = app.properties.configuration.ingress.fqdn
output managementAppPrincipalId string = app.identity.principalId
