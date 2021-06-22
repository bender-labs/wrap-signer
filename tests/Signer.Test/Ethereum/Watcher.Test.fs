module Signer.``Ethereum watcher test``

open System
open FSharp.Control
open FsUnit.Xunit
open Nethereum.Web3
open Signer.Ethereum
open Xunit

[<Fact>]
let ``Should watch for transactions failure`` () =
    async {
        let url = Environment.GetEnvironmentVariable("ETH_NODE")
        if String.IsNullOrEmpty(url)
        then
            
            ()
        else
            let web3 =
                Web3(url)

            let! t =
                Watcher.watchForExecutionFailure
                    web3
                    { Contract = "0x5Dc76fD132354be5567ad617fD1fE8fB79421D82"
                      From = 2647927I
                      Confirmations = 0 }
                |> AsyncSeq.take 2
                |> AsyncSeq.toListAsync

            t |> Seq.length |> should equal 2
    }
