namespace AzureTackle

open System
open Azure.Data.Tables
open System.Threading
open System.Threading.Tasks
open FSharp.Control
open System.Text
open System.Globalization

[<AutoOpen>]
module Filter =

    module Keys =
        let [<Literal>] PartitionKey = "PartitionKey"
        let [<Literal>] RowKey = "RowKey"

    type ColumnComparison =
        | LessThan of obj
        | LessThanOrEqual of obj
        | GreaterThan of obj
        | GreaterThanOrEqual of obj
        | Equal of obj
        | NotEqual of obj

    type BinaryOperation =
        | And
        | Or

    type UnaryOperation = | Not

    type AzureFilter =
        | Empty
        | Column of string * ColumnComparison
        | Binary of AzureFilter * BinaryOperation * AzureFilter
        | Unary of UnaryOperation * AzureFilter
        static member (+)(a, b) = Binary(a, And, b)
        static member (*)(a, b) = Binary(a, Or, b)
        static member (!!) a = Unary(Not, a)

    module private ColumnComparison =
        let comparison =
            function
            | LessThan _ -> "lt"
            | LessThanOrEqual _ -> "le"
            | GreaterThan _ -> "gt"
            | GreaterThanOrEqual _ -> "ge"
            | Equal _ -> "eq"
            | NotEqual _ -> "ne"

        let value =
            function
            | LessThan v -> v
            | LessThanOrEqual v -> v
            | GreaterThan v -> v
            | GreaterThanOrEqual v -> v
            | Equal v -> v
            | NotEqual v -> v

    module private BinaryOperation =
        let operation =
            function
            | And -> "and"
            | Or -> "or"

    module private UnaryOperation =
        let operation =
            function
            | Not -> "not"


    module private StringValue =
        let private formatProvider =
            CultureInfo.InvariantCulture

        let forBinary (value: byte[]) =
            let sb = StringBuilder()

            for num in value do
                sb.AppendFormat("{0:x2}", num :> obj) |> ignore

            String.Format(formatProvider, "X'{0}'", sb.ToString())

        let forBool (value: bool) = if value then "true" else "false"

        let forDateTimeOffset (value: DateTimeOffset) =
            let v =
                value.UtcDateTime.ToString("o", formatProvider)

            sprintf "datetime'%s'" v

        let forDateTime (value: DateTime) =
            DateTimeOffset(value) |> forDateTimeOffset

        let forDouble (value: double) = Convert.ToString(value, formatProvider)

        let forGuid (value: Guid) = sprintf "guid'%s'" (value.ToString())

        let forInt (value: int) = Convert.ToString(value, formatProvider)

        let forLong (value: int64) =
            Convert.ToString(value, formatProvider)
            |> sprintf "%sL"

        let forAny (v: obj) =
            Convert.ToString(v, formatProvider)
            |> sprintf "'%s'"

    let private getColumnComparison field comp =
        let stringValue =
            match ColumnComparison.value comp with
            | :? (byte[]) as v -> v |> StringValue.forBinary
            | :? bool as v -> v |> StringValue.forBool
            | :? DateTime as v -> v |> StringValue.forDateTime
            | :? DateTimeOffset as v -> v |> StringValue.forDateTimeOffset
            | :? double as v -> v |> StringValue.forDouble
            | :? Guid as v -> v |> StringValue.forGuid
            | :? int as v -> v |> StringValue.forInt
            | :? int64 as v -> v |> StringValue.forLong
            | v -> v |> StringValue.forAny

        sprintf "%s %s %s" field (ColumnComparison.comparison comp) stringValue

    let rec private _toQuery (f: AzureFilter) =
        match f with
        | Empty -> ""
        | Column(field, comp) -> getColumnComparison field comp
        | Binary(w1, op, w2) ->
            match _toQuery w1, _toQuery w2 with
            | "", fq
            | fq, "" -> fq
            | fq1, fq2 -> sprintf "(%s) %s %s" fq1 (BinaryOperation.operation op) fq2
        | Unary(op, w) ->
            match _toQuery w with
            | "" -> ""
            | v -> sprintf "%s (%s)" (UnaryOperation.operation op) v

    let toQuery (f: AzureFilter) =
        match f |> _toQuery with
        | "" -> None
        | x -> Some x
    /// Creates FILTER condition for column
    let column name whereComp = AzureFilter.Column(name, whereComp)
        /// FILTER PK equals
    let PartKey (value:string) = column Keys.PartitionKey (Equal value)
    /// FILTER RK equals
    let RowKey (value:string) = column Keys.RowKey (Equal value)
    /// FILTER column value equals to
    let Equal name (o:obj) = column name (Equal o)
    /// FILTER column value not equals to
    let NOT name (o:obj) = column name (NotEqual o)
    /// FILTER column value greater than
    let GreaterThan name (o:obj) = column name (GreaterThan o)
    /// FILTER column value lower than
    let LessThan name (o:obj) = column name (LessThan o)
    /// FILTER column value greater/equals than
    let GreaterThanOrEqual name (o:obj) = column name (GreaterThanOrEqual o)
    /// FILTER column value lower/equals than
    let LessThanOrEqual name (o:obj) = column name (LessThanOrEqual o)

