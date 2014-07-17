﻿/* Copyright 2013-2014 MongoDB Inc.
*
* Licensed under the Apache License, Version 2.0 (the "License");
* you may not use this file except in compliance with the License.
* You may obtain a copy of the License at
*
* http://www.apache.org/licenses/LICENSE-2.0
*
* Unless required by applicable law or agreed to in writing, software
* distributed under the License is distributed on an "AS IS" BASIS,
* WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
* See the License for the specific language governing permissions and
* limitations under the License.
*/

using System;
using System.Diagnostics;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using MongoDB.Bson;
using MongoDB.Driver.Core.Async;
using MongoDB.Driver.Core.ConnectionPools;
using MongoDB.Driver.Core.Connections;
using MongoDB.Driver.Core.Misc;
using MongoDB.Driver.Core.Servers.Events;
using MongoDB.Driver.Core.WireProtocol;

namespace MongoDB.Driver.Core.Servers
{
    /// <summary>
    /// Represents a server in a MongoDB cluster.
    /// </summary>
    public class Server : IRootServer
    {
        // fields
        private readonly ExponentiallyWeightedMovingAverage _averageRoundTripTimeCalculator = new ExponentiallyWeightedMovingAverage(0.2);
        private readonly CancellationTokenSource _backgroundTaskCancellationTokenSource = new CancellationTokenSource();
        private readonly IConnectionPool _connectionPool;
        private ServerDescription _description;
        private TaskCompletionSource<bool> _descriptionChangedTaskCompletionSource = new TaskCompletionSource<bool>();
        private bool _disposed;
        private readonly DnsEndPoint _endPoint;
        private InterruptibleDelay _heartbeatDelay = new InterruptibleDelay(TimeSpan.Zero);
        private readonly IServerListener _listener;
        private object _lock = new object();
        private readonly ServerSettings _settings;

        // events
        public event EventHandler<ServerDescriptionChangedEventArgs> DescriptionChanged;

        // constructors
        internal Server(DnsEndPoint endPoint, ServerSettings settings, IConnectionPoolFactory connectionPoolFactory, IServerListener listener)
        {
            _endPoint = endPoint;
            _settings = settings;
            _description = new ServerDescription(endPoint);
            _connectionPool = connectionPoolFactory.CreateConnectionPool(endPoint);
            _listener = listener;
        }

        // properties
        public ServerDescription Description
        {
            get
            {
                lock (_lock)
                {
                    return _description;
                }
            }
        }

        public DnsEndPoint EndPoint
        {
            get { return _endPoint; }
        }

        // methods
        private void CheckIfDescriptionChanged(ServerDescription newDescription)
        {
            var descriptionChanged = false;
            ServerDescription oldDescription = null;
            TaskCompletionSource<bool> oldDescriptionChangedTaskCompletionSource = null;

            lock (_lock)
            {
                if (!_description.Equals(newDescription))
                {
                    descriptionChanged = true;
                    oldDescription = _description;
                    oldDescriptionChangedTaskCompletionSource = _descriptionChangedTaskCompletionSource;
                    _description = newDescription.WithRevision(oldDescription.Revision + 1);
                    _descriptionChangedTaskCompletionSource = new TaskCompletionSource<bool>();
                }
            }

            if (descriptionChanged)
            {
                oldDescriptionChangedTaskCompletionSource.TrySetResult(true);
                OnDescriptionChanged(oldDescription, newDescription);
            }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            lock (_lock)
            {
                if (disposing)
                {
                    if (!_disposed)
                    {
                        _backgroundTaskCancellationTokenSource.Cancel();
                        _backgroundTaskCancellationTokenSource.Dispose();
                        _connectionPool.Dispose();
                    }
                }
                _disposed = true;
            }
        }

        public virtual async Task<IConnection> GetConnectionAsync(TimeSpan timeout, CancellationToken cancellationToken)
        {
            return await _connectionPool.AcquireConnectionAsync(timeout, cancellationToken);
        }

