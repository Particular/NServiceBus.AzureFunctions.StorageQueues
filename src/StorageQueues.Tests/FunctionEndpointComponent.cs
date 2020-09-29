namespace StorageQueues.Tests
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft.WindowsAzure.Storage.Queue;
    using Newtonsoft.Json;
    using NServiceBus;
    using NServiceBus.AcceptanceTesting;
    using NServiceBus.AcceptanceTesting.Customization;
    using NServiceBus.AcceptanceTesting.Support;
    using NServiceBus.Azure.Transports.WindowsAzureStorageQueues;
    using NServiceBus.MessageInterfaces.MessageMapper.Reflection;
    using NServiceBus.Serialization;
    using NServiceBus.Settings;
    using ExecutionContext = Microsoft.Azure.WebJobs.ExecutionContext;

    abstract class FunctionEndpointComponent : IComponentBehavior
    {
        public FunctionEndpointComponent()
        {
        }

        public FunctionEndpointComponent(object triggerMessage)
        {
            Messages.Add(triggerMessage);
        }

        public Task<ComponentRunner> CreateRunner(RunDescriptor runDescriptor)
        {
            return Task.FromResult<ComponentRunner>(
                new FunctionRunner(
                    Messages,
                    CustomizeConfiguration,
                    runDescriptor.ScenarioContext,
                    GetType()));
        }

        public IList<object> Messages { get; } = new List<object>();

        public Action<StorageQueueTriggeredEndpointConfiguration> CustomizeConfiguration { private get; set; } = (_ => { });


        class FunctionRunner : ComponentRunner
        {
            public FunctionRunner(
                IList<object> messages,
                Action<StorageQueueTriggeredEndpointConfiguration> configurationCustomization,
                ScenarioContext scenarioContext,
                Type functionComponentType)
            {
                this.messages = messages;
                this.configurationCustomization = configurationCustomization;
                this.scenarioContext = scenarioContext;
                this.functionComponentType = functionComponentType;
                this.Name = functionComponentType.FullName;

                var serializer = new NewtonsoftSerializer();
                messageSerializer = serializer.Configure(new SettingsHolder())(new MessageMapper());
            }

            public override string Name { get; }

            public override Task Start(CancellationToken token)
            {
                endpoint = new TestableFunctionEndpoint(context =>
                {
                    var functionEndpointConfiguration = new StorageQueueTriggeredEndpointConfiguration(Name);

                    // Tests are not exercising pub/sub and processing pipeline is the same as with the regular endpoints (message-driven pub/sub should work as usual)
                    functionEndpointConfiguration.Transport.DisablePublishing();

                    var endpointConfiguration = functionEndpointConfiguration.AdvancedConfiguration;

                    endpointConfiguration.TypesToIncludeInScan(functionComponentType.GetTypesScopedByTestClass());

                    endpointConfiguration.Recoverability()
                        .Immediate(i => i.NumberOfRetries(0))
                        .Delayed(d => d.NumberOfRetries(0))
                        .Failed(c => c
                            // track messages sent to the error queue to fail the test
                            .OnMessageSentToErrorQueue(failedMessage =>
                            {
                                scenarioContext.FailedMessages.AddOrUpdate(
                                    Name,
                                    new[] { failedMessage },
                                    (_, fm) =>
                                    {
                                        var messages = fm.ToList();
                                        messages.Add(failedMessage);
                                        return messages;
                                    });
                                return Task.CompletedTask;
                            }));

                    endpointConfiguration.RegisterComponents(c => c.RegisterSingleton(scenarioContext.GetType(), scenarioContext));

                    configurationCustomization(functionEndpointConfiguration);
                    return functionEndpointConfiguration;
                });

                return Task.CompletedTask;
            }

            public override async Task ComponentsStarted(CancellationToken token)
            {
                foreach (var message in messages)
                {
                    var transportMessage = GenerateMessage(message);
                    var context = new ExecutionContext();
                    await endpoint.Process(transportMessage, context);
                }
            }

            public override Task Stop()
            {
                if (scenarioContext.FailedMessages.TryGetValue(Name, out var failedMessages))
                {
                    throw new MessageFailedException(failedMessages.First(), scenarioContext);
                }

                return base.Stop();
            }

            CloudQueueMessage GenerateMessage(object message)
            {
                var messageWrapper = new MessageWrapper();
                using (var stream = new MemoryStream())
                {
                    messageSerializer.Serialize(message, stream);
                    messageWrapper.Body = stream.ToArray();
                }

                messageWrapper.Headers = new Dictionary<string, string> {{"NServiceBus.EnclosedMessageTypes", message.GetType().FullName}};

                var cloudQueueMessage = new CloudQueueMessage(JsonConvert.SerializeObject(messageWrapper));
                var dequeueCountProperty = typeof(CloudQueueMessage).GetProperty("DequeueCount");
                dequeueCountProperty.SetValue(cloudQueueMessage, 1);
                return cloudQueueMessage;
            }

            readonly Action<StorageQueueTriggeredEndpointConfiguration> configurationCustomization;
            readonly ScenarioContext scenarioContext;
            readonly Type functionComponentType;
            IList<object> messages;
            FunctionEndpoint endpoint;
            IMessageSerializer messageSerializer;
        }
    }
}