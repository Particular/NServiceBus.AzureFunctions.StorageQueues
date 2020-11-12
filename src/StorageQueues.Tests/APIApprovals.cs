﻿namespace StorageQueues.ApprovalTests
{
    using NServiceBus;
    using NUnit.Framework;
    using Particular.Approvals;
    using PublicApiGenerator;

    [TestFixture]
    public class APIApprovals
    {
        [Test]
        public void Approve()
        {
            var publicApi = ApiGenerator.GeneratePublicApi(typeof(StorageQueueTriggeredEndpointConfiguration).Assembly, new ApiGeneratorOptions
            {
                ExcludeAttributes = new[] {"System.Runtime.Versioning.TargetFrameworkAttribute"}
            });
            Approver.Verify(publicApi);
        }
    }
}