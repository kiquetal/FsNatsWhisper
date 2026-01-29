namespace FsNatsWhisper

open System
open System.Text
open System.Text.Json
open System.Threading
open FsNatsWhisper.Domain
open NATS.Client.Core
open NATS.Client.JetStream
open System.Threading.Tasks
open FsNatsWhisper.S3
open System.IO

module Processing =

    let private findProjectRoot (startDir: string) =
        let rec find dir =
            if File.Exists(Path.Combine(dir, "FsNatsWhisper.fsproj")) then
                dir
            else
                let parent = Directory.GetParent(dir)
                if parent = null then
                    failwith "Could not find project root containing FsNatsWhisper.fsproj"
                else
                    find parent.FullName
        find startDir

    let startHeartbeat (msg: INatsJSMsg<NatsMemoryOwner<byte>>) (cancellationToken: CancellationToken) : Task =
        // Start a background task that sends InProgress signals every 10 seconds
        Task.Run(Func<Task>(fun () ->
            task {
                try
                    while not cancellationToken.IsCancellationRequested do
                        do! Task.Delay(TimeSpan.FromSeconds(10.0), cancellationToken)
                        if not cancellationToken.IsCancellationRequested then
                            do! msg.AckProgressAsync(Nullable(), cancellationToken)
                            printfn "Sent heartbeat (AckProgress) to NATS"
                with
                | :? OperationCanceledException ->
                    // Normal cancellation, ignore
                    printfn "Heartbeat cancelled"
                | ex ->
                    printfn "Error in heartbeat: %s" ex.Message
            }
        ), cancellationToken)

    let downloadAndDecrypt (masterKey: string) (request: FileUploadRequest) : Async<byte[]> =
        async {
            printfn "Processing EventId: %s, Key: %s in Bucket: %s" request.EventId request.S3DataKey request.BucketName

            // 1. Download metadata
            let! metadataBytes = S3.downloadFile request.BucketName request.S3MetadataKey
            let metadataJson = Encoding.UTF8.GetString(metadataBytes)
            printfn "Downloaded metadata: %s" metadataJson

            // Parse metadata
            let options = JsonSerializerOptions()
            options.PropertyNameCaseInsensitive <- true
            let metadata = JsonSerializer.Deserialize<Metadata>(metadataJson, options)
            
            printfn "Metadata - Version: %s, Algorithm: %s, Original file: %s" metadata.Version metadata.Algorithm metadata.OriginalFilename

            // 2. Decrypt the KEK (Key Encryption Key) using the master key
            // The KEK in the metadata is base64 encoded and likely encrypted
            let masterKeyBytes = Convert.FromBase64String(masterKey)
            let kekEncryptedBytes = Convert.FromBase64String(metadata.Kek)
            
            printfn "Master key length: %d bytes" masterKeyBytes.Length
            printfn "Encrypted KEK length: %d bytes" kekEncryptedBytes.Length
            
            // The KEK should be encrypted with the master key
            // Extract IV from the encrypted KEK (first 12 bytes for AES-GCM)
            if kekEncryptedBytes.Length < 12 then
                failwith "Encrypted KEK is too short to contain IV"
            
            let kekIv = kekEncryptedBytes.[0..11]
            let kekCiphertext = kekEncryptedBytes.[12..]
            
            printfn "Decrypting KEK using master key..."
            let kekBytes = Crypto.decrypt masterKeyBytes kekIv kekCiphertext
            printfn "Decrypted KEK length: %d bytes" kekBytes.Length

            // 3. Download encrypted audio file
            let! encryptedBytes = S3.downloadFile request.BucketName request.S3DataKey
            printfn "Downloaded %d bytes (expected encrypted size: %d)." encryptedBytes.Length metadata.EncryptedSize

            // 4. Decrypt the audio file using the KEK
            // Note: The current metadata structure doesn't include IV information
            // This needs to be adjusted based on how the encryption was actually performed
            // For now, we'll extract the IV from the encrypted data if it follows standard patterns
            
            // Assuming AES-GCM with 12-byte IV prepended to the ciphertext
            if encryptedBytes.Length < 12 then 
                failwith "Encrypted data is too short to contain IV"
            
            let iv = encryptedBytes.[0..11]
            let ciphertext = encryptedBytes.[12..]
            
            let audioBytes = Crypto.decrypt kekBytes iv ciphertext
            printfn"Decrypted audio. Size: %d bytes (expected original size: %d)." audioBytes.Length metadata.OriginalSize

            // Save the decrypted file for debugging
            let projectRoot = findProjectRoot(AppContext.BaseDirectory)
            let downloadsFolder = Path.Combine(projectRoot, "downloads")
            Directory.CreateDirectory(downloadsFolder) |> ignore
            let safeFilename = Path.GetFileName(metadata.OriginalFilename) // Sanitize filename
            let outputPath = Path.Combine(downloadsFolder, safeFilename)
            File.WriteAllBytes(outputPath, audioBytes)
            printfn "Saved decrypted file to: %s" outputPath

            return audioBytes
        }

    let handleRequest (masterKey: string) (msg: INatsJSMsg<NatsMemoryOwner<byte>>) : Task =
        task {
            // Create a cancellation token for the heartbeat
            use heartbeatCts = new CancellationTokenSource()
            
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
                    // Start heartbeat to keep NATS from timing out during long operations
                    printfn "Starting heartbeat for EventId: %s" request.EventId
                    let _heartbeatTask = startHeartbeat msg heartbeatCts.Token
                    
                    try
                        // Download metadata and decrypt the file
                        let! audioBytes = downloadAndDecrypt masterKey request |> Async.StartAsTask
                        
                        // TODO: Add transcription logic here later
                        
                        // Acknowledge the message
                        printfn "Processing complete for EventId: %s, acknowledging message" request.EventId
                        do! msg.AckAsync().AsTask()
                    finally
                        // Stop the heartbeat
                        heartbeatCts.Cancel()

            with ex ->
                printfn "Error processing message: %s" ex.Message
                printfn "Stack trace: %s" ex.StackTrace
                // Stop the heartbeat
                heartbeatCts.Cancel()
                // Negative acknowledge on error so message can be redelivered
                do! msg.NakAsync().AsTask()
        }
