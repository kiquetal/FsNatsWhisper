namespace FsNatsWhisper

open System
open System.Security.Cryptography

module Crypto =

    let decrypt (key: byte[]) (iv: byte[]) (encryptedData: byte[]) : byte[] =
        // validation
        if key.Length <> 32 then failwith "Key must be 32 bytes (256 bits)"
        if iv.Length <> 12 then failwith "IV must be 12 bytes"
        
        // Assuming the tag is appended to the end of the ciphertext (standard practice in many schemes)
        // Tag size for AES-GCM is typically 16 bytes (128 bits)
        let tagSize = 16
        if encryptedData.Length < tagSize then failwith "Encrypted data is too short to contain a tag"

        let cipherTextSize = encryptedData.Length - tagSize
        let cipherText = encryptedData.[0..cipherTextSize - 1]
        let tag = encryptedData.[cipherTextSize..]
        let plainText = Array.zeroCreate<byte> cipherTextSize

        use aes = new AesGcm(key)
        aes.Decrypt(iv, cipherText, tag, plainText)

        plainText
