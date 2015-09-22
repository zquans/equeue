﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using ECommon.Extensions;
using ECommon.Logging;
using ECommon.Scheduling;
using ECommon.Utilities;

namespace EQueue.Broker
{
    public class QueueService : IQueueService
    {
        private readonly ConcurrentDictionary<string, Queue> _queueDict;
        private readonly IQueueStore _queueStore;
        private readonly IMessageStore _messageStore;
        private readonly IOffsetManager _offsetManager;
        private readonly IScheduleService _scheduleService;
        private readonly ILogger _logger;
        private int _isRemovingConsumedQueueIndex;
        private int _isRemovingExceedMaxCacheQueueIndex;

        public QueueService(IQueueStore queueStore, IMessageStore messageStore, IOffsetManager offsetManager, IScheduleService scheduleService, ILoggerFactory loggerFactory)
        {
            _queueDict = new ConcurrentDictionary<string, Queue>();
            _queueStore = queueStore;
            _messageStore = messageStore;
            _offsetManager = offsetManager;
            _scheduleService = scheduleService;
            _logger = loggerFactory.Create(GetType().FullName);
        }

        public void Start()
        {
            //先清理状态
            _queueDict.Clear();
            _scheduleService.StopTask("QueueService.RemoveConsumedQueueIndex");
            _scheduleService.StopTask("QueueService.RemoveExceedMaxCacheQueueIndex");

            //再重新加载状态
            var chunkConfig = BrokerController.Instance.Setting.QueueChunkConfig;
            var pathList = Directory
                            .EnumerateDirectories(chunkConfig.BasePath, "*", SearchOption.AllDirectories)
                            .OrderBy(x => x, StringComparer.CurrentCultureIgnoreCase)
                            .ToArray();
            for (var i = 1; i < pathList.Count(); i++)
            {
                var path = pathList[i];
                var items = path.Split('\\');
                var queueId = int.Parse(items[items.Length - 1]);
                var topic = items[items.Length - 2];
                var queue = new Queue(topic, queueId);
                queue.Load();
                var key = CreateQueueKey(queue.Topic, queue.QueueId);
                _queueDict.TryAdd(key, queue);
            }

            _scheduleService.StartTask("QueueService.RemoveConsumedQueueIndex", RemoveConsumedQueueIndex, BrokerController.Instance.Setting.RemoveConsumedQueueIndexInterval, BrokerController.Instance.Setting.RemoveConsumedQueueIndexInterval);
            _scheduleService.StartTask("QueueService.RemoveExceedMaxCacheQueueIndex", RemoveExceedMaxCacheQueueIndex, BrokerController.Instance.Setting.RemoveExceedMaxCacheQueueIndexInterval, BrokerController.Instance.Setting.RemoveExceedMaxCacheQueueIndexInterval);
        }
        public void Shutdown()
        {
            foreach (var queue in _queueDict.Values)
            {
                queue.Close();
            }
            _queueDict.Clear();
            _scheduleService.StopTask("QueueService.RemoveConsumedQueueIndex");
            _scheduleService.StopTask("QueueService.RemoveExceedMaxCacheQueueIndex");
        }
        public IEnumerable<string> GetAllTopics()
        {
            return _queueDict.Values.Select(x => x.Topic).Distinct();
        }
        public int GetAllQueueCount()
        {
            return _queueDict.Count;
        }
        public long GetAllQueueIndexCount()
        {
            return _queueDict.Values.Sum(x => x.GetMessageCount());
        }
        public long GetAllQueueUnConusmedMessageCount()
        {
            return _queueDict.Values.Sum(x => x.GetMessageRealCount());
        }
        public long GetQueueMinMessageOffset()
        {
            var minMessageOffset = -1L;
            if (_queueDict.Count > 0)
            {
                minMessageOffset = _queueDict.Values.Min(x => x.GetMinQueueOffset());
            }
            return minMessageOffset;
        }
        public bool IsQueueExist(string topic, int queueId)
        {
            var key = CreateQueueKey(topic, queueId);
            return _queueDict.ContainsKey(key);
        }
        public long GetQueueCurrentOffset(string topic, int queueId)
        {
            var key = CreateQueueKey(topic, queueId);
            Queue queue;
            if (_queueDict.TryGetValue(key, out queue))
            {
                return queue.CurrentOffset;
            }
            return -1;
        }
        public long GetQueueMinOffset(string topic, int queueId)
        {
            var key = CreateQueueKey(topic, queueId);
            Queue queue;
            if (_queueDict.TryGetValue(key, out queue))
            {
                return queue.GetMinQueueOffset();
            }
            return -1;
        }
        public void CreateTopic(string topic, int initialQueueCount)
        {
            lock (this)
            {
                Ensure.NotNullOrEmpty(topic, "topic");
                Ensure.Positive(initialQueueCount, "initialQueueCount");
                if (initialQueueCount > BrokerController.Instance.Setting.TopicMaxQueueCount)
                {
                    throw new ArgumentException(string.Format("Initial queue count cannot bigger than {0}.", BrokerController.Instance.Setting.TopicMaxQueueCount));
                }
                var queues = new List<Queue>();
                for (var index = 0; index < initialQueueCount; index++)
                {
                    var queue = new Queue(topic, index);
                    queue.Load();
                    queues.Add(queue);
                }
                foreach (var queue in queues)
                {
                    if (!IsQueueExist(queue.Topic, queue.QueueId))
                    {
                        _queueStore.CreateQueue(queue);
                    }
                    _queueDict.TryAdd(CreateQueueKey(queue.Topic, queue.QueueId), queue);
                }
            };
        }
        public Queue GetQueue(string topic, int queueId)
        {
            var key = CreateQueueKey(topic, queueId);
            Queue queue;
            if (_queueDict.TryGetValue(key, out queue))
            {
                return queue;
            }
            return null;
        }
        public void AddQueue(string topic)
        {
            lock (this)
            {
                Ensure.NotNullOrEmpty(topic, "topic");
                var queues = _queueDict.Values.Where(x => x.Topic == topic);
                if (queues.Count() >= BrokerController.Instance.Setting.TopicMaxQueueCount)
                {
                    throw new ArgumentException(string.Format("Queue count cannot bigger than {0}.", BrokerController.Instance.Setting.TopicMaxQueueCount));
                }
                var queueId = queues.Count() == 0 ? 0 : queues.Max(x => x.QueueId) + 1;
                var queue = new Queue(topic, queueId);
                queue.Load();
                _queueStore.CreateQueue(queue);
                var key = CreateQueueKey(queue.Topic, queue.QueueId);
                _queueDict.TryAdd(key, queue);
            }
        }
        public void RemoveQueue(string topic, int queueId)
        {
            lock (this)
            {
                var key = CreateQueueKey(topic, queueId);
                Queue queue;
                if (!_queueDict.TryGetValue(key, out queue))
                {
                    return;
                }

                //检查队列状态是否是已禁用
                if (queue.Setting.Status != QueueStatus.Disabled)
                {
                    throw new Exception("Queue status is not disabled, cannot be deleted.");
                }
                //检查是否有未消费完的消息
                if (queue.GetMessageRealCount() > 0L)
                {
                    throw new Exception("Queue is not allowed to delete as there are messages exist in this queue.");
                }

                //删除队列消息
                _messageStore.DeleteQueueMessage(topic, queueId);

                //删除队列消费进度信息
                _offsetManager.DeleteQueueOffset(topic, queueId);

                //删除队列
                _queueStore.DeleteQueue(queue);

                //从内存移除队列
                _queueDict.Remove(key);
            }
        }
        public void EnableQueue(string topic, int queueId)
        {
            lock (this)
            {
                var queue = GetQueue(topic, queueId);
                if (queue != null)
                {
                    var foundQueue = _queueStore.GetQueue(topic, queueId);
                    if (foundQueue != null)
                    {
                        foundQueue.Enable();
                        _queueStore.UpdateQueue(foundQueue);
                        queue.Enable();
                    }
                }
            }
        }
        public void DisableQueue(string topic, int queueId)
        {
            lock (this)
            {
                var queue = GetQueue(topic, queueId);
                if (queue != null)
                {
                    var foundQueue = _queueStore.GetQueue(topic, queueId);
                    if (foundQueue != null)
                    {
                        foundQueue.Disable();
                        _queueStore.UpdateQueue(foundQueue);
                        queue.Disable();
                    }
                }
            }
        }
        public IEnumerable<Queue> QueryQueues(string topic)
        {
            return _queueDict.Values.Where(x => x.Topic.Contains(topic));
        }
        public IEnumerable<Queue> GetOrCreateQueues(string topic, QueueStatus? status = null)
        {
            lock (this)
            {
                var queues = _queueDict.Values.Where(x => x.Topic == topic);
                if (queues.IsEmpty() && BrokerController.Instance.Setting.AutoCreateTopic)
                {
                    CreateTopic(topic, BrokerController.Instance.Setting.TopicDefaultQueueCount);
                    queues = _queueDict.Values.Where(x => x.Topic == topic);
                }
                if (status != null)
                {
                    return queues.Where(x => x.Setting.Status == status.Value);
                }
                return queues;
            }
        }
        public IEnumerable<Queue> FindQueues(string topic, QueueStatus? status = null)
        {
            var queues = _queueDict.Values.Where(x => x.Topic == topic);
            if (status != null)
            {
                return queues.Where(x => x.Setting.Status == status.Value);
            }
            return queues;
        }

