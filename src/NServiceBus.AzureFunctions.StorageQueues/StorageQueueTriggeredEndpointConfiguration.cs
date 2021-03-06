﻿namespace NServiceBus
{
    using System;
    using System.Threading.Tasks;
    using AzureFunctions.StorageQueues;
    using Logging;
    using Microsoft.Azure.WebJobs;
    using Serialization;
    using Transport;

    /// <summary>
    /// Represents a serverless NServiceBus endpoint running within an AzureStorageQueue trigger.
    /// </summary>
    public class StorageQueueTriggeredEndpointConfiguration
    {
        static StorageQueueTriggeredEndpointConfiguration()
        {
            LogManager.UseFactory(FunctionsLoggerFactory.Instance);
        }

        /// <summary>
        /// Creates a serverless NServiceBus endpoint running within an AzureStorageQueue trigger.
        /// </summary>
        public StorageQueueTriggeredEndpointConfiguration(string endpointName, string connectionStringName = null)
        {
            EndpointConfiguration = new EndpointConfiguration(endpointName);

            EndpointConfiguration.Recoverability().Delayed(c => c.NumberOfRetries(0));

            recoverabilityPolicy.SendFailedMessagesToErrorQueue = true;
            EndpointConfiguration.Recoverability().CustomPolicy(recoverabilityPolicy.Invoke);

            // Disable diagnostics by default as it will fail to create the diagnostics file in the default path.
            // Can be overriden by ServerlessEndpointConfiguration.LogDiagnostics().
            EndpointConfiguration.CustomDiagnosticsWriter(_ => Task.CompletedTask);

            // 'WEBSITE_SITE_NAME' represents an Azure Function App and the environment variable is set when hosting the function in Azure.
            var functionAppName = Environment.GetEnvironmentVariable("WEBSITE_SITE_NAME") ?? Environment.MachineName;
            EndpointConfiguration.UniquelyIdentifyRunningInstance()
                .UsingCustomDisplayName(functionAppName)
                .UsingCustomIdentifier(DeterministicGuid.Create(functionAppName));

            // Look for license as an environment variable
            var licenseText = Environment.GetEnvironmentVariable("NSERVICEBUS_LICENSE");
            if (!string.IsNullOrWhiteSpace(licenseText))
            {
                EndpointConfiguration.License(licenseText);
            }

            Transport = UseTransport<AzureStorageQueueTransport>();

            var connectionString = Environment.GetEnvironmentVariable(connectionStringName ?? DefaultStorageConnectionString);
            Transport.ConnectionString(connectionString);

            var recoverability = AdvancedConfiguration.Recoverability();
            recoverability.Immediate(settings => settings.NumberOfRetries(4));
            recoverability.Delayed(settings => settings.NumberOfRetries(0));

            Transport.DelayedDelivery().DisableTimeoutManager();

            EndpointConfiguration.UseSerialization<NewtonsoftSerializer>();
        }

        /// <summary>
        /// Azure Storage Queues transport
        /// </summary>
        public TransportExtensions<AzureStorageQueueTransport> Transport { get; }

        internal EndpointConfiguration EndpointConfiguration { get; }
        internal PipelineInvoker PipelineInvoker { get; private set; }

        /// <summary>
        /// Gives access to the underlying endpoint configuration for advanced configuration options.
        /// </summary>
        public EndpointConfiguration AdvancedConfiguration => EndpointConfiguration;

        /// <summary>
        /// Attempts to derive the required configuration parameters automatically from the Azure Functions related attributes via
        /// reflection.
        /// </summary>
        public static StorageQueueTriggeredEndpointConfiguration FromAttributes()
        {
            var configuration = TriggerDiscoverer.TryGet<QueueTriggerAttribute>();
            if (configuration != null)
            {
                return new StorageQueueTriggeredEndpointConfiguration(configuration.QueueName, configuration.Connection);
            }

            throw new Exception($"Unable to automatically derive the endpoint name from the QueueTrigger attribute. Make sure the attribute exists or create the {nameof(StorageQueueTriggeredEndpointConfiguration)} with the required parameter manually.");
        }

        /// <summary>
        /// Define a transport to be used when sending and publishing messages.
        /// </summary>
        protected TransportExtensions<TTransport> UseTransport<TTransport>()
            where TTransport : TransportDefinition, new()
        {
            var serverlessTransport = EndpointConfiguration.UseTransport<ServerlessTransport<TTransport>>();

            PipelineInvoker = serverlessTransport.PipelineAccess();
            return serverlessTransport.BaseTransportConfiguration();
        }

        /// <summary>
        /// Define the serializer to be used.
        /// </summary>
        public SerializationExtensions<T> UseSerialization<T>() where T : SerializationDefinition, new()
        {
            return EndpointConfiguration.UseSerialization<T>();
        }

        /// <summary>
        /// Disables moving messages to the error queue even if an error queue name is configured.
        /// </summary>
        public void DoNotSendMessagesToErrorQueue()
        {
            recoverabilityPolicy.SendFailedMessagesToErrorQueue = false;
        }

        /// <summary>
        /// Logs endpoint diagnostics information to the log. Diagnostics are logged on level <see cref="LogLevel.Info" />.
        /// </summary>
        public void LogDiagnostics()
        {
            EndpointConfiguration.CustomDiagnosticsWriter(diagnostics =>
            {
                LogManager.GetLogger("StartupDiagnostics").Info(diagnostics);
                return Task.CompletedTask;
            });
        }

        readonly ServerlessRecoverabilityPolicy recoverabilityPolicy = new ServerlessRecoverabilityPolicy();
        internal const string DefaultStorageConnectionString = "AzureWebJobsStorage";
    }
}