﻿using System;
using System.Collections.Generic;
using System.Configuration;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using ECommon.Components;
using ECommon.Configurations;
using ECommon.Logging;
using ECommon.Remoting;
using ECommon.Scheduling;
using ECommon.Socketing;
using EQueue.Clients.Producers;
using EQueue.Configurations;
using EQueue.Protocols;
using EQueue.Utils;
using ECommonConfiguration = ECommon.Configurations.Configuration;

namespace QuickStart.ProducerClient
{
    class Program
    {
        static long _previousSentCount = 0;
        static long _sentCount = 0;
        static long _calculateCount = 0;
        static string _mode;
        static bool _hasError;
        static ILogger _logger;
        static IScheduleService _scheduleService;
        static IRTStatisticService _rtStatisticService;

        static void Main(string[] args)
        {
            InitializeEQueue();
            SendMessageTest();
            StartPrintThroughputTask();
            Console.ReadLine();
        }

        static void InitializeEQueue()
        {
            ECommonConfiguration
                .Create()
                .UseAutofac()
                .RegisterCommonComponents()
                .UseLog4Net()
                .UseJsonNet()
                .RegisterUnhandledExceptionHandler()
                .RegisterEQueueComponents()
                .SetDefault<IQueueSelector, QueueAverageSelector>();

            _logger = ObjectContainer.Resolve<ILoggerFactory>().Create(typeof(Program).Name);
            _scheduleService = ObjectContainer.Resolve<IScheduleService>();
            _rtStatisticService = ObjectContainer.Resolve<IRTStatisticService>();
        }
        static void SendMessageTest()
        {
            _mode = ConfigurationManager.AppSettings["Mode"];

            var clusterName = ConfigurationManager.AppSettings["ClusterName"];
            var address = ConfigurationManager.AppSettings["NameServerAddress"];
            var nameServerAddress = string.IsNullOrEmpty(address) ? SocketUtils.GetLocalIPV4() : IPAddress.Parse(address);
            var clientCount = int.Parse(ConfigurationManager.AppSettings["ClientCount"]);
            var messageSize = int.Parse(ConfigurationManager.AppSettings["MessageSize"]);
            var messageCount = long.Parse(ConfigurationManager.AppSettings["MessageCount"]);
            var batchSize = int.Parse(ConfigurationManager.AppSettings["BatchSize"]);
            var actions = new List<Action>();
            var payload = new byte[messageSize];
            var topic = ConfigurationManager.AppSettings["Topic"];

            for (var i = 0; i < clientCount; i++)
            {
                var setting = new ProducerSetting
                {
                    ClusterName = clusterName,
                    NameServerList = new List<IPEndPoint> { new IPEndPoint(nameServerAddress, 9493) }
                };
                var producer = new Producer(setting);
                if (_mode == "Callback")
                {
                    producer.RegisterResponseHandler(new ResponseHandler { BatchSize = batchSize });
                }
                producer.Start();
                actions.Add(() => SendMessages(producer, _mode, batchSize, messageCount, topic, payload));
            }

            Task.Factory.StartNew(() => Parallel.Invoke(actions.ToArray()));
        }
        static void SendMessages(Producer producer, string mode, int batchSize, long messageCount, string topic, byte[] payload)
        {
            _logger.Info("----Send message starting----");

            var sendAction = default(Action<long>);

            if (_mode == "Oneway")
            {
                sendAction = index =>
                {
                    if (batchSize == 1)
                    {
                        var message = new Message(topic, 100, payload);
                        producer.SendOneway(message, index.ToString());
                        _rtStatisticService.AddRT((DateTime.Now - message.CreatedTime).TotalMilliseconds);
                        Interlocked.Increment(ref _sentCount);
                    }
                    else
                    {
                        var messages = new List<Message>();
                        for (var i = 0; i < batchSize; i++)
                        {
                            messages.Add(new Message(topic, 100, payload));
                        }
                        producer.BatchSendOneway(messages, index.ToString());
                        var currentTime = DateTime.Now;
                        foreach (var message in messages)
                        {
                            _rtStatisticService.AddRT((currentTime - message.CreatedTime).TotalMilliseconds);
                            Interlocked.Increment(ref _sentCount);
                        }
                    }
                };
            }
            else if (_mode == "Sync")
            {
                sendAction = index =>
                {
                    if (batchSize == 1)
                    {
                        var message = new Message(topic, 100, payload);
                        var result = producer.Send(message, index.ToString());
                        if (result.SendStatus != SendStatus.Success)
                        {
                            throw new Exception(result.ErrorMessage);
                        }
                        _rtStatisticService.AddRT((DateTime.Now - message.CreatedTime).TotalMilliseconds);
                        Interlocked.Increment(ref _sentCount);
                    }
                    else
                    {
                        var messages = new List<Message>();
                        for (var i = 0; i < batchSize; i++)
                        {
                            messages.Add(new Message(topic, 100, payload));
                        }
                        var result = producer.BatchSend(messages, index.ToString());
                        if (result.SendStatus != SendStatus.Success)
                        {
                            throw new Exception(result.ErrorMessage);
                        }
                        var currentTime = DateTime.Now;
                        foreach (var message in messages)
                        {
                            _rtStatisticService.AddRT((currentTime - message.CreatedTime).TotalMilliseconds);
                            Interlocked.Increment(ref _sentCount);
                        }
                    }
                };
            }
            else if (_mode == "Async")
            {
                sendAction = index =>
                {
                    if (batchSize == 1)
                    {
                        var message = new Message(topic, 100, payload);
                        producer.SendAsync(message, index.ToString()).ContinueWith(t =>
                        {
                            if (t.Exception != null)
                            {
                                _hasError = true;
                                _logger.ErrorFormat("Send message has exception, errorMessage: {0}", t.Exception.GetBaseException().Message);
                                return;
                            }
                            if (t.Result == null)
                            {
                                _hasError = true;
                                _logger.Error("Send message timeout.");
                                return;
                            }
                            if (t.Result.SendStatus != SendStatus.Success)
                            {
                                _hasError = true;
                                _logger.ErrorFormat("Send message failed, errorMessage: {0}", t.Result.ErrorMessage);
                                return;
                            }
                            _rtStatisticService.AddRT((DateTime.Now - message.CreatedTime).TotalMilliseconds);
                            Interlocked.Increment(ref _sentCount);
                        });
                    }
                    else
                    {
                        var messages = new List<Message>();
                        for (var i = 0; i < batchSize; i++)
                        {
                            messages.Add(new Message(topic, 100, payload));
                        }
                        producer.BatchSendAsync(messages, index.ToString()).ContinueWith(t =>
                        {
                            if (t.Exception != null)
                            {
                                _hasError = true;
                                _logger.ErrorFormat("Send message has exception, errorMessage: {0}", t.Exception.GetBaseException().Message);
                                return;
                            }
                            if (t.Result == null)
                            {
                                _hasError = true;
                                _logger.Error("Send message timeout.");
                                return;
                            }
                            if (t.Result.SendStatus != SendStatus.Success)
                            {
                                _hasError = true;
                                _logger.ErrorFormat("Send message failed, errorMessage: {0}", t.Result.ErrorMessage);
                                return;
                            }
                            var currentTime = DateTime.Now;
                            foreach (var message in messages)
                            {
                                _rtStatisticService.AddRT((currentTime - message.CreatedTime).TotalMilliseconds);
                                Interlocked.Increment(ref _sentCount);
                            }
                        });
                    }
                };
            }
            else if (_mode == "Callback")
            {
                sendAction = index =>
                {
                    if (batchSize == 1)
                    {
                        var message = new Message(topic, 100, payload);
                        producer.SendWithCallback(message, index.ToString());
                    }
                    else
                    {
                        var messages = new List<Message>();
                        for (var i = 0; i < batchSize; i++)
                        {
                            messages.Add(new Message(topic, 100, payload));
                        }
                        producer.BatchSendWithCallback(messages, index.ToString());
                    }
                };
            }     

            Task.Factory.StartNew(() =>
            {
                for (var i = 0L; i < messageCount; i++)
                {
                    try
                    {
                        sendAction(i);
                    }
                    catch (Exception ex)
                    {
                        _hasError = true;
                        _logger.ErrorFormat("Send message failed, errorMsg:{0}", ex.Message);
                    }

                    if (_hasError)
                    {
                        Thread.Sleep(3000);
                        _hasError = false;
                    }
                }
            });
        }