        private static string CreateQueueKey(string topic, int queueId)
        {
            return string.Format("{0}-{1}", topic, queueId);
        }
        private void RemoveConsumedQueueIndex()
        {
            if (Interlocked.CompareExchange(ref _isRemovingConsumedQueueIndex, 1, 0) == 0)
            {
                try
                {
                    foreach (var queue in _queueDict.Values)
                    {
                        var consumedQueueOffset = _offsetManager.GetMinOffset(queue.Topic, queue.QueueId);
                        if (consumedQueueOffset > queue.CurrentOffset)
                        {
                            consumedQueueOffset = queue.CurrentOffset;
                        }
                        queue.RemoveAllPreviousQueueIndex(consumedQueueOffset);
                        _messageStore.UpdateConsumedQueueOffset(queue.Topic, queue.QueueId, consumedQueueOffset);
                    }
                }
                catch (Exception ex)
                {
                    _logger.Error("Failed to remove consumed queue index.", ex);
                }
                finally
                {
                    Interlocked.Exchange(ref _isRemovingConsumedQueueIndex, 0);
                }
            }
        }
        private void RemoveExceedMaxCacheQueueIndex()
        {
            if (Interlocked.CompareExchange(ref _isRemovingExceedMaxCacheQueueIndex, 1, 0) == 0)
            {
                try
                {
                    if (!_messageStore.SupportBatchLoadQueueIndex)
                    {
                        return;
                    }

                    var exceedCount = GetAllQueueIndexCount() - BrokerController.Instance.Setting.QueueIndexMaxCacheSize;
                    if (exceedCount > 0)
                    {
                        //First we should remove all the consumed queue index from memory.
                        RemoveConsumedQueueIndex();

                        var queueEntryList = new List<KeyValuePair<Queue, long>>();
                        foreach (var queue in _queueDict.Values)
                        {
                            queueEntryList.Add(new KeyValuePair<Queue, long>(queue, queue.GetMessageCount()));
                        }
                        var totalUnConsumedQueueIndexCount = queueEntryList.Sum(x => x.Value);
                        var unconsumedExceedCount = totalUnConsumedQueueIndexCount - BrokerController.Instance.Setting.QueueIndexMaxCacheSize;
                        if (unconsumedExceedCount <= 0)
                        {
                            return;
                        }

                        //If the remaining queue index count still exceed the max queue index cache size, then we try to remove all the exceeded unconsumed queue indexes.
                        var totalRemovedCount = 0L;
                        foreach (var entry in queueEntryList)
                        {
                            var requireRemoveCount = unconsumedExceedCount * entry.Value / totalUnConsumedQueueIndexCount;
                            if (requireRemoveCount > 0)
                            {
                                totalRemovedCount += entry.Key.RemoveRequiredQueueIndexFromLast(requireRemoveCount);
                            }
                        }
                        if (totalRemovedCount > 0)
                        {
                            _logger.InfoFormat("Auto removed {0} unconsumed queue indexes which exceed the max queue cache size, current total unconsumed queue index count:{1}, current exceed count:{2}", totalRemovedCount, totalUnConsumedQueueIndexCount, exceedCount);
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.Error("Failed to remove exceed max cache queue index.", ex);
                }
                finally
                {
                    Interlocked.Exchange(ref _isRemovingExceedMaxCacheQueueIndex, 0);
                }
            }
        }
    }
}
