// Deploys "Rootline Inventory" as a shared Azure Monitor workbook.
// The workbook JSON is loaded from the file next to this one, so there is nothing to keep in sync.
//   az deployment group create -g <resource-group> -f main.bicep

@description('Name shown in Azure Monitor > Workbooks.')
param workbookDisplayName string = 'Rootline Inventory'

@description('Region for the workbook resource itself (it reads data from every region).')
param location string = resourceGroup().location

@description('Workbook resource name (a GUID). Keep the default for a new workbook; pass an existing GUID to update one.')
param workbookId string = newGuid()

resource workbook 'Microsoft.Insights/workbooks@2022-04-01' = {
  name: workbookId
  location: location
  kind: 'shared'
  properties: {
    displayName: workbookDisplayName
    category: 'workbook'
    sourceId: 'azure monitor'
    version: '1.0'
    serializedData: loadTextContent('rootline-inventory.workbook.json')
  }
}

output workbookResourceId string = workbook.id
