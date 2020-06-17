# NServiceBus.AzureFunctions.StorageQueues

Process messages in AzureFunctions using the Azure Storage Queues trigger and the NServiceBus message pipeline.

## Running tests locally

Test projects included in the solution rely on two environment variable `AzureWebJobsStorage` used by Azure Functions SDK.
In order to run the tests, the value needs to contain a real connection string.