        static void StartPrintThroughputTask()
        {
            _scheduleService.StartTask("PrintThroughput", PrintThroughput, 1000, 1000);
        }
        static void PrintThroughput()
        {
            var totalSentCount = _sentCount;
            var throughput = totalSentCount - _previousSentCount;
            _previousSentCount = totalSentCount;
            if (throughput > 0)
            {
                _calculateCount++;
            }

            var average = 0L;
            if (_calculateCount > 0)
            {
                average = totalSentCount / _calculateCount;
            }
            _logger.InfoFormat("Send message mode: {0}, totalSent: {1}, throughput: {2}/s, average: {3}, rt: {4:F3}ms", _mode, totalSentCount, throughput, average, _rtStatisticService.ResetAndGetRTStatisticInfo());
        }

        class ResponseHandler : IResponseHandler
        {
            public int BatchSize;

            public void HandleResponse(RemotingResponse remotingResponse)
            {
                if (BatchSize == 1)
                {
                    var sendResult = Producer.ParseSendResult(remotingResponse);
                    if (sendResult.SendStatus != SendStatus.Success)
                    {
                        _hasError = true;
                        _logger.Error(sendResult.ErrorMessage);
                        return;
                    }
                    Interlocked.Increment(ref _sentCount);
                    _rtStatisticService.AddRT((DateTime.Now - sendResult.MessageStoreResult.CreatedTime).TotalMilliseconds);
                }
                else
                {
                    var sendResult = Producer.ParseBatchSendResult(remotingResponse);
                    if (sendResult.SendStatus != SendStatus.Success)
                    {
                        _hasError = true;
                        _logger.Error(sendResult.ErrorMessage);
                        return;
                    }
                    var currentTime = DateTime.Now;
                    foreach (var result in sendResult.MessageStoreResult.MessageResults)
                    {
                        Interlocked.Increment(ref _sentCount);
                        _rtStatisticService.AddRT((currentTime - result.CreatedTime).TotalMilliseconds);
                    }
                }
            }
        }
    }
}