        private async Task<HeartbeatInfo> HeartbeatAsync(IConnection connection, TimeSpan timeout, CancellationToken cancellationToken)
        {
            var slidingTimeout = new SlidingTimeout(timeout);

            var stopwatch = Stopwatch.StartNew();
            var isMasterResult = new IsMasterResult(await new CommandWireProtocol("admin", new BsonDocument("isMaster", 1), true).ExecuteAsync(connection, slidingTimeout, cancellationToken));
            stopwatch.Stop();
            var roundTripTime = stopwatch.Elapsed;

            var buildInfoResult = new BuildInfoResult(await new CommandWireProtocol("admin", new BsonDocument("buildInfo", 1), true).ExecuteAsync(connection, slidingTimeout, cancellationToken));

            return new HeartbeatInfo { Connection = connection, RoundTripTime = roundTripTime, IsMasterResult = isMasterResult, BuildInfoResult = buildInfoResult };
        }

        public async Task HeartbeatBackgroundTask()
        {
            var cancellationToken = _backgroundTaskCancellationTokenSource.Token;
            try
            {
                IConnection connection = null;
                try
                {
                    while (true)
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        var heartbeatInfo = await HeartbeatWithRetryAsync(connection, _settings.HeartbeatTimeout, cancellationToken);
                        connection = heartbeatInfo.Connection; // HeartbeatWithRetryAsync creates (or recreates) connections as necessary

                        var averageRoundTripTime = _averageRoundTripTimeCalculator.AddSample(heartbeatInfo.RoundTripTime);
                        var averageRoundTripTimeRounded = TimeSpan.FromMilliseconds(Math.Round(averageRoundTripTime.TotalMilliseconds));
                        var serverDescription = ToServerDescription(heartbeatInfo, averageRoundTripTimeRounded);
                        CheckIfDescriptionChanged(serverDescription);

                        var heartbeatDelay = new InterruptibleDelay(_settings.HeartbeatInterval);
                        lock (_lock)
                        {
                            _heartbeatDelay = heartbeatDelay;
                        }
                        await heartbeatDelay.Task;
                    }
                }
                finally
                {
                    if (connection != null)
                    {
                        connection.Dispose();
                    }
                }
            }
            catch (TaskCanceledException)
            {
                // ignore TaskCanceledException
            }
        }

        private async Task<HeartbeatInfo> HeartbeatWithRetryAsync(IConnection connection, TimeSpan timeout, CancellationToken cancellationToken)
        {
            HeartbeatInfo heartbeatInfo = null;

            // if the first attempt fails try a second time with a new connection
            for (var attempt = 0; heartbeatInfo == null && attempt < 2; attempt++)
            {
                try
                {
                    var slidingTimeout = new SlidingTimeout(timeout);
                    if (connection == null)
                    {
                        connection = await _connectionPool.AcquireConnectionAsync(slidingTimeout, cancellationToken);
                    }

                    heartbeatInfo = await HeartbeatAsync(connection, slidingTimeout, cancellationToken);
                }
                catch
                {
                    // TODO: log the exception?
                    if (connection != null)
                    {
                        connection.Dispose();
                        connection = null;
                    }
                }
            }

            return heartbeatInfo ?? new HeartbeatInfo { Connection = null };
        }

        public void Initialize()
        {
            var info = _description.WithState(ServerState.Disconnected);
            CheckIfDescriptionChanged(info);

            HeartbeatBackgroundTask().LogUnobservedExceptions();
        }

        private void OnDescriptionChanged(ServerDescription oldDescription, ServerDescription newDescription)
        {
            var args = new ServerDescriptionChangedEventArgs(oldDescription, newDescription);

            if (_listener != null)
            {
                _listener.ServerDescriptionChanged(args);
            }

            var handler = DescriptionChanged;
            if (handler != null)
            {
                try { handler(this, args); }
                catch { } // ignore exceptions
            }
        }

        private ServerDescription ToServerDescription(HeartbeatInfo heartbeatInfo, TimeSpan averageRoundTripTime)
        {
            if (heartbeatInfo.Connection == null)
            {
                return _description.WithState(ServerState.Disconnected);
            }
            else
            {
                return _description.WithHeartbeatInfo(heartbeatInfo.IsMasterResult, heartbeatInfo.BuildInfoResult, averageRoundTripTime);
            }
        }

        // nested types
        private class HeartbeatInfo
        {
            public IConnection Connection;
            public TimeSpan RoundTripTime;
            public IsMasterResult IsMasterResult;
            public BuildInfoResult BuildInfoResult;
        }
    }
}