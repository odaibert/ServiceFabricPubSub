﻿using System;
using System.Collections.Generic;
using System.Fabric;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.ServiceFabric.Data.Collections;
using Microsoft.ServiceFabric.Services.Communication.Runtime;
using Microsoft.ServiceFabric.Services.Runtime;
using PubSubDotnetSDK;
using Microsoft.ServiceFabric.Services.Remoting.Runtime;
using Microsoft.ServiceFabric.Data;
using Microsoft.ServiceFabric.Data.Notifications;

namespace TopicService
{
    /// <summary>
    /// An instance of this class is created for each service replica by the Service Fabric runtime.
    /// </summary>
    internal sealed class TopicService : StatefulService, ITopicService
    {
        public TopicService(StatefulServiceContext context)
            : base(context)
        { }



        /// <summary>
        /// Optional override to create listeners (e.g., HTTP, Service Remoting, WCF, etc.) for this service replica to handle client or user requests.
        /// </summary>
        /// <remarks>
        /// For more information on service communication, see https://aka.ms/servicefabricservicecommunication
        /// </remarks>
        /// <returns>A collection of listeners.</returns>
        protected override IEnumerable<ServiceReplicaListener> CreateServiceReplicaListeners()
        {
            return new List<ServiceReplicaListener>()
            {
                new ServiceReplicaListener( (context) => this.CreateServiceRemotingListener(context) )
            };
        }




        /// <summary>
        /// This is the main entry point for your service replica.
        /// This method executes when this replica of your service becomes primary and has write status.
        /// </summary>
        /// <param name="cancellationToken">Canceled when Service Fabric needs to shut down this service replica.</param>
        protected override async Task RunAsync(CancellationToken cancellationToken)
        {
            this.StateManager.StateManagerChanged += StateManager_StateManagerChanged;
            this.StateManager.TransactionChanged += StateManager_TransactionChanged;
            int count = 1;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
                await Push(new PubSubMessage() { Message = $"TEST  Message #{count++} : {DateTime.Now}" }).ConfigureAwait(false); // HACK FOR TEST
            }
        }

        private void StateManager_TransactionChanged(object sender, NotifyTransactionChangedEventArgs e)
        {
            // transaction commited
            if (e.Action == NotifyTransactionChangedAction.Commit)
            {
                Task.Run(() => DuplicateMessages(CancellationToken.None));
            }
        }

        private void StateManager_StateManagerChanged(object sender, NotifyStateManagerChangedEventArgs e)
        {
            // state manager created
            if (e.Action == NotifyStateManagerChangedAction.Add)
            {
                Task.Run(() => DuplicateMessages(CancellationToken.None));
            }            
        }

        private async Task DuplicateMessages(CancellationToken cancellationToken)
        {
            // get input q
            var inputQueue = await this.StateManager.GetOrAddAsync<IReliableConcurrentQueue<PubSubMessage>>("inputQueue");            

            var lst = await this.StateManager.GetOrAddAsync<IReliableDictionary<string, bool>>("queueList");

            using (var tx = this.StateManager.CreateTransaction())
            {
                // enq 1 message
                while (inputQueue.Count > 0)
                {
                    var msg = await inputQueue.TryDequeueAsync(tx).ConfigureAwait(false);
                    if (!msg.HasValue) return;
                    IAsyncEnumerable<KeyValuePair<string, bool>> asyncEnumerable = await lst.CreateEnumerableAsync(tx).ConfigureAwait(false);
                    using (IAsyncEnumerator<KeyValuePair<string, bool>> asyncEnumerator = asyncEnumerable.GetAsyncEnumerator())
                    {
                        while (await asyncEnumerator.MoveNextAsync(CancellationToken.None).ConfigureAwait(false))
                        {
                            var queue = await this.StateManager.GetOrAddAsync<IReliableQueue<PubSubMessage>>(asyncEnumerator.Current.Key);
                            await queue.EnqueueAsync(tx, msg.Value).ConfigureAwait(false);
                        }
                        ServiceEventSource.Current.ServiceMessage(this.Context, $"ENQUEUE: {msg.Value.Message} into {asyncEnumerator.Current.Key}");
                    }
                }                
                await tx.CommitAsync().ConfigureAwait(false);
                
            }
        }

        /// <summary>
        /// Create a new outputqueue for a new subscriber instance
        /// </summary>
        /// <param name="subscriberId"></param>
        /// <returns></returns>
        public async Task RegisterSubscriber(string subscriberId)
        {
            var queueName = $"queue_{subscriberId}";

            var lst = await this.StateManager.GetOrAddAsync<IReliableDictionary<string, bool>>("queueList").ConfigureAwait(false);
            using (var tx = this.StateManager.CreateTransaction())
            {
                if (!await lst.ContainsKeyAsync(tx, queueName).ConfigureAwait(false))
                {
                    await lst.AddAsync(tx, queueName, true).ConfigureAwait(false);
                }
                await tx.CommitAsync().ConfigureAwait(false);
            }

        }


        /// <summary>
        /// Enqueue a new message in the topic
        /// </summary>
        /// <param name="msg"></param>
        /// <returns></returns>
        public async Task Push(PubSubMessage msg)
        {
            var lst = await this.StateManager.GetOrAddAsync<IReliableDictionary<string, bool>>("queueList").ConfigureAwait(false);
            var inputQueue = await this.StateManager.GetOrAddAsync<IReliableConcurrentQueue<PubSubMessage>>("inputQueue");
            using (var tx = this.StateManager.CreateTransaction())
            {
                await inputQueue.EnqueueAsync(tx, msg).ConfigureAwait(false);
                await tx.CommitAsync().ConfigureAwait(false);
            }
            ServiceEventSource.Current.ServiceMessage(this.Context, $"INPUT QUEUE: {msg.Message}");
            
        }

        /// <summary>
        /// HACK Method for sprint0. 
        /// Should be removed in next sprint.
        /// </summary>
        /// <param name="subcriberId"></param>
        /// <returns></returns>
        public async Task<PubSubMessage> InternalPop(string subscriberId)
        {
            var queueName = $"queue_{subscriberId}";
            var q = await this.StateManager.GetOrAddAsync<IReliableQueue<PubSubMessage>>(queueName).ConfigureAwait(false);

            var lst = await this.StateManager.GetOrAddAsync<IReliableDictionary<string, bool>>("queueList").ConfigureAwait(false);


            PubSubMessage msg = null;
            using (var tx = this.StateManager.CreateTransaction())
            {
                if (!await lst.ContainsKeyAsync(tx,queueName).ConfigureAwait(false))
                {
                    await lst.AddAsync(tx, queueName,true).ConfigureAwait(false);
                }

                var msgCV= await q.TryDequeueAsync(tx).ConfigureAwait(false);
                if (msgCV.HasValue)
                    msg = msgCV.Value;
                await tx.CommitAsync().ConfigureAwait(false);
            }
            ServiceEventSource.Current.ServiceMessage(this.Context, $"DEQUEUE FOR {subscriberId} : {msg?.Message}");
            return msg;
        }
    }
}