module Table =
    type AzureAccount =
        { TableServiceClient: TableServiceClient }

    type AzureConnection =
        | AzureConnection of string
        | UseTableServiceClient of TableServiceClient
        member this.Connect() =
            match this with
            | AzureConnection connectionString -> { TableServiceClient = TableServiceClient(connectionString) }
            | UseTableServiceClient tableServiceClient -> { TableServiceClient = tableServiceClient }


    // let getTable tableName (azConnection: AzureAccount) =
    //     task {
    //         let client = azConnection.TableServiceClient

    //         let table =
    //             try
    //                 client.GetTableReference tableName
    //             with
    //             | exn ->
    //                 let msg =
    //                     sprintf "Could not get TableReference %s" exn.Message

    //                 failwith msg

    //         return table
    //     }
    //     |> Async.AwaitTask
    //     |> Async.RunSynchronously
    let tablesCreated =
        System.Collections.Concurrent.ConcurrentDictionary<string, TableClient>()

    let getTableClient (tableName) (azConnection: AzureAccount) =
        task {
            match tablesCreated.TryGetValue tableName with
            | true, tableClient -> return tableClient
            | _ ->
                let tableClient =
                    azConnection.TableServiceClient.GetTableClient(tableName)

                let! _ = tableClient.CreateIfNotExistsAsync()

                tablesCreated.TryAdd(tableName, tableClient)
                |> ignore

                return tableClient
        }

    let getAndCreateTable tableName (azConnection: AzureAccount) =
        task {
            let client = azConnection.TableServiceClient

            let table =
                try
                    client.GetTableClient(tableName)
                with exn ->

                    let msg =
                        sprintf "Could not get TableReference %s" exn.Message

                    printfn "Could not get TableReference %s" exn.Message
                    failwith msg
            // Azure will temporarily lock table names after deleting and can take some time before the table name is made available again.
            let mutable finished = false

            while not finished do
                try
                    client.CreateTableIfNotExistsAsync(tableName)
                    |> Async.AwaitTask
                    |> Async.RunSynchronously
                    |> ignore

                    finished <- true
                with _ ->
                    Threading.Thread.Sleep 5000

            return table
        }
        |> Async.AwaitTask
        |> Async.RunSynchronously

    let receiveValue (partKey, rowKey) (table: TableClient) =
        task {
            let! response = table.GetEntityAsync(partKey, rowKey)
            let result = response.Value

            if isNull result then
                return None
            else
                return Some result
        }

    let query (filter: string option) (table: TableClient) token : Task<Azure.Pageable<'a>> =
        task {

            match filter with
            | Some f -> return table.Query<'a>(f, Nullable(1500), [||], token)
            | None -> return table.Query<'a>("", Nullable(1500), [||], token)
        }

    let queryAsync (filter: string option) (table: TableClient) token : Task<Azure.AsyncPageable<'a>> =
        task {

            match filter with
            | Some f -> return table.QueryAsync<'a>(f, Nullable(1500), [||], token)
            | None -> return table.QueryAsync<'a>("", Nullable(1500), [||], token)
        }

