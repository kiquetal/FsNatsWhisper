namespace FsNatsWhisper

open System
open System.Text
open System.Text.Json
open FsNatsWhisper.Domain
open NATS.Client.Core
open NATS.Client.JetStream
open System.Threading.Tasks
open FsNatsWhisper.S3
open FsNatsWhisper.Whisper

module Processing =

    let handleRequest (masterKey: string) (msg: INatsJSMsg<NatsMemoryOwner<byte>>) : Task =
        task {
            try
                // msg.Data is NatsMemoryOwner<byte>
                let dataSpan = msg.Data.Span

                // Log raw message for debugging
                let rawJson = Encoding.UTF8.GetString(dataSpan)
                printfn "Received message: %s" rawJson

                // Deserialize with case-insensitive property names
                let options = JsonSerializerOptions()
                options.PropertyNameCaseInsensitive <- true

                // Double-deserialize: first from the outer JSON string, then from the inner JSON string
                let innerJson = JsonSerializer.Deserialize<string>(rawJson)
                let request = JsonSerializer.Deserialize<FileUploadRequest>(innerJson, options)

                if box request = null then
                    printfn "Received empty or invalid request."
                    do! msg.AckAsync().AsTask() // Acknowledge to avoid reprocessing
                else
                    printfn "Received request for EventId: %s, Key: %s in Bucket: %s" request.EventId request.S3DataKey request.BucketName

                    // 1. Download metadata
                    let! metadataBytes = S3.downloadFile request.BucketName request.S3MetadataKey |> Async.StartAsTask
                    let metadataJson = Encoding.UTF8.GetString(metadataBytes)
                    printfn "Downloaded metadata: %s" metadataJson

                    // Parse metadata
                    let metadata = JsonSerializer.Deserialize<JsonElement>(metadataJson)
                    let encryptedDecryptionKeyBase64 = metadata.GetProperty("decryption_key").GetString()
                    let keyIvBase64 = metadata.GetProperty("key_iv").GetString()
                    let dataIvBase64 = metadata.GetProperty("iv").GetString()

                    // 2. Decrypt the data key using the master key
                    let masterKeyBytes = Convert.FromBase64String(masterKey)
                    let keyIv = Convert.FromBase64String(keyIvBase64)
                    let encryptedDecryptionKey = Convert.FromBase64String(encryptedDecryptionKeyBase64)
                    let dataDecryptionKey = Crypto.decrypt masterKeyBytes keyIv encryptedDecryptionKey
                    printfn "Successfully decrypted data key."

                    // 3. Download encrypted audio file
                    let! encryptedBytes = S3.downloadFile request.BucketName request.S3DataKey |> Async.StartAsTask
                    printfn "Downloaded %d bytes." encryptedBytes.Length

                    // 4. Decrypt the audio file using the decrypted data key
                    let dataIv = Convert.FromBase64String(dataIvBase64)
                    let audioBytes = Crypto.decrypt dataDecryptionKey dataIv encryptedBytes
                    printfn "Decrypted audio. Size: %d bytes." audioBytes.Length

                    // 5. Transcribe
                    // transcribe returns Async<string>, convert to Task
                    let! text = Whisper.transcribe audioBytes |> Async.StartAsTask
                    printfn "Transcription: %s" text


                    // Acknowledge the message
                    do! msg.AckAsync().AsTask()

            with ex ->
                printfn "Error processing message: %s" ex.Message
                printfn "Stack trace: %s" ex.StackTrace
                // Negative acknowledge on error so message can be redelivered
                do! msg.NakAsync().AsTask()
        }
