﻿using NATS.Client.Serializers.Json;
using NATS.Client.Core;
using NATS.Client.JetStream;
using NATS.Client.ObjectStore;

var semaphore = new SemaphoreSlim(1, 1);

await using var connection = new NatsConnection();

var jsContext = new NatsJSContext(connection);
var objectStoreContext = new NatsObjContext(jsContext);

// Create an object store named votes
var voteStore = await objectStoreContext.CreateObjectStoreAsync("votes");

// Create two receivers for vote.save to demonstrate load balancing between multiple instances
var voteResponder1 = VoteResponder("vote.save", "group1", voteStore, 1);
var voteResponder2 = VoteResponder("vote.save", "group1", voteStore, 2);

// Receiver for vote.get
var voteGetResponder = Task.Run(async () =>
{
    await foreach (var msg in connection.SubscribeAsync<string>("vote.get"))
    {
        Console.WriteLine("Received candidate fetch request");
        var candidateVotes = new Dictionary<string, int>();

        // Fetch the votes from the vote store
        await foreach (var item in voteStore.ListAsync())
        {
            candidateVotes.Add($"Candidate-{Convert.ToInt32(item.Name)}",
                BitConverter.ToInt32(await voteStore.GetBytesAsync(item.Name)));
        }

        // Serialize the candidate votes to JSON using NatsJsonContextSerializer
        await msg.ReplyAsync(candidateVotes,
            serializer: NatsJsonSerializer<Dictionary<string, int>>.Default);
        Console.WriteLine("Request processed");
    }
});

Console.WriteLine("Vote Processor Service is ready.");
await Task.WhenAll(voteResponder1, voteResponder2, voteGetResponder);
return;

Task VoteResponder(string subject, string queue, INatsObjStore objectStore, int consumerId)
{
    var task = Task.Run(async () =>
    {
        await foreach (var msg in connection.SubscribeAsync<int>(subject, queue))
        {
            var candidateId = msg.Data;
            Console.WriteLine($"Processor {consumerId}: Storing vote for candidate: {candidateId}");

            try
            {
                // Acquire lock to ensure thread safety when updating the vote count
                await semaphore.WaitAsync();

                // Increment the vote count for the candidate
                var dataBytes = await objectStore.GetBytesAsync(candidateId.ToString());
                var voteCount = BitConverter.ToInt32(dataBytes);
                voteCount++;
                await objectStore.PutAsync(candidateId.ToString(), BitConverter.GetBytes(voteCount));
            }
            catch (NatsObjNotFoundException)
            {
                // If candidate record does not exist in the store, create it
                await objectStore.PutAsync(candidateId.ToString(), BitConverter.GetBytes(1));
            }
            finally
            {
                semaphore.Release();
            }

            Console.WriteLine($"Processor {consumerId}: Vote saved");
        }
    });

    return task;
}