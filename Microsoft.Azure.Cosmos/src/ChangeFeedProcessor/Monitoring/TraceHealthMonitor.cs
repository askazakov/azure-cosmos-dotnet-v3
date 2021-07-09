﻿//------------------------------------------------------------
// Copyright (c) Microsoft Corporation.  All rights reserved.
//------------------------------------------------------------

namespace Microsoft.Azure.Cosmos.ChangeFeed.Monitoring
{
    using System;
    using System.Threading.Tasks;
    using Microsoft.Azure.Cosmos.Core.Trace;

    /// <summary>
    /// A monitor which logs the errors only as traces
    /// </summary>
    internal sealed class TraceHealthMonitor : ChangeFeedProcessorHealthMonitor
    {
        public override Task NotifyErrorAsync(
             string leaseToken,
             Exception exception)
        {
            Extensions.TraceException(exception);
            DefaultTrace.TraceError($"Error detected for lease {leaseToken}. ");

            return Task.CompletedTask;
        }
    }
}