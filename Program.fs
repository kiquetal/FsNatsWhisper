namespace FsNatsWhisper

open System
open System.Text.Json
open NATS.Client.Core
open NATS.Client.JetStream
open NATS.Client.JetStream.Models
open FsNatsWhisper.Domain
open System.Threading.Tasks

module Program =

    let processRequest (nats: INatsConnection) (defaultResultSubject: string) (msg: INatsJSMsg<NatsMemoryOwner<byte>>) =
        task {
            try
                // msg.Data is NatsMemoryOwner<byte>
                let dataSpan = msg.Data.Span
                
                // Deserialize
                let request = JsonSerializer.Deserialize<TranscriptionRequest>(dataSpan)
                
                if box request = null then
                    printfn "Received empty or invalid request."
                else
                    printfn "Received request for Key: %s in Bucket: %s" request.Key request.Bucket
                    
                    // 1. Download
                    let! encryptedBytes = S3.downloadFile request.Bucket request.Key |> Async.StartAsTask
                    printfn "Downloaded %d bytes." encryptedBytes.Length
                    
                    // 2. Decrypt
                    let key = Convert.FromBase64String(request.DecryptionKeyBase64)
                    let iv = Convert.FromBase64String(request.IvBase64)
                    let audioBytes = Crypto.decrypt key iv encryptedBytes
                    printfn "Decrypted audio. Size: %d bytes." audioBytes.Length
                    
                    // 3. Transcribe
                    // transcribe returns Async<string>, convert to Task
                    let! text = Whisper.transcribe audioBytes |> Async.StartAsTask
                    printfn "Transcription: %s" text
                    
                    // 4. Publish Result
                    let result = { OriginalRequest = request; TranscribedText = text }
                    let resultJson = JsonSerializer.Serialize(result)
                    
                    // Reply
                    if not (String.IsNullOrEmpty(msg.ReplyTo)) then
                        do! nats.PublishAsync(msg.ReplyTo, resultJson).AsTask()
                    else
                        do! nats.PublishAsync(defaultResultSubject, resultJson).AsTask()
                        
                    printfn "Result published to %s" (if String.IsNullOrEmpty(msg.ReplyTo) then defaultResultSubject else msg.ReplyTo)
                    
                    // Acknowledge the message
                    do! msg.AckAsync().AsTask()
                
            with ex ->
                printfn "Error processing message: %s" ex.Message
                // Negative acknowledge on error so message can be redelivered
                do! msg.NakAsync().AsTask()
        }

    [<EntryPoint>]
    let main _argv =
        let t = task {
            printfn "Starting FsNatsWhisper Service..."
            
            let natsUrl = Environment.GetEnvironmentVariable("NATS_URL") 
                          |> Option.ofObj 
                          |> Option.defaultValue "nats://localhost:4222"
            
            let natsSubject = Environment.GetEnvironmentVariable("NATS_SUBJECT")
                              |> Option.ofObj
                              |> Option.defaultValue "file.uploads"

            let natsResultSubject = Environment.GetEnvironmentVariable("NATS_RESULT_SUBJECT")
                                    |> Option.ofObj
                                    |> Option.defaultValue "audio.transcription.result"
            
            let streamName = Environment.GetEnvironmentVariable("NATS_STREAM_NAME")
                             |> Option.ofObj
                             |> Option.defaultValue "FILE_UPLOADS"
            
            let consumerName = Environment.GetEnvironmentVariable("NATS_CONSUMER_NAME")
                               |> Option.ofObj
                               |> Option.defaultValue "transcription-worker"

            // Connect to NATS
            let opts = NatsOpts(Url = natsUrl)
            let nats = NatsConnection(opts)
            
            printfn "Connected to NATS at %s" natsUrl

            // Create JetStream context
            let js = NatsJSContextFactory().CreateContext(nats)
            
            printfn "Subscribing to stream '%s' on subject '%s' with consumer '%s'..." streamName natsSubject consumerName
            
            // Create pull consumer configuration with DeliverAll to get existing messages
            let consumerConfig = ConsumerConfig()
            consumerConfig.DurableName <- consumerName
            consumerConfig.AckPolicy <- ConsumerConfigAckPolicy.Explicit
            consumerConfig.DeliverPolicy <- ConsumerConfigDeliverPolicy.All
            consumerConfig.FilterSubject <- natsSubject
            
            // Create or get consumer
            let! consumer = js.CreateOrUpdateConsumerAsync(streamName, consumerConfig)
            
            printfn "Listening for messages (including existing ones)..."
            
            // Consume messages from the consumer
            let consumeEnumerable = consumer.ConsumeAsync<NatsMemoryOwner<byte>>()
            
            // Process messages as they come
            let mutable continueLoop = true
            try
                let enumerator = consumeEnumerable.GetAsyncEnumerator()
                while continueLoop do
                    try
                        let! hasNext = enumerator.MoveNextAsync().AsTask()
                        if hasNext then
                            let msg = enumerator.Current
                            do! processRequest nats natsResultSubject msg
                        else
                            continueLoop <- false
                    with
                    | :? OperationCanceledException ->
                        printfn "Shutting down..."
                        continueLoop <- false
                    | ex ->
                        printfn "Error reading message: %s" ex.Message
            finally
                nats.DisposeAsync().AsTask().Wait()

            return 0
        }
        
        t.Result
