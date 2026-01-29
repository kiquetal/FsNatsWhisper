namespace FsNatsWhisper

open System.IO
open FsNatsWhisper.Whisper

module Test =
    [<EntryPoint>]
    let main argv =
        // --- IMPORTANT ---
        // Change this to the name of the file in your 'downloads' folder.
        let audioFileName = "zeno.mp3.encrypted"
        // ---

        let audioFilePath = Path.Combine("downloads", audioFileName)

        printfn "--- Starting Transcription Test ---"
        printfn "Attempting to transcribe file: %s" audioFilePath

        if not (File.Exists(audioFilePath)) then
            printfn ""
            printfn "!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!"
            printfn "!! ERROR: Test file not found."
            printfn "!! Make sure the file '%s' exists in the 'downloads' folder."
            printfn "!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!"
            1 // Return error code
        else
            try
                let audioBytes = File.ReadAllBytes(audioFilePath)

                let transcriptionTask =
                    async {
                        printfn "File read successfully (%d bytes). Starting transcription..."
                        let! text = transcribe audioBytes
                        printfn ""
                        printfn "--- Transcription Result ---"
                        printfn "%s" text
                        printfn "--------------------------"

                        // Save the transcription to a .txt file
                        let outputFileName = Path.ChangeExtension(audioFileName, ".txt")
                        let outputFilePath = Path.Combine("downloads", outputFileName)
                        File.WriteAllText(outputFilePath, text)
                        printfn "Result saved to: %s" outputFilePath
                    }

                // Run the async workflow and wait for it to complete
                transcriptionTask |> Async.RunSynchronously
                0 // Return success code
            with
            | ex ->
                printfn "An error occurred during transcription: %s" ex.Message
                1
