module Signer.Configuration

open System
open Amazon.KeyManagementService
open Azure.Identity
open LiteDB
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.DependencyInjection
open Netezos.Keys
open Netezos.Rpc
open Nethereum.Signer
open Nethereum.Web3
open Nichelson
open Signer.Ethereum
open Signer.EventStore
open Signer.IPFS
open Signer.PaymentAddress
open Signer.State.LiteDB
open Signer

[<CLIMutable>]
type EthNodeConfiguration =
    { Endpoint: string
      Confirmations: int }


[<CLIMutable>]
type EthereumConfiguration =
    { InitialLevel: int
      Node: EthNodeConfiguration
      LockingContract: string }

[<CLIMutable>]
type TezosNodeConfiguration =
    { ChainId: string
      Endpoint: string
      Confirmations: int }

type SignerType =
    | AWS = 0
    | Memory = 1
    | Azure = 2

[<CLIMutable>]
type TezosConfiguration =
    { QuorumContract: string
      MinterContract: string
      InitialLevel: int
      Node: TezosNodeConfiguration }

[<CLIMutable>]
type IpfsConfiguration = { Endpoint: string; KeyName: string }

type IServiceCollection with
    member this.AddState(configuration: IConfiguration) =
        let stateFactory (_: IServiceProvider) =
            let liteDbPath = configuration.["LiteDB:Path"]

            let db =
                new LiteDatabase(sprintf "Filename=%s;Connection=direct" liteDbPath)

            new StateLiteDb(db) :> obj


        this.Add(ServiceDescriptor(typeof<StateLiteDb>, stateFactory, ServiceLifetime.Singleton))

        this
            .AddSingleton<EventStoreState>(fun x -> x.GetRequiredService<StateLiteDb>() :> EventStoreState)
            .AddSingleton<IpfsClient>(fun x ->
                let conf =
                    x.GetRequiredService<IpfsConfiguration>()

                IpfsClient(conf.Endpoint))

    member this.AddConfiguration(configuration: IConfiguration) =
        this
            .AddSingleton(configuration
                .GetSection("Tezos")
                .Get<TezosConfiguration>())
            .AddSingleton(configuration
                .GetSection("Ethereum")
                .Get<EthereumConfiguration>())
            .AddSingleton(configuration
                .GetSection("IPFS")
                .Get<IpfsConfiguration>())

    member this.AddWeb3() =
        let web3Factory (s: IServiceProvider) =
            let conf = s.GetService<EthereumConfiguration>()
            Web3(conf.Node.Endpoint) :> obj

        this.Add(ServiceDescriptor(typeof<Web3>, web3Factory, ServiceLifetime.Singleton))
        this

    member this.AddTezosRpc() =
        let tezosRpcFactory (s: IServiceProvider) =
            let conf = s.GetService<TezosConfiguration>()
            new TezosRpc(conf.Node.Endpoint) :> obj

        this.Add(ServiceDescriptor(typeof<TezosRpc>, tezosRpcFactory, ServiceLifetime.Singleton))
        this

    member this.AddTezosSigner(configuration: IConfiguration) =
        let signerType =
            configuration
                .GetSection("Tezos:Signer:Type")
                .Get<SignerType>()

        let createAwsSigner (s: IServiceProvider) =
            let kms =
                s.GetService<IAmazonKeyManagementService>()

            let keyId = configuration.["Tezos:Signer:KeyId"]
            Signer.Tezos.Crypto.awsSigner kms keyId :> obj

        let service =
            match signerType with
            | SignerType.AWS ->
                this.AddAWSService<IAmazonKeyManagementService>()
                |> ignore

                ServiceDescriptor(typeof<TezosSigner>, createAwsSigner, ServiceLifetime.Singleton)
            | SignerType.Azure ->
                let keyId = configuration.["Tezos:Signer:KeyId"]
                let vault = configuration.["Azure:KeyVault"]

                let signer =
                    Signer.Tezos.Crypto.azureSigner (DefaultAzureCredential()) (Uri(vault)) keyId :> obj

                ServiceDescriptor(typeof<TezosSigner>, signer)
            | SignerType.Memory ->
                let key = configuration.["Tezos:Signer:Key"]
                ServiceDescriptor(typeof<TezosSigner>, Signer.Tezos.Crypto.memorySigner (Key.FromBase58 key))
            | v -> failwith (sprintf "Unknown signer type: %A" v)

        this.Add(service)
        this

    member this.AddEthereumSigner(configuration: IConfiguration) =
        let signerType =
            configuration
                .GetSection("Ethereum:Signer:Type")
                .Get<SignerType>()

        let createAwsSigner (s: IServiceProvider) =
            let kms =
                s.GetService<IAmazonKeyManagementService>()

            let keyId = configuration.["Ethereum:Signer:KeyId"]
            Crypto.awsSigner kms keyId :> obj

        let service =
            match signerType with
            | SignerType.AWS ->
                this.AddAWSService<IAmazonKeyManagementService>()
                |> ignore

                ServiceDescriptor(typeof<EthereumSigner>, createAwsSigner, ServiceLifetime.Singleton)
            | SignerType.Azure ->
                let keyId = configuration.["Ethereum:Signer:KeyId"]
                let vault = configuration.["Azure:KeyVault"]

                let signer =
                    Crypto.azureSigner (DefaultAzureCredential()) (Uri(vault)) keyId :> obj

                ServiceDescriptor(typeof<EthereumSigner>, signer)
            | SignerType.Memory ->
                let key =
                    EthECKey(configuration.["Ethereum:Signer:Key"])

                let signer = Signer.Ethereum.Crypto.memorySigner key

                ServiceDescriptor(typeof<EthereumSigner>, signer)
            | v -> failwith (sprintf "Unknown signer type: %A" v)

        this.Add(service)
        this
        
    member this.AddPaymentAddressWorkflow() =
        let createPaymentAddressWorkflow (s: IServiceProvider) =
            let signer =
                s.GetService<TezosSigner>()
            let configuration = s.GetService<TezosConfiguration>()
            let target =
                    { QuorumContract = TezosAddress.FromStringUnsafe configuration.QuorumContract
                      MinterContract = TezosAddress.FromStringUnsafe configuration.MinterContract
                      ChainId = configuration.Node.ChainId }
            PaymentAddress.workflow signer target :> obj
        this.Add(ServiceDescriptor(typeof<ChangePaymentAddressWorkflow>, createPaymentAddressWorkflow, ServiceLifetime.Singleton))
        this

    member this.AddCommonServices(configuration: IConfiguration) =
        this
            .AddConfiguration(configuration)
            .AddState(configuration)
            .AddWeb3()
            .AddTezosRpc()
            .AddTezosSigner(configuration)
            .AddEthereumSigner(configuration)
