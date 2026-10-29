targetScope = 'resourceGroup'

@description('Azure region used by the recommender resources.')
param location string = 'westus3'

@description('Existing Azure Container Registry name.')
param registryName string = 'mdxrec340f351d'

@description('Existing Log Analytics workspace name.')
param logAnalyticsWorkspaceName string = 'law-mdx-recommender'

@description('Existing Container Apps environment name.')
param managedEnvironmentName string = 'cae-mdx-recommender'

@description('Existing user-assigned identity name.')
param identityName string = 'id-mdx-recommender'

@description('Existing Container Apps Job name.')
param jobName string = 'job-marketdixon-recommender'

@description('Container image tag already published to the registry.')
param imageTag string = '20261027'

@description('SQL connection string stored as a Container Apps secret.')
@minLength(1)
@secure()
param marketDixonDbConnectionString string

var acrPullRoleDefinitionId = subscriptionResourceId(
  'Microsoft.Authorization/roleDefinitions',
  '7f951dda-4ed3-4680-a7ca-43fe172d538d'
)

resource registry 'Microsoft.ContainerRegistry/registries@2023-07-01' = {
  name: registryName
  location: location
  sku: {
    name: 'Basic'
  }
  properties: {
    adminUserEnabled: false
    publicNetworkAccess: 'Enabled'
  }
}

resource logAnalytics 'Microsoft.OperationalInsights/workspaces@2022-10-01' = {
  name: logAnalyticsWorkspaceName
  location: location
  properties: {
    sku: {
      name: 'PerGB2018'
    }
  }
}

resource recommenderIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: identityName
  location: location
}

resource acrPullAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(registry.id, recommenderIdentity.id, acrPullRoleDefinitionId)
  scope: registry
  properties: {
    principalId: recommenderIdentity.properties.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: acrPullRoleDefinitionId
  }
}

resource managedEnvironment 'Microsoft.App/managedEnvironments@2024-03-01' = {
  name: managedEnvironmentName
  location: location
  properties: {
    appLogsConfiguration: {
      destination: 'log-analytics'
      logAnalyticsConfiguration: {
        customerId: logAnalytics.properties.customerId
        sharedKey: logAnalytics.listKeys().primarySharedKey
      }
    }
  }
}

resource recommenderJob 'Microsoft.App/jobs@2024-03-01' = {
  name: jobName
  location: location
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${recommenderIdentity.id}': {}
    }
  }
  properties: {
    environmentId: managedEnvironment.id
    configuration: {
      triggerType: 'Manual'
      replicaTimeout: 1800
      replicaRetryLimit: 0
      manualTriggerConfig: {
        parallelism: 1
        replicaCompletionCount: 1
      }
      registries: [
        {
          server: registry.properties.loginServer
          identity: recommenderIdentity.id
        }
      ]
      secrets: [
        {
          name: 'marketdixon-db-connection'
          value: marketDixonDbConnectionString
        }
      ]
    }
    template: {
      containers: [
        {
          name: 'job-marketdixon-recommender'
          image: '${registry.properties.loginServer}/recommender-job:${imageTag}'
          env: [
            {
              name: 'ConnectionStrings__MarketDixonDb'
              secretRef: 'marketdixon-db-connection'
            }
          ]
          resources: {
            cpu: json('0.25')
            memory: '0.5Gi'
          }
        }
      ]
    }
  }
  dependsOn: [
    acrPullAssignment
  ]
}
