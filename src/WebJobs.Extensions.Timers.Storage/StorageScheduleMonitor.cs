﻿// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the MIT License. See License.txt in the project root for license information.

using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Azure;
using Azure.Storage.Blobs;
using Microsoft.Azure.WebJobs.Host.Executors;
using Microsoft.Azure.WebJobs.Host.Storage;
using Microsoft.Azure.WebJobs.Logging;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace Microsoft.Azure.WebJobs.Extensions.Timers
{
    /// <summary>
    /// <see cref="ScheduleMonitor"/> that stores schedule information in Azure Blob storage.
    /// </summary>
    public class StorageScheduleMonitor : ScheduleMonitor
    {
        private readonly IAzureBlobStorageProvider _azureStorageProvider;

        private readonly JsonSerializer _serializer;
        private readonly ILogger _logger;
        private readonly IHostIdProvider _hostIdProvider;
        private string _timerStatusPath;
        private BlobContainerClient _containerClient;

        /// <summary>
        /// Initializes a new instance of <see cref="StorageScheduleMonitor"/>.
        /// </summary>
        /// <param name="hostIdProvider"><see cref="IHostIdProvider"/> to retrieve the current Host ID.</param>
        /// <param name="loggerFactory"><see cref="ILoggerFactory"/> for logging purposes.</param>
        /// <param name="azureStorageProvider"><see cref="IAzureBlobStorageProvider"/> to instantiate Blob-related clients for Timer operations.</param>
        public StorageScheduleMonitor(IHostIdProvider hostIdProvider, ILoggerFactory loggerFactory, IAzureBlobStorageProvider azureStorageProvider)
        {
            _hostIdProvider = hostIdProvider ?? throw new ArgumentNullException(nameof(hostIdProvider));
            _logger = loggerFactory.CreateLogger(LogCategories.CreateTriggerCategory("Timer"));
            _azureStorageProvider = azureStorageProvider ?? throw new ArgumentNullException(nameof(azureStorageProvider));

            JsonSerializerSettings settings = new JsonSerializerSettings
            {
                DateFormatHandling = DateFormatHandling.IsoDateFormat
            };
            _serializer = JsonSerializer.Create(settings);
        }

        /// <summary>
        /// Gets the blob directory where timer statuses will be stored.
        /// </summary>
        internal string TimerStatusPath
        {
            get
            {
                // We have to delay create the blob directory since we require the JobHost ID, and that will only
                // be available AFTER the host as been started
                if (string.IsNullOrEmpty(_timerStatusPath))
                {
                    string hostId = _hostIdProvider.GetHostIdAsync(CancellationToken.None).GetAwaiter().GetResult();
                    if (string.IsNullOrEmpty(hostId))
                    {
                        throw new InvalidOperationException("Unable to determine host ID.");
                    }

                    _timerStatusPath = string.Format("timers/{0}", hostId);
                }

                return _timerStatusPath;
            }
        }

        internal BlobContainerClient ContainerClient
        {
            get
            {
                if (_containerClient == null)
                {
                    if (!_azureStorageProvider.TryCreateHostingBlobContainerClient(out _containerClient))
                    {
                        throw new InvalidOperationException($"Could not create BlobContainerClient for ScheduleMonitor");
                    }
                }

                return _containerClient;
            }
        }

        /// <inheritdoc/>
        public override async Task<ScheduleStatus> GetStatusAsync(string timerName)
        {
            BlobClient statusBlobClient = await GetStatusBlobClient(timerName, createContainerIfNotExists: false);

            try
            {
                string statusLine;
                var downloadResponse = await statusBlobClient.DownloadAsync();
                using (StreamReader reader = new StreamReader(downloadResponse.Value.Content))
                {
                    statusLine = reader.ReadToEnd();
                }

                ScheduleStatus status;
                using (StringReader stringReader = new StringReader(statusLine))
                {
                    status = (ScheduleStatus)_serializer.Deserialize(stringReader, typeof(ScheduleStatus));
                }
                return status;
            }
            catch (RequestFailedException exception)
            {
                if (exception.Status == 404)
                {
                    // we haven't recorded a status yet
                    return null;
                }
                throw;
            }
        }

        /// <inheritdoc/>
        public override async Task UpdateStatusAsync(string timerName, ScheduleStatus status)
        {
            string statusLine;
            using (StringWriter stringWriter = new StringWriter())
            {
                _serializer.Serialize(stringWriter, status);
                statusLine = stringWriter.ToString();
            }

            try
            {
                BlobClient statusBlobClient = await GetStatusBlobClient(timerName, createContainerIfNotExists: true);
                using (Stream stream = new MemoryStream(Encoding.UTF8.GetBytes(statusLine)))
                {
                    await statusBlobClient.UploadAsync(stream, overwrite: true);
                }
            }
            catch (Exception ex)
            {
                // best effort
                _logger.LogError(ex, "Function '{timerName}' failed to update the timer trigger status.", timerName);
            }
        }

        private async Task<BlobClient> GetStatusBlobClient(string timerName, bool createContainerIfNotExists = false)
        {
            // Path to the status blob is:
            // timers/{hostId}/{timerName}/status
            string blobName = string.Format("{0}/{1}/status", TimerStatusPath, timerName);
            if (createContainerIfNotExists)
            {
                await ContainerClient.CreateIfNotExistsAsync();
            }

            return ContainerClient.GetBlobClient(blobName);
        }
    }
}