[<RequireQualifiedAccess>]
module AzureTable =
    open Table

    type AzureTableConfig =
        { AzureTable: TableClient option
          TableName: string option
          AzureAccount: AzureAccount option }

    type StorageOption =
        { Stage: Stage option
          DevStorage: AzureTableConfig option
          ProdStorage: AzureTableConfig }

    type TableProps =
        { Filter: AzureFilter option
          FilterReceive: (string * string) option
          StorageOption: StorageOption option
          Token: CancellationToken }

    let private defaultAzConfig () =
        { AzureTable = None
          TableName = None
          AzureAccount = None }

    let private defaultProps (token) =
        { Filter = None
          FilterReceive = None
          StorageOption = None
          Token = token }

    let connect (connectionString: string, token) =
        let connection =
            AzureConnection connectionString

        let initAzConfig =
            { defaultAzConfig () with
                AzureAccount = Some(connection.Connect()) }

        let initStorage =
            { Stage = None
              ProdStorage = initAzConfig
              DevStorage = None }

        { defaultProps (token) with
            StorageOption = Some initStorage }

    let connectWithStages (connectionStringProd: string, connectionStringDev: string, stage: Stage, token) =
        let connection =
            AzureConnection connectionStringProd

        let connectionBackUp =
            AzureConnection connectionStringDev

        let initAzConfig =
            { defaultAzConfig () with
                AzureAccount = Some(connection.Connect()) }

        let initAzConfigBackup =
            { defaultAzConfig () with
                AzureAccount = Some(connectionBackUp.Connect()) }

        { defaultProps (token) with
            StorageOption =
                Some
                    { Stage = Some stage
                      ProdStorage = initAzConfig
                      DevStorage = Some initAzConfigDev } }

    let table tableName (props: TableProps) =
        try
            let newStorageOption =
                match props.StorageOption with
                | Some storageOption ->
                    match storageOption.DevStorage with
                    | Some backup ->
                        match backup.AzureAccount, storageOption.ProdStorage.AzureAccount with
                        | Some backUpAcc, Some normalAcc ->

                            { storageOption with
                                DevStorage =
                                    Some
                                        { backup with
                                            AzureTable = Some(getAndCreateTable tableName backUpAcc)
                                            TableName = Some tableName }
                                ProdStorage =
                                    { storageOption.ProdStorage with
                                        AzureTable = Some(getAndCreateTable tableName normalAcc)
                                        TableName = Some tableName } }
                        | _ ->

                            printfn "please use connect to initialize the Azure backup connection"
                            failwith "please use connect to initialize the Azure backup connection"
                    | None ->
                        match storageOption.ProdStorage.AzureAccount with
                        | Some normalAcc ->
                            { storageOption with
                                ProdStorage =
                                    { storageOption.ProdStorage with
                                        AzureTable = Some(getAndCreateTable tableName normalAcc)
                                        TableName = Some tableName } }
                        | _ ->
                            printfn "please use connect to initialize the Azure backup connection"
                            failwith "please use connect to initialize the Azure backup connection"

                | None ->
                    printfn "please use connect to initialize the Azure connection"
                    failwith "please use connect to initialize the Azure connection"

            { props with
                StorageOption = Some newStorageOption }
        with exn ->
            failwithf "Could not get a table %s" exn.Message

    let filter (filter: AzureFilter) (props: TableProps) =

        { props with Filter = Some filter }

    let filterReceive (partKey, rowKey) (props: TableProps) =
        { props with
            FilterReceive = Some(partKey, rowKey) }


    let getTable props =
        let storageOption =
            match props.StorageOption with
            | Some x -> x
            | _ -> failwith "please add a storage account"

        match storageOption.ProdStorage.AzureTable with
        | Some table -> table
        | None -> failwith "please add a table"

    let findDevTable props =
        match props.StorageOption with
        | Some storageOption ->
            match storageOption.DevStorage with
            | Some devStorage ->
                match devStorage.AzureTable with
                | Some table -> Some table
                | None -> None
            | _ -> None
        | _ -> None

    let receive (read: AzureTackleRowEntity -> 't) (props: TableProps) =
        task {
            let storageOption =
                match props.StorageOption with
                | Some x -> x
                | _ -> failwith "please add a storage account"

            let azureTable =
                match storageOption.ProdStorage.AzureTable with
                | Some table -> table
                | None -> failwith "please add a table"

            let keys =
                match props.FilterReceive with
                | Some keys -> keys
                | None -> failwith "please use filterReceive to set a the PartitionKey and RowKey"

            let! result = receiveValue keys azureTable

            return
                result
                |> Option.map AzureTackleRowEntity
                |> Option.map read
        }

    let execute (read: AzureTackleRowEntity -> 't) (props: TableProps) =
        task {
            try
                let azureTable = getTable props

                let applyFilter =
                    match props.Filter with
                    | Some filters -> filters |> toQuery
                    | None -> None

                let! results = query applyFilter azureTable props.Token
                let results = results |> Seq.toList

                return
                    Ok
                        [| for result in results ->
                               let e = AzureTackleRowEntity(result)
                               read e |]
            with exn ->
                return Error exn
        }
    // let executeAsync (read: AzureTackleRowEntity -> 't,pages) (props: TableProps) =
    //     task {
    //         try
    //             let azureTable = getTable props
    //             let filter = appendFilters props.Filters
    //             let! results = queryAsync filter azureTable props.Token
    //             let resultEnum = results.GetAsyncEnumerator()

    //             let! hello = resultEnum.MoveNextAsync().AsTask()

    //             return
    //                 Ok [|
    //                     for result in results ->
    //                         let e = AzureTackleRowEntity(result)
    //                         read e
    //                 |]
    //         with
    //         | exn -> return Error exn
    //     }

    let executeDirect (read: AzureTackleRowEntity -> 't) (props: TableProps) =
        task {
            try
                let azureTable = getTable props
                let applyFilter =
                    match props.Filter with
                    | Some filters -> filters |> toQuery
                    | None -> None

                let! results = query applyFilter azureTable props.Token
                let results = results |> Seq.toList
                return
                    [| for result in results ->
                           let e = AzureTackleRowEntity(result)
                           read e |]
            with
            | exn -> return failwithf "ExecuteDirect failed with exn: %s" exn.Message
        }

    let insert (partKey, rowKey) (set: AzureTackleSetEntity -> TableEntity) (props: TableProps) =
        task {
            try
                let entity =
                    AzureTackleSetEntity(partKey, rowKey)
                    |> set

                match props.StorageOption with
                | Some sOption ->
                    match sOption.Stage with
                    | Some stage ->
                        match stage with
                        | Dev ->
                            match findDevTable props with
                            | Some devTable ->
                                let! _ = devTable.UpsertEntityAsync(entity, TableUpdateMode.Replace, props.Token)
                                return Ok()

                            | _ -> return Ok()

                        | Prod ->
                            let azureTable = getTable props

                            match findDevTable props with
                            | Some devTable ->
                                let! _ = devTable.UpsertEntityAsync(entity, TableUpdateMode.Replace, props.Token)
                                return Ok()
                            | None ->
                                let! _ = azureTable.UpsertEntityAsync(entity, TableUpdateMode.Replace, props.Token)

                                return Ok()
                    | None ->
                        let azureTable = getTable props
                        let! _ = azureTable.UpsertEntityAsync(entity, TableUpdateMode.Replace, props.Token)
                        return Ok()
                | _ ->
                    printfn "please use connect to initialize the Azure connection"
                    return failwith "please use connect to initialize the Azure connection"
            with exn ->
                return Error exn
        }

    let insertBatch (messages: 'a array) (mapper: 'a -> TableEntity) (props: TableProps) =
        task {
            try
                match props.StorageOption with
                | Some sOption ->
                    match sOption.Stage with
                    | Some stage ->
                        match stage with
                        | Dev ->
                            match findDevTable props with
                            | Some devTable ->
                                let actions =
                                    messages
                                    |> Array.map mapper
                                    |> Array.map (fun entity ->
                                        TableTransactionAction(TableTransactionActionType.UpsertReplace, entity))

                                let! _ = devTable.SubmitTransactionAsync(actions)
                                return Ok()

                            | _ -> return Ok()

                        | Prod ->
                            let azureTable = getTable props

                            let actions =
                                messages
                                |> Array.map mapper
                                |> Array.map (fun entity ->
                                    TableTransactionAction(TableTransactionActionType.UpsertReplace, entity))

                            match findDevTable props with
                            | Some devTable ->
                                let! _ = devTable.SubmitTransactionAsync(actions)
                                return Ok()
                            | None ->
                                let! _ = azureTable.SubmitTransactionAsync(actions)
                                return Ok()
                    | None ->

                        let azureTable = getTable props

                        let actions =
                            messages
                            |> Array.map mapper
                            |> Array.map (fun entity ->
                                TableTransactionAction(TableTransactionActionType.UpsertReplace, entity))

                        let! _ = azureTable.SubmitTransactionAsync(actions)
                        return Ok()
                | _ ->
                    printfn "please use connect to initialize the Azure connection"
                    return failwith "please use connect to initialize the Azure connection"
            with exn ->
                return Error exn
        }

    let delete (partKey, rowKey) (props: TableProps) =
        task {
            try
                match props.StorageOption with
                | Some sOption ->
                    match sOption.Stage with
                    | Some stage ->
                        match stage with
                        | Dev ->
                            match findDevTable props with
                            | Some devTable ->
                                let! _ = devTable.DeleteEntityAsync(partKey, rowKey)
                                return Ok()

                            | _ -> return Ok()

                        | Prod ->
                            let azureTable = getTable props

                            match findDevTable props with
                            | Some devTable ->
                                let! _ = devTable.DeleteEntityAsync(partKey, rowKey)
                                return Ok()
                            | None ->
                                let! _ = azureTable.DeleteEntityAsync(partKey, rowKey)

                                return Ok()
                    | None ->
                        let azureTable = getTable props
                        let! _ = azureTable.DeleteEntityAsync(partKey, rowKey)
                        return Ok()
                | _ ->
                    printfn "please use connect to initialize the Azure connection"
                    return failwith "please use connect to initialize the Azure connection"
            with exn ->
                return Error exn
        }

    let deleteBatch (messages: 'a array) (mapper: 'a -> TableEntity) (props: TableProps) =
        task {
            try
                match props.StorageOption with
                | Some sOption ->
                    match sOption.Stage with
                    | Some stage ->
                        match stage with
                        | Dev ->
                            match findDevTable props with
                            | Some devTable ->
                                let actions =
                                    messages
                                    |> Array.map mapper
                                    |> Array.map (fun entity ->
                                        TableTransactionAction(TableTransactionActionType.Delete, entity))

                                let! _ = devTable.SubmitTransactionAsync(actions)
                                return Ok()

                            | _ -> return Ok()

                        | Prod ->
                            let azureTable = getTable props

                            let actions =
                                messages
                                |> Array.map mapper
                                |> Array.map (fun entity ->
                                    TableTransactionAction(TableTransactionActionType.Delete, entity))

                            match findDevTable props with
                            | Some devTable ->
                                let! _ = devTable.SubmitTransactionAsync(actions)
                                return Ok()
                            | None ->
                                let! _ = azureTable.SubmitTransactionAsync(actions)
                                return Ok()
                    | None ->

                        let azureTable = getTable props

                        let actions =
                            messages
                            |> Array.map mapper
                            |> Array.map (fun entity ->
                                TableTransactionAction(TableTransactionActionType.Delete, entity))

                        let! _ = azureTable.SubmitTransactionAsync(actions)
                        return Ok()
                | _ ->
                    printfn "please use connect to initialize the Azure connection"
                    return failwith "please use connect to initialize the Azure connection"
            with exn ->
                return Error exn
        }
