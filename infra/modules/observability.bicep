// ============================================================================
// Observability — Alert rules for Discovery Bot
// Deploys scheduled query rules against App Insights / Log Analytics
// ============================================================================

param appInsightsId string
param location string
param tags object = {}

@description('Email addresses for alert notifications (comma-separated)')
param alertEmails array = []

// ---------------------------------------------------------------------------
// Action Group (email notifications)
// ---------------------------------------------------------------------------

resource actionGroup 'Microsoft.Insights/actionGroups@2023-01-01' = {
  name: 'ag-discovery-alerts'
  location: 'global'
  tags: tags
  properties: {
    groupShortName: 'disco-alert'
    enabled: true
    emailReceivers: [for (email, i) in alertEmails: {
      name: 'email-${i}'
      emailAddress: email
      useCommonAlertSchema: true
    }]
  }
}

// ---------------------------------------------------------------------------
// Quality Alerts
// ---------------------------------------------------------------------------

resource groundednessAlert 'Microsoft.Insights/scheduledQueryRules@2023-03-15-preview' = {
  name: 'alert-groundedness-low'
  location: location
  tags: tags
  properties: {
    displayName: 'Agent Groundedness Score Low'
    description: 'Average groundedness evaluation score dropped below 3.5 in the last hour'
    severity: 2
    enabled: true
    evaluationFrequency: 'PT15M'
    windowSize: 'PT1H'
    scopes: [appInsightsId]
    criteria: {
      allOf: [
        {
          query: 'customMetrics | where name == "ai.evaluation.groundedness" | summarize avg_score=avg(value) by bin(timestamp, 1h) | where avg_score < 3.5'
          timeAggregation: 'Count'
          operator: 'GreaterThan'
          threshold: 0
        }
      ]
    }
    actions: {
      actionGroups: [actionGroup.id]
    }
  }
}

// ---------------------------------------------------------------------------
// Operational Alerts
// ---------------------------------------------------------------------------

resource successRateAlert 'Microsoft.Insights/scheduledQueryRules@2023-03-15-preview' = {
  name: 'alert-success-rate-low'
  location: location
  tags: tags
  properties: {
    displayName: 'Conversation Success Rate Low'
    description: 'More than 10% of conversation requests failed in the last 30 minutes'
    severity: 1
    enabled: true
    evaluationFrequency: 'PT5M'
    windowSize: 'PT30M'
    scopes: [appInsightsId]
    criteria: {
      allOf: [
        {
          query: 'requests | where name contains "Conversation" | summarize total=count(), failed=countif(success == false) | extend failRate = todouble(failed)/todouble(total) | where failRate > 0.1 and total > 5'
          timeAggregation: 'Count'
          operator: 'GreaterThan'
          threshold: 0
        }
      ]
    }
    actions: {
      actionGroups: [actionGroup.id]
    }
  }
}

resource latencyAlert 'Microsoft.Insights/scheduledQueryRules@2023-03-15-preview' = {
  name: 'alert-latency-high'
  location: location
  tags: tags
  properties: {
    displayName: 'Conversation Latency High'
    description: 'P95 conversation response time exceeded 30 seconds in the last 15 minutes'
    severity: 2
    enabled: true
    evaluationFrequency: 'PT5M'
    windowSize: 'PT15M'
    scopes: [appInsightsId]
    criteria: {
      allOf: [
        {
          query: 'requests | where name contains "Conversation" | summarize p95=percentile(duration, 95) | where p95 > 30000'
          timeAggregation: 'Count'
          operator: 'GreaterThan'
          threshold: 0
        }
      ]
    }
    actions: {
      actionGroups: [actionGroup.id]
    }
  }
}

resource extractionFailureAlert 'Microsoft.Insights/scheduledQueryRules@2023-03-15-preview' = {
  name: 'alert-extraction-failures'
  location: location
  tags: tags
  properties: {
    displayName: 'Knowledge Extraction Failures Spike'
    description: 'More than 5 extraction failures in the last 15 minutes'
    severity: 2
    enabled: true
    evaluationFrequency: 'PT5M'
    windowSize: 'PT15M'
    scopes: [appInsightsId]
    criteria: {
      allOf: [
        {
          query: 'customMetrics | where name == "discovery.knowledge.extraction_failures" | summarize total=sum(value) | where total > 5'
          timeAggregation: 'Count'
          operator: 'GreaterThan'
          threshold: 0
        }
      ]
    }
    actions: {
      actionGroups: [actionGroup.id]
    }
  }
}

// ---------------------------------------------------------------------------
// Safety Alerts
// ---------------------------------------------------------------------------

resource safetyAlert 'Microsoft.Insights/scheduledQueryRules@2023-03-15-preview' = {
  name: 'alert-safety-violation'
  location: location
  tags: tags
  properties: {
    displayName: 'Safety Evaluation Violation'
    description: 'A safety evaluation (hate, violence, self-harm, sexual) scored below threshold'
    severity: 1
    enabled: true
    evaluationFrequency: 'PT15M'
    windowSize: 'PT1H'
    scopes: [appInsightsId]
    criteria: {
      allOf: [
        {
          query: 'customMetrics | where name startswith "ai.evaluation.safety" | where value > 0 | summarize violations=count() | where violations > 0'
          timeAggregation: 'Count'
          operator: 'GreaterThan'
          threshold: 0
        }
      ]
    }
    actions: {
      actionGroups: [actionGroup.id]
    }
  }
}

// ---------------------------------------------------------------------------
// Outputs
// ---------------------------------------------------------------------------

output actionGroupId string = actionGroup.id
