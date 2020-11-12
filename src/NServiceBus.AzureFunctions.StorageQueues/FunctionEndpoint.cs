namespace NServiceBus
{
    using System;
    using System.IO;
    using System.Threading;
    using System.Threading.Tasks;
    using Azure.Transports.WindowsAzureStorageQueues;
    using AzureFunctions.StorageQueues;
    using Extensibility;
    using global::Newtonsoft.Json;
    using Microsoft.Extensions.Logging;
    using Microsoft.WindowsAzure.Storage.Queue;
    using Transport;
    using ExecutionContext = Microsoft.Azure.WebJobs.ExecutionContext;

    /// <summary>
    /// An NServiceBus endpoint hosted in Azure Function which does not receive messages automatically but only handles
    /// messages explicitly passed to it by the caller.
    /// </summary>
    public class FunctionEndpoint : ServerlessEndpoint<StorageQueueTriggeredEndpointConfiguration>
    {
        /// <summary>
        /// Create a new endpoint hosting in Azure Function.
        /// </summary>
        public FunctionEndpoint(Func<FunctionExecutionContext, StorageQueueTriggeredEndpointConfiguration> configurationFactory)
        {
            endpointFactory = executionContext =>
            {
                LoadAssemblies(AssemblyDirectoryResolver(executionContext));

                configuration = configurationFactory(executionContext);
                return Endpoint.Start(configuration.EndpointConfiguration);
            };
        }

        // This ctor is used for the FunctionsHostBuilder scenario where the endpoint is created already during configuration time using the function host's container.
        internal FunctionEndpoint(IStartableEndpointWithExternallyManagedContainer externallyManagedContainerEndpoint,
            StorageQueueTriggeredEndpointConfiguration configuration, IServiceProvider serviceProvider)
        {
            this.configuration = configuration;
            endpointFactory = _ => externallyManagedContainerEndpoint.Start(serviceProvider);
        }


        /// <summary>
        /// Processes a message received from an AzureStorageQueue trigger using the NServiceBus message pipeline.
        /// </summary>
        public async Task Process(CloudQueueMessage message, ExecutionContext executionContext, ILogger functionsLogger = null)
        {
            FunctionsLoggerFactory.Instance.SetCurrentLogger(functionsLogger);

            MessageWrapper wrapper;
            // Read message content via StreamReader to handle BOM correctly.
            using (var memoryStream = new MemoryStream(message.AsBytes))
            using (var reader = new StreamReader(memoryStream))
            {
                wrapper = JsonSerializer.Deserialize<MessageWrapper>(new JsonTextReader(reader));
            }

            var messageContext = CreateMessageContext(wrapper);
            var functionExecutionContext = new FunctionExecutionContext(executionContext, functionsLogger);

            await InitializeEndpointIfNecessary(functionExecutionContext).ConfigureAwait(false);

            try
            {
                await pipeline.PushMessage(messageContext).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                var errorContext = new ErrorContext(
                    exception,
                    wrapper.GetHeaders(),
                    messageContext.MessageId,
                    messageContext.Body,
                    new TransportTransaction(),
                    message.DequeueCount);

                var errorHandleResult = await pipeline.PushFailedMessage(errorContext).ConfigureAwait(false);

                if (errorHandleResult == ErrorHandleResult.Handled)
                {
                    // return to signal to the Functions host it can complete the incoming message
                    return;
                }

                throw;
            }

            MessageContext CreateMessageContext(MessageWrapper originalMessage)
            {
                return new MessageContext(
                    originalMessage.GetMessageId(),
                    originalMessage.GetHeaders(),
                    originalMessage.Body,
                    new TransportTransaction(),
                    new CancellationTokenSource(),
                    new ContextBag());
            }
        }

        static readonly global::Newtonsoft.Json.JsonSerializer JsonSerializer = new global::Newtonsoft.Json.JsonSerializer();
    }
}