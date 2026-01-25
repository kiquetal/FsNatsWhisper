namespace FsNatsWhisper

open System
open System.Text.Json
open NATS.Net
open NATS.Client.Core
open FsNatsWhisper.Domain
open System.Threading.Tasks

module Program =

    let processRequest (nats: INatsConnection) (msg: NatsMsg<NatsMemoryOwner<byte>>) =
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
                        do! nats.PublishAsync(msg.ReplyTo, resultJson).AsTask() :> Task
                    else
                        do! nats.PublishAsync("audio.transcription.result", resultJson).AsTask() :> Task
                        
                    printfn "Result published."
                
            with ex ->
                printfn "Error processing message: %s" ex.Message
        }

    [<EntryPoint>]
    let main argv =
        let t = task {
            printfn "Starting FsNatsWhisper Service..."
            
            let url = "nats://localhost:4222"
            
            // Connect to NATS
            let opts = NatsOpts(Url = url)
            use nats = new NatsConnection(opts)
            
            printfn "Connected to NATS at %s" url

            // Subscribe
            let subject = "audio.transcription.request"
            printfn "Subscribing to %s..." subject
            
            // SubscribeCoreAsync returns INatsSub, which is IAsyncDisposable
            // We need to pass the serializer or use default raw bytes
            use! sub = nats.SubscribeCoreAsync<NatsMemoryOwner<byte>>(subject)
            
            printfn "Listening for messages..."
            
            while! sub.Msgs.WaitToReadAsync().AsTask() do
                let mutable msg = Unchecked.defaultof<NatsMsg<NatsMemoryOwner<byte>>>
                while sub.Msgs.TryRead(&msg) do
                    do! processRequest nats msg

            return 0
        }
        
        t.Result
