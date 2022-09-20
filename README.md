# NServiceBus.AzureFunctions.StorageQueues

Process messages in AzureFunctions using the Azure Storage Queues trigger and the NServiceBus message pipeline.

**This Particular Preview has been archived and is not maintained or supported by Particular Software.**

## Migration

Any users of the Preview package have two options available:

1. Switch your Azure Functions to use Azure Service Bus instead of Azure Storage Queues. [Azure Functions for Azure Service Bus](https://docs.particular.net/nservicebus/hosting/azure-functions-service-bus/) is fully supported by Particular Software.
2. Continue to use Azure Storage Queues transport, but use traditional [cloud services hosting](https://docs.particular.net/nservicebus/hosting/cloud-services-host/) rather than Azure Functions. The [Azure Storage Queue Transport](https://docs.particular.net/transports/azure-storage-queues/) is fully supported by Particular Software.

## Basic usage

### Endpoint configuration

```csharp
static readonly IFunctionEndpoint endpoint = new FunctionEndpoint(executionContext =>
{
    var storageQueueTriggeredEndpointConfiguration = StorageQueueTriggeredEndpointConfiguration.FromAttributes();

    return storageQueueTriggeredEndpointConfiguration;
});
```

The endpoint is automatically configured with the endpoint name, the transport connection string, and the logger passed into the function using a static factory method provided by the `ServiceBusTriggeredEndpointConfiguration.FromAttributes` method.

Alternatively, the endpoint name can be passed in manually:

```csharp
static readonly IFunctionEndpoint endpoint = new FunctionEndpoint(executionContext =>
{
    var storageQueueTriggeredEndpointConfiguration = new StorageQueueTriggeredEndpointConfiguration("ASQTriggerQueue");

    return storageQueueTriggeredEndpointConfiguration;
});
```

### Azure Function definition

```csharp
[FunctionName("ASQTriggerQueue")]
public static async Task Run(
    [QueueTrigger(queueName: "ASQTriggerQueue")]
    CloudQueueMessage message,
    ILogger logger,
    ExecutionContext executionContext)
{
    await endpoint.Process(message, executionContext, logger);
}
```

### Dispatching outside a message handler

Messages can be dispatched outside a message handler in functions activated by queue- and non-queue-based triggers.

```csharp
[FunctionName("HttpSender")]
public static async Task<IActionResult> Run(
    [HttpTrigger(AuthorizationLevel.Function, "get", "post", Route = null)] HttpRequest request, ExecutionContext executionContext, ILogger logger)
{
    logger.LogInformation("C# HTTP trigger function received a request.");

    var sendOptions = new SendOptions();
    sendOptions.SetDestination("DestinationEndpointName");

    await functionEndpoint.Send(new TriggerMessage(), sendOptions, executionContext, logger);

    return new OkObjectResult($"{nameof(TriggerMessage)} sent.");
}
```

Note: For statically defined endpoints, dispatching outside a message handler within a non-queue-triggered function will require a separate send-only endpoint.

```csharp
private static readonly IFunctionEndpoint functionEndpoint = new FunctionEndpoint(executionContext =>
{
    var configuration = new StorageQueueTriggeredEndpointConfiguration("HttpSender");

    configuration.AdvancedConfiguration.SendOnly();

    return configuration;
});
```

## IFunctionsHostBuilder usage

As an alternative to the configuration approach described in the previous section, an endpoint can also be configured with a static `IFunctionEndpoint` field using the `IFunctionsHostBuilder` API as described in [Use dependency injection in .NET Azure Functions](https://docs.microsoft.com/en-us/azure/azure-functions/functions-dotnet-dependency-injection).

### Endpoint configuration

NServiceBus can be registered and configured on the host builder using the `UseNServiceBus` extension method in the startup class:

```csharp
class Startup : FunctionsStartup
{
    public override void Configure(IFunctionsHostBuilder builder)
    {
        builder.UseNServiceBus(() => new StorageQueueTriggeredEndpointConfiguration("MyFunctionsEndpoint"));
    }
}
```

Any services registered via the `IFunctionsHostBuilder` will be available to message handlers via dependency injection. The startup class needs to be declared via the `FunctionStartup` attribute: `[assembly: FunctionsStartup(typeof(Startup))]`.

### Azure Function definition

To access `IFunctionEndpoint` from the Azure Function trigger, inject the `IFunctionEndpoint` via constructor-injection into the containing class:

```csharp
class MyFunction
{
    readonly IFunctionEndpoint endpoint;

    // inject the FunctionEndpoint via dependency injection:
    public MyFunction(IFunctionEndpoint endpoint)
    {
        this.endpoint = endpoint;
    }

    [FunctionName("MyFunctionsEndpoint")]
    public async Task Run(
        [QueueTrigger(queueName: "MyFunctionsEndpoint")]
        CloudQueueMessage message,
        ILogger logger,
        ExecutionContext executionContext)
    {
        await endpoint.Process(message, executionContext, logger);
    }
}
```

### Dispatching outside a message handler

Triggering a message using HTTP function:

```csharp
public class HttpSender
{
    readonly IFunctionEndpoint functionEndpoint;

    public HttpSender(IFunctionEndpoint functionEndpoint)
    {
        this.functionEndpoint = functionEndpoint;
    }

    [FunctionName("HttpSender")]
    public async Task<IActionResult> Run(
        [HttpTrigger(AuthorizationLevel.Function, "get", "post", Route = null)] HttpRequest request, ExecutionContext executionContext, ILogger logger)
    {
        logger.LogInformation("C# HTTP trigger function received a request.");

        var sendOptions = new SendOptions();
        sendOptions.RouteToThisEndpoint();

        await functionEndpoint.Send(new TriggerMessage(), sendOptions, executionContext, logger);

        return new OkObjectResult($"{nameof(TriggerMessage)} sent.");
    }
}
```

## Configuration

### License

The license is provided via the `NSERVICEBUS_LICENSE` environment variable, which is set via the Function settings in the Azure Portal.
Use a `local.settings.json` file for local development. In Azure, specify a Function setting using the environment variable as the key.

include: license-file-local-setting-file

### Custom diagnostics

[NServiceBus startup diagnostics](/nservicebus/hosting/startup-diagnostics.md) are disabled by default when using Azure Functions. Diagnostics can be written to the logs with the following snippet:

```csharp
storageQueueTriggeredEndpointConfiguration.LogDiagnostics();
```

### Persistence

The Azure Storage Queues transport requires a persistence for pub/sub and sagas to work.

```csharp
var persistence = endpointConfiguration.AdvancedConfiguration.UsePersistence<AzureStoragePersistence>();
persistence.ConnectionString("<connection-string>");
```

Endpoints that do not have sagas and do not require pub/sub can omit persistence registration using the following transport option:

```csharp
endpointConfiguration.Transport.DisablePublishing();
```

### Error queue

For recoverability to move the continuously failing messages to the error queue rather than the Functions poison queue, the error queue must be created in advance and configured using the following API:

```csharp
endpointConfiguration.AdvancedConfiguration.SendFailedMessagesTo("error");
```

## Known constraints and limitations

When using Azure Functions with Azure Storage Queues, the following points must be taken into consideration:

- Endpoints cannot create their own queues or other infrastructure using installers; the infrastructure required by the endpoint to run must be created in advance. For example:
  - Queues for commands
  - Subscription records in storage for events
- The Configuration API exposes NServiceBus transport configuration options via the `configuration.Transport` method to allow customization; however, not all of the options will be applicable to execution within Azure Functions.
- When using the default recoverability or specifying custom number of immediate retries, the number of delivery attempts specified on the underlying queue or Azure Functions host must be greater than the number of the immediate retries. The Azure Functions default is 5 (`DequeueCount`) for the Azure Storage Queues trigger.

### Message polling

Polling for new messages is handled by the Azure Storage Queues trigger. Using the default configuration, the latency for new messages to be processed by the function might take up to 1 second (see the [polling algorithm documentation](https://docs.microsoft.com/en-us/azure/azure-functions/functions-bindings-storage-queue-trigger?tabs=csharp#polling-algorithm) for further details). The maximum polling interval can be adjusted via the `maxPollingIntervall` setting in the `hosts.json` file, see the [official documentation](https://docs.microsoft.com/en-us/azure/azure-functions/functions-bindings-storage-queue#hostjson-settings) for further details.

### Features dependent upon delayed delivery

The delayed delivery feature of the Azure Storage Queues transport polls for the delayed messages information and must run continuously in the background. With the Azure Functions Consumption plan, this time is limited to the function [execution duration](https://docs.microsoft.com/en-us/azure/azure-functions/functions-scale#timeout) with some additional non-deterministic cool off time. Past that time, delayed delivery will not work as expected until another message to process or the Function is kept "warm".

For features that require timely execution of the delayed delivery related features, use one of the following options:
- Keep the Function warm in the Consumption plan
- Use an App Service Plan for Functions hosting
- Use a Premium plan

The following features are supported but are not guaranteed to execute timely on the Consumption plan:
  - [Saga timeouts](/nservicebus/sagas/timeouts.md)
  - [Delayed messages](/transports/azure-storage-queues/delayed-delivery.md) destined for the endpoints hosted with Azure Functions

The following features require an explicit opt-in:
  - [Delayed retries](/nservicebus/recoverability/#delayed-retries)

```csharp
var recoverability = endpointConfiguration.AdvancedConfiguration.Recoverability();
recoverability.Delayed(settings =>
{
    settings.NumberOfRetries(numberOfDelayedRetries);
    settings.TimeIncrease(timeIncreaseBetweenDelayedRetries);
});
```

## Preparing the Azure Storage account

Queues must be provisioned manually.

Subscriptions to events are created when the endpoint executes at least once. To ensure the endpoint processes all the events, subscriptions should be created manually.

## Running tests locally

Test projects included in the solution rely on two environment variable `AzureWebJobsStorage` used by Azure Functions SDK.
In order to run the tests, the value needs to contain a real connection string.
