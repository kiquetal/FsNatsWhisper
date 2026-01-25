namespace FsNatsWhisper

open System
open System.IO
open Whisper.net
open Whisper.net.Ggml

module Whisper =

    let modelFileName = "ggml-tiny.bin"

    let ensureModelExists () =
        async {
                        if not (File.Exists(modelFileName)) then
                            printfn "Downloading Whisper model %s..." modelFileName
                            use client = new System.Net.Http.HttpClient()
                            let downloader = WhisperGgmlDownloader(client)
                            
                            // Get stream from downloader
                            use! modelStream = downloader.GetGgmlModelAsync(GgmlType.Tiny, QuantizationType.NoQuantization, System.Threading.CancellationToken.None) |> Async.AwaitTask
                            
                            // Copy to file
                            use fileStream = File.OpenWrite(modelFileName)
                            do! modelStream.CopyToAsync(fileStream) |> Async.AwaitTask
                            
                            printfn "Model downloaded."
        }

    let transcribe (audioData: byte[]) =
        async {
            do! ensureModelExists()

            use factory = WhisperFactory.FromPath(modelFileName)
            let builder = factory.CreateBuilder().WithLanguage("auto")
            use processor = builder.Build()

            // Converting byte[] (assuming 16-bit PCM, 16kHz, Mono) to float[] or stream
            // If the input is a WAV file, we should skip the header (typically 44 bytes).
            // A robust solution would parse the WAV header. For this example, we'll try to detect/skip simple headers.
            
            let pcmData = 
                // Simple heuristic: if it starts with "RIFF", skip 44 bytes.
                if audioData.Length > 44 && 
                   audioData.[0] = byte 'R' && audioData.[1] = byte 'I' && 
                   audioData.[2] = byte 'F' && audioData.[3] = byte 'F' then
                    // Parse WAV header to be safe, but for now skip 44 bytes standard header
                    use ms = new MemoryStream(audioData)
                    use reader = new BinaryReader(ms)
                    // Skip header
                    ms.Seek(44L, SeekOrigin.Begin) |> ignore
                    reader.ReadBytes(audioData.Length - 44)
                else
                    audioData

            // Whisper.net typically wants 16kHz PCM.
            // We can feed the stream directly.
            
            use ms = new MemoryStream(pcmData)
            
            // Using a Task to consume the IAsyncEnumerable manually
            let collectSegments = task {
                let mutable text = ""
                // 'use!' should handle IAsyncDisposable
                let enumerator = processor.ProcessAsync(ms).GetAsyncEnumerator()
                try
                     while! enumerator.MoveNextAsync() do
                        let segment = enumerator.Current
                        text <- text + segment.Text
                with ex ->
                    // Just explicitly dispose if needed or rely on 'use!' if I can use it.
                    // But 'finally' doesn't support async.
                    // Manual disposal:
                    do! enumerator.DisposeAsync()
                    raise ex
                
                // Success path disposal
                do! enumerator.DisposeAsync()
                return text
            }
            
            return! collectSegments |> Async.AwaitTask
        }
