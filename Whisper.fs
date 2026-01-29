namespace FsNatsWhisper

open System
open System.IO
open System.Diagnostics
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

    let private convertAudio (inputAudioData: byte[]) =
        async {
            let processStartInfo = ProcessStartInfo()
            processStartInfo.FileName <- "ffmpeg"
            processStartInfo.Arguments <- "-i pipe:0 -f wav -acodec pcm_s16le -ar 16000 -ac 1 pipe:1"
            processStartInfo.RedirectStandardInput <- true
            processStartInfo.RedirectStandardOutput <- true
            processStartInfo.RedirectStandardError <- true
            processStartInfo.UseShellExecute <- false
            processStartInfo.CreateNoWindow <- true

            use process = new Process()
            process.StartInfo <- processStartInfo

            try
                process.Start() |> ignore

                use outputStream = new MemoryStream()

                // Concurrently write to stdin and read from stdout to avoid deadlock
                let writeTask = process.StandardInput.BaseStream.WriteAsync(inputAudioData, 0, inputAudioData.Length)
                let readTask = process.StandardOutput.BaseStream.CopyToAsync(outputStream)

                // Wait for the write to complete, then close the input stream to signal completion
                do! writeTask |> Async.AwaitTask
                process.StandardInput.Close()

                // Wait for the read to complete
                do! readTask |> Async.AwaitTask

                process.WaitForExit()

                let errorOutput = process.StandardError.ReadToEnd()
                if not (String.IsNullOrWhiteSpace(errorOutput)) then
                    printfn "FFmpeg stderr: %s" errorOutput

                return outputStream.ToArray()
            with
            | ex ->
                printfn "Failed to run ffmpeg. Make sure it is installed and in your PATH. Error: %s" ex.Message
                return Array.empty
        }

    let transcribe (audioData: byte[]) =
        async {
            do! ensureModelExists()

            // 1. Convert audio to the required format
            let! wavPcmData = convertAudio audioData

            use factory = WhisperFactory.FromPath(modelFileName)
            let builder = factory.CreateBuilder().WithLanguage("auto")
            use processor = builder.Build()
            
            use ms = new MemoryStream(wavPcmData)
            
            // Using a Task to consume the IAsyncEnumerable manually
            let collectSegments = task {
                let mutable text = ""
                use enumerator = processor.ProcessAsync(ms).GetAsyncEnumerator()
                while! enumerator.MoveNextAsync() do
                    let segment = enumerator.Current
                    text <- text + segment.Text
                
                return text
            }
            
            return! collectSegments |> Async.AwaitTask
        }
