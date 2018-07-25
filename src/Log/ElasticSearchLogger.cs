﻿using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Elasticsearch.Net;
using PipServices.Commons.Config;
using PipServices.Commons.Convert;
using PipServices.Commons.Data;
using PipServices.Commons.Errors;
using PipServices.Commons.Refer;
using PipServices.Commons.Run;
using PipServices.Components.Log;
using PipServices.Rpc.Connect;

namespace PipServices.ElasticSearch.Log
{
    public class ElasticSearchLogger : CachedLogger, IReferenceable, IOpenable
    {
        private ConsoleLogger _errorConsoleLogger = new ConsoleLogger { Level = LogLevel.Trace };
        private FixedRateTimer _timer;
        private HttpConnectionResolver _connectionResolver = new HttpConnectionResolver();
        private ElasticLowLevelClient _client;
        private string _indexName = "log";
        private bool _dailyIndex = false;
        private string _currentIndexName;

        public ElasticSearchLogger()
        { }

        public override void Configure(ConfigParams config)
        {
            base.Configure(config);
            _connectionResolver.Configure(config);
            _errorConsoleLogger.Configure(config);

            _indexName = config.GetAsStringWithDefault("index", _indexName);
            _dailyIndex = config.GetAsBooleanWithDefault("daily", _dailyIndex);
        }

        public override void SetReferences(IReferences references)
        {
            base.SetReferences(references);
            _connectionResolver.SetReferences(references);
            _errorConsoleLogger.SetReferences(references);
        }

        protected override void Write(LogLevel level, string correlationId, Exception error, string message)
        {
            if (Level < level)
            {
                return;
            }

            base.Write(level, correlationId, error, message);
        }

        public bool IsOpened()
        {
            return _timer != null;
        }

        public async Task OpenAsync(string correlationId)
        {
            if (IsOpened()) return;

            var connection = await _connectionResolver.ResolveAsync(correlationId);
            var uri = new Uri(connection.Uri);

            try
            {
                // Create client
                var settings = new ConnectionConfiguration(uri)
                    .RequestTimeout(TimeSpan.FromMinutes(2))
                    .ThrowExceptions(true);
                _client = new ElasticLowLevelClient(settings);

                // Create index if it doesn't exist
                await CreateIndex(correlationId, true);

                if (_timer == null)
                {
                    _timer = new FixedRateTimer(OnTimer, _interval, _interval);
                    _timer.Start();
                }
            }
            catch
            {
                // Do nothing if elastic search client was not initialized
                _errorConsoleLogger.Error(correlationId, $"Failed to initialize Elastic Search Logger with uri='{uri}'");
            }
        }

        private string GetCurrentIndex() 
        {
            if (!_dailyIndex) return _indexName;

            var today = DateTime.UtcNow.Date;
            var dateSuffix = today.ToString("yyyyMMdd");
            return _indexName + "-" + dateSuffix;  
        }

        private async Task CreateIndex(string correlationId, bool force)
        {
            var newIndex = GetCurrentIndex();
            if (!force && _currentIndexName == newIndex) return;

            _currentIndexName = newIndex;
            var response = await _client.IndicesExistsAsync<StringResponse>(_currentIndexName);
            if (response.HttpStatusCode == 404)
            {
                var request = new
                {
                    settings = new
                    {
                        number_of_shards = 1
                    },
                    mappings = new
                    {
                        log_message = new
                        {
                            properties = new
                            {
                                time = new { type = "date", index = true },
                                source = new { type = "keyword", index = true },
                                level = new { type = "keyword", index = true },
                                correlation_id = new { type = "text", index = true },
                                error = new
                                {
                                    type = "object",
                                    properties = new
                                    {
                                        type = new { type = "keyword", index = true },
                                        category = new { type = "keyword", index = true },
                                        status = new { type = "integer", index = false },
                                        code = new { type = "keyword", index = true },
                                        message = new { type = "text", index = false },
                                        details = new { type = "object" },
                                        correlation_id = new { type = "text", index = false },
                                        cause = new { type = "text", index = false },
                                        stack_trace = new { type = "text", index = false }
                                    }
                                },
                                message = new { type = "text", index = false }
                            }
                        }
                    }
                };
                var json = JsonConverter.ToJson(request);
                try
                {
                    response = await _client.IndicesCreateAsync<StringResponse>(_currentIndexName, PostData.String(json));
                    if (!response.Success)
                        throw new ConnectionException(correlationId, "CANNOT_CREATE_INDEX", response.Body);
                }
                catch (Exception ex)
                {
                    if (!ex.Message.Contains("resource_already_exists"))
                        throw;
                }
            }
            else if (!response.Success)
            {
                throw new ConnectionException(correlationId, "CONNECTION_FAILED", response.Body);
            }
        }

        public async Task CloseAsync(string correlationId)
        {
            // Log all remaining messages before closing
            Dump();

            if (_timer != null)
            {
                _timer.Stop();
                _timer = null;
            }

            _client = null;

            await Task.Delay(0);
        }

        private void OnTimer()
        {
            Dump();
        }

        protected override void Save(List<LogMessage> messages)
        {
            if (messages == null || messages.Count == 0) return;

            if (_client == null)
            {
                throw new InvalidStateException("elasticsearch_logger", "NOT_OPENED", "ElasticSearchLogger is not opened");
            }

            lock (_lock)
            {
                CreateIndex("elasticsearch_logger", false).Wait();

                var bulk = new List<string>();
                foreach (var message in messages)
                {
                    bulk.Add(JsonConverter.ToJson(new { index = new { _index = _currentIndexName, _type = "log_message", _id = IdGenerator.NextLong() } }));
                    bulk.Add(JsonConverter.ToJson(message));
                }

                try
                {
                    var response = _client.Bulk<StringResponse>(PostData.MultiJson(bulk));
                    if (!response.Success)
                    {
                        throw new InvocationException("elasticsearch_logger", "REQUEST_FAILED", response.Body);
                    }
                }
                catch
                {
                    // Do nothing if elastic search client was not enable to process bulk of messages
                    _errorConsoleLogger.Error(null, "Failed to bulk messages with Elastic Search Logger.");
                }
            }
        }
    }
}
