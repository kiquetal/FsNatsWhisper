namespace FsNatsWhisper

open System
open System.Text.Json
open System.Threading
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
        // Create cancellation token source for graceful shutdown
        use cts = new CancellationTokenSource()
        
        // Handle Ctrl+C and SIGTERM
        Console.CancelKeyPress.Add(fun args ->
            printfn "Shutdown signal received. Gracefully stopping..."
            args.Cancel <- true // Prevent immediate termination
            cts.Cancel()
        )
        
        let t = task {
            try
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
                                   |> Option.defaultValue "file_processor"

                // Connect to NATS
                let opts = NatsOpts(Url = natsUrl)
                let nats = NatsConnection(opts)
                
                try
                    printfn "Connected to NATS at %s" natsUrl

                    // Create JetStream context
                    let js = NatsJSContextFactory().CreateContext(nats)
                    
                    printfn "Subscribing to stream '%s' on subject '%s' with consumer '%s'..." streamName natsSubject consumerName
                    
                    // Try to get existing consumer first
                    let! consumer = 
                        task {
                            try
                                // Try to get existing consumer
                                let! existingConsumer = js.GetConsumerAsync(streamName, consumerName, cts.Token)
                                printfn "Using existing consumer '%s'" consumerName
                                return existingConsumer
                            with
                            | _ ->
                                // Consumer doesn't exist, create a new one
                                printfn "Creating new consumer '%s'" consumerName
                                let consumerConfig = ConsumerConfig()
                                consumerConfig.DurableName <- consumerName
                                consumerConfig.AckPolicy <- ConsumerConfigAckPolicy.Explicit
                                consumerConfig.DeliverPolicy <- ConsumerConfigDeliverPolicy.All
                                consumerConfig.FilterSubject <- natsSubject
                                return! js.CreateConsumerAsync(streamName, consumerConfig, cts.Token)
                        }
                    
                    printfn "Listening for messages (including existing ones)..."
                    
                    // Consume messages from the consumer with cancellation token
                    let consumeEnumerable = consumer.ConsumeAsync<NatsMemoryOwner<byte>>(cancellationToken = cts.Token)
                    
                    // Process messages as they come
                    try
                        let enumerator = consumeEnumerable.GetAsyncEnumerator(cts.Token)
                        try
                            let mutable continueLoop = true
                            while continueLoop && not cts.Token.IsCancellationRequested do
                                try
                                    let! hasNext = enumerator.MoveNextAsync().AsTask()
                                    if hasNext then
                                        let msg = enumerator.Current
                                        do! processRequest nats natsResultSubject msg
                                    else
                                        continueLoop <- false
                                with
                                | :? OperationCanceledException ->
                                    printfn "Shutdown requested, stopping message processing..."
                                    continueLoop <- false
                                | ex ->
                                    printfn "Error reading message: %s" ex.Message
                                    // Continue processing other messages
                        finally
                            // Properly dispose enumerator
                            enumerator.DisposeAsync().AsTask().Wait()
                    with
                    | :? OperationCanceledException ->
                        printfn "Consumer cancelled."
                finally
                    // Gracefully close NATS connection
                    printfn "Closing NATS connection..."
                    nats.DisposeAsync().AsTask().Wait()
                    printfn "NATS connection closed."

                printfn "Service stopped gracefully."
                return 0
            with ex ->
                printfn "Fatal error: %s" ex.Message
                return 1
        }
        
        t.GetAwaiter().GetResult()
