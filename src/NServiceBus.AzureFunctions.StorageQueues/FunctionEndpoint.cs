﻿namespace NServiceBus
{
    using System;
    using System.IO;
    using System.Reflection;
    using System.Runtime.Loader;
    using System.Threading;
    using System.Threading.Tasks;
    using Azure.Transports.WindowsAzureStorageQueues;
    using AzureFunctions.StorageQueues;
    using Extensibility;
    using global::Newtonsoft.Json;
    using Logging;
    using Microsoft.Extensions.Logging;
    using Microsoft.WindowsAzure.Storage.Queue;
    using Transport;
    using ExecutionContext = Microsoft.Azure.WebJobs.ExecutionContext;

    /// <summary>
    /// An NServiceBus endpoint hosted in Azure Function which does not receive messages automatically but only handles
    /// messages explicitly passed to it by the caller.
    /// </summary>
    public class FunctionEndpoint : IFunctionEndpoint
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

        async Task InitializeEndpointIfNecessary(FunctionExecutionContext executionContext, CancellationToken token = default)
        {
            if (pipeline == null)
            {
                await semaphoreLock.WaitAsync(token).ConfigureAwait(false);
                try
                {
                    if (pipeline == null)
                    {
                        LogManager.GetLogger("Previews").Info("NServiceBus.AzureFunctions.ServiceBus is a preview package. Preview packages are licensed separately from the rest of the Particular Software platform and have different support guarantees. You can view the license at https://particular.net/eula/previews and the support policy at https://docs.particular.net/previews/support-policy. Customer adoption drives whether NServiceBus.AzureFunctions.ServiceBus will be incorporated into the Particular Software platform. Let us know you are using it, if you haven't already, by emailing us at support@particular.net.");
                        endpoint = await endpointFactory(executionContext).ConfigureAwait(false);

                        pipeline = configuration.PipelineInvoker;
                    }
                }
                finally
                {
                    semaphoreLock.Release();
                }
            }
        }

        /// <inheritdoc />
        public async Task Send(object message, SendOptions options, ExecutionContext executionContext, ILogger functionsLogger = null)
        {
            await InitializeEndpointUsedOutsideHandlerIfNecessary(executionContext, functionsLogger).ConfigureAwait(false);

            await endpoint.Send(message, options).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public Task Send(object message, ExecutionContext executionContext, ILogger functionsLogger = null)
        {
            return Send(message, new SendOptions(), executionContext, functionsLogger);
        }

        /// <inheritdoc />
        public async Task Send<T>(Action<T> messageConstructor, SendOptions options, ExecutionContext executionContext, ILogger functionsLogger = null)
        {
            await InitializeEndpointUsedOutsideHandlerIfNecessary(executionContext, functionsLogger).ConfigureAwait(false);

            await endpoint.Send(messageConstructor, options).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public Task Send<T>(Action<T> messageConstructor, ExecutionContext executionContext, ILogger functionsLogger = null)
        {
            return Send(messageConstructor, new SendOptions(), executionContext, functionsLogger);
        }

        /// <inheritdoc />
        public async Task Publish(object message, PublishOptions options, ExecutionContext executionContext, ILogger functionsLogger = null)
        {
            await InitializeEndpointUsedOutsideHandlerIfNecessary(executionContext, functionsLogger).ConfigureAwait(false);

            await endpoint.Publish(message, options).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public async Task Publish<T>(Action<T> messageConstructor, PublishOptions options, ExecutionContext executionContext, ILogger functionsLogger = null)
        {
            await InitializeEndpointUsedOutsideHandlerIfNecessary(executionContext, functionsLogger).ConfigureAwait(false);

            await endpoint.Publish(messageConstructor, options).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public async Task Publish(object message, ExecutionContext executionContext, ILogger functionsLogger = null)
        {
            await InitializeEndpointUsedOutsideHandlerIfNecessary(executionContext, functionsLogger).ConfigureAwait(false);

            await endpoint.Publish(message).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public async Task Publish<T>(Action<T> messageConstructor, ExecutionContext executionContext, ILogger functionsLogger = null)
        {
            await InitializeEndpointUsedOutsideHandlerIfNecessary(executionContext, functionsLogger).ConfigureAwait(false);

            await endpoint.Publish(messageConstructor).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public async Task Subscribe(Type eventType, SubscribeOptions options, ExecutionContext executionContext, ILogger functionsLogger = null)
        {
            await InitializeEndpointUsedOutsideHandlerIfNecessary(executionContext, functionsLogger).ConfigureAwait(false);

            await endpoint.Subscribe(eventType, options).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public async Task Subscribe(Type eventType, ExecutionContext executionContext, ILogger functionsLogger = null)
        {
            await InitializeEndpointUsedOutsideHandlerIfNecessary(executionContext, functionsLogger).ConfigureAwait(false);

            await endpoint.Subscribe(eventType).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public async Task Unsubscribe(Type eventType, UnsubscribeOptions options, ExecutionContext executionContext, ILogger functionsLogger = null)
        {
            await InitializeEndpointUsedOutsideHandlerIfNecessary(executionContext, functionsLogger).ConfigureAwait(false);

            await endpoint.Unsubscribe(eventType, options).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public async Task Unsubscribe(Type eventType, ExecutionContext executionContext, ILogger functionsLogger = null)
        {
            await InitializeEndpointUsedOutsideHandlerIfNecessary(executionContext, functionsLogger).ConfigureAwait(false);

            await endpoint.Unsubscribe(eventType).ConfigureAwait(false);
        }

        async Task InitializeEndpointUsedOutsideHandlerIfNecessary(ExecutionContext executionContext, ILogger functionsLogger)
        {
            FunctionsLoggerFactory.Instance.SetCurrentLogger(functionsLogger);

            var functionExecutionContext = new FunctionExecutionContext(executionContext, functionsLogger);

            await InitializeEndpointIfNecessary(functionExecutionContext).ConfigureAwait(false);
        }

        internal static void LoadAssemblies(string assemblyDirectory)
        {
            var binFiles = Directory.EnumerateFiles(
                assemblyDirectory,
                "*.dll",
                SearchOption.TopDirectoryOnly);

            var assemblyLoadContext = AssemblyLoadContext.GetLoadContext(Assembly.GetExecutingAssembly());
            foreach (var binFile in binFiles)
            {
                try
                {
                    var assemblyName = AssemblyName.GetAssemblyName(binFile);
                    if (IsRuntimeAssembly(assemblyName.GetPublicKeyToken()))
                    {
                        continue;
                    }

                    // LoadFromAssemblyName works when actually running inside a function as FunctionAssemblyLoadContext probes the "bin" folder for the assembly name
                    // this doesn't work when running with a different AssemblyLoadContext (e.g. tests) and the assembly needs to be loaded by the full path instead.
                    assemblyLoadContext.LoadFromAssemblyPath(binFile);
                    //assemblyLoadContext.LoadFromAssemblyName(assemblyName);
                }
                catch (Exception e)
                {
                    LogManager.GetLogger<FunctionEndpoint>().DebugFormat(
                        "Failed to load assembly {0}. This error can be ignored if the assembly isn't required to execute the function.{1}{2}",
                        binFile, Environment.NewLine, e);
                }
            }
        }

        static bool IsRuntimeAssembly(byte[] publicKeyToken)
        {
            var tokenString = BitConverter.ToString(publicKeyToken).Replace("-", string.Empty).ToLowerInvariant();

            switch (tokenString)
            {
                case "b77a5c561934e089": // Microsoft
                case "7cec85d7bea7798e":
                case "b03f5f7f11d50a3a":
                case "31bf3856ad364e35":
                case "cc7b13ffcd2ddd51":
                case "adb9793829ddae60":
                case "7e34167dcc6d6d8c": // Microsoft.Azure.ServiceBus
                case "23ec7fc2d6eaa4a5": // Microsoft.Data.SqlClient
                case "50cebf1cceb9d05e": // Mono.Cecil
                case "30ad4fe6b2a6aeed": // Newtonsoft.Json
                case "9fc386479f8a226c": // NServiceBus
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>
        /// Provides a function to locate the file system directory containing the binaries to be loaded and scanned.
        /// When using functions, assemblies are moved to a 'bin' folder within ExecutionContext.FunctionAppDirectory.
        /// </summary>
        protected Func<FunctionExecutionContext, string> AssemblyDirectoryResolver = functionExecutionContext => Path.Combine(functionExecutionContext.ExecutionContext.FunctionAppDirectory, "bin");

        readonly SemaphoreSlim semaphoreLock = new SemaphoreSlim(initialCount: 1, maxCount: 1);
        Func<FunctionExecutionContext, Task<IEndpointInstance>> endpointFactory;
        StorageQueueTriggeredEndpointConfiguration configuration;
        PipelineInvoker pipeline;
        IEndpointInstance endpoint;

        static readonly global::Newtonsoft.Json.JsonSerializer JsonSerializer = new global::Newtonsoft.Json.JsonSerializer();
    }
}