﻿namespace NServiceBus
{
    using Logging;
    using System;
    using AzureFunctions.StorageQueues;
    using Microsoft.Azure.WebJobs;

    /// <summary>
    /// Represents a serverless NServiceBus endpoint running within an AzureStorageQueue trigger.
    /// </summary>
    public class StorageQueueTriggeredEndpointConfiguration : ServerlessEndpointConfiguration
    {
        internal const string DefaultStorageConnectionString = "AzureWebJobsStorage";

        /// <summary>
        /// Azure Storage Queues transport
        /// </summary>
        public TransportExtensions<AzureStorageQueueTransport> Transport { get; }

        static StorageQueueTriggeredEndpointConfiguration()
        {
            LogManager.UseFactory(FunctionsLoggerFactory.Instance);
        }

        /// <summary>
        /// Creates a serverless NServiceBus endpoint running within an AzureStorageQueue trigger.
        /// </summary>
        public StorageQueueTriggeredEndpointConfiguration(string endpointName, string connectionStringName = null) : base(endpointName)
        {
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
        /// Attempts to derive the required configuration parameters automatically from the Azure Functions related attributes via reflection.
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
    }
